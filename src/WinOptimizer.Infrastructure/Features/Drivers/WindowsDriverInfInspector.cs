using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using WinOptimizer.Core.Features.Drivers.Models;

namespace WinOptimizer.Infrastructure.Features.Drivers;

/// <summary>Inspects a package INF only when its model sections match the offered hardware identifier.</summary>
public interface IDriverInfInspector
{
    Task<DriverComponent[]> InspectAsync(string root, string[] paths, CancellationToken ct);
}

/// <summary>Uses SetupAPI's INF parser and signer scores; an ordinary or unclassified signature is not WHQL.</summary>
public sealed class WindowsDriverInfInspector(DriverPowerShell shell) : IDriverInfInspector
{
    private sealed record Signature(string Path, string Catalog, string Publisher, uint Score, bool Valid, uint Error);
    private sealed record Signatures(Signature[] Items);

    /// <summary>Verifies the package's catalogs in one worker that releases native file handles on exit.</summary>
    public async Task<DriverComponent[]> InspectAsync(string root, string[] paths, CancellationToken ct)
    {
        if (paths.Length == 0) return [];
        foreach (var path in paths) DriverScanWorkspace.ValidateChild(root, path);
        var request = DriverScanWorkspace.ValidateChild(root, Path.Combine(root, $"inf-signatures-{Guid.NewGuid():N}.json"));
        var encodedPath = Convert.ToBase64String(Encoding.UTF8.GetBytes(request));
        try
        {
            await OfficialDriverHttp.WriteAtomicAsync(request, (output, token) =>
                JsonSerializer.SerializeAsync(output, paths, cancellationToken: token), ct).ConfigureAwait(false);
            var prelude = $"$request=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{encodedPath}'));";
            var result = await shell.RunAsync<Signatures>("DriverSignatures", prelude, ct).ConfigureAwait(false);
            if (result.Items is null || result.Items.Length != paths.Length || result.Items.Where((item, index) =>
                    item is null || !string.Equals(item.Path, paths[index], StringComparison.OrdinalIgnoreCase)).Any())
                throw new DriverScanException("Windows returned incomplete driver catalog signature evidence.");
            return await Task.Run(() =>
            {
                var components = new DriverComponent[paths.Length];
                for (var index = 0; index < paths.Length; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    components[index] = ReadComponent(paths[index], root, result.Items[index]);
                }
                return components;
            }, ct).ConfigureAwait(false);
        }
        finally { File.Delete(request); }
    }

    /// <summary>Reads the models Windows selects for the running OS and the catalog bound to this INF.</summary>
    private static DriverComponent ReadComponent(string infPath, string root, Signature signer)
    {
        using var inf = SetupOpenInfFile(infPath, null, 2, out _); // INF_STYLE_WIN4
        if (inf.IsInvalid) throw new IOException($"Cannot read driver INF: {infPath}", new Win32Exception(Marshal.GetLastWin32Error()));
        var sections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (SetupFindFirstLine(inf, "Manufacturer", null, out var manufacturer))
        {
            do
            {
                var name = new StringBuilder(256);
                if (SetupDiGetActualModelsSection(ref manufacturer, IntPtr.Zero, name, (uint)name.Capacity, out _, IntPtr.Zero))
                    sections.Add(name.ToString());
            } while (SetupFindNextLine(ref manufacturer, out manufacturer));
        }
        var (version, date) = ReadDriverVer(inf, "Version") ?? ("", "");
        var installSections = sections.SelectMany(section => ReadLines(inf, section)).Where(fields => fields.Length > 1)
            .GroupBy(fields => fields[0], StringComparer.OrdinalIgnoreCase).Select(models =>
            {
                var install = ActualInstallSection(inf, models.Key);
                var (effectiveVersion, effectiveDate) = ReadDriverVer(inf, install) ?? (version, date);
                var ids = models.SelectMany(fields => fields.Skip(1).Select((id, index) =>
                        new DriverIdentifier(id, index == 0 ? DriverIdentifierKind.Hardware : DriverIdentifierKind.Compatible, index)))
                    .Where(id => !string.IsNullOrWhiteSpace(id.Value)).Distinct().ToArray();
                return new DriverInstallSection(install, effectiveVersion, effectiveDate, ids);
            }).ToArray();
        var identifiers = installSections.SelectMany(section => section.Identifiers).Distinct().ToArray();
        var verified = HasTrustedSignature(signer.Valid, signer.Error);
        var architecture = RuntimeInformation.OSArchitecture switch
        { Architecture.X64 => ".NTamd64", Architecture.Arm64 => ".NTarm64", Architecture.X86 => ".NTx86", _ => "" };
        var catalogName = VersionField(inf, "CatalogFile" + architecture);
        if (catalogName.Length == 0) catalogName = VersionField(inf, "CatalogFile.NT");
        if (catalogName.Length == 0) catalogName = VersionField(inf, "CatalogFile");
        var catalogHash = "";
        if (catalogName.Length > 0 && Path.GetFileName(catalogName) == catalogName &&
            Path.GetExtension(catalogName).Equals(".cat", StringComparison.OrdinalIgnoreCase))
        {
            var catalogPath = DriverScanWorkspace.ValidateChild(root, Path.Combine(Path.GetDirectoryName(infPath)!, catalogName));
            if (File.Exists(catalogPath)) catalogHash = HashFile(catalogPath);
            verified = verified && string.Equals(Path.GetFileName(signer.Catalog), catalogName, StringComparison.OrdinalIgnoreCase) &&
                (!Path.IsPathFullyQualified(signer.Catalog ?? "") || string.Equals(Path.GetFullPath(signer.Catalog!), catalogPath, StringComparison.OrdinalIgnoreCase));
        }
        // SIGNERSCORE_LOGO_PREMIUM, SIGNERSCORE_LOGO_STANDARD and SIGNERSCORE_WHQL.
        // INBOX (3) and UNCLASSIFIED (4) do not establish that a package passed WHQL testing.
        verified = verified && catalogHash.Length > 0;
        var certification = verified && signer.Valid && IsWhqlScore(signer.Score)
            ? DriverCertification.Whql : DriverCertification.Unverified;
        return new(Path.GetRelativePath(root, infPath), VersionField(inf, "Class"), VersionField(inf, "Provider"), version,
            date, identifiers, sections.ToArray(), HashFile(infPath), catalogHash, signer.Publisher, verified, certification)
        { Catalog = catalogName, ExtensionId = VersionField(inf, "ExtensionId"), InstallSections = installSections };
    }

    // ERROR_AUTHENTICODE_TRUSTED_PUBLISHER indicates a valid signature from the Trusted Publishers store.
    internal static bool HasTrustedSignature(bool setupVerified, uint error) => setupVerified || error == 0xE0000241u;

    internal static bool IsWhqlScore(uint score) => score is 0x0D000001 or 0x0D000002 or 0x0D000005;

    /// <summary>Resolves the locally applicable DDInstall decoration using the same parser as Models sections.</summary>
    private static string ActualInstallSection(InfHandle inf, string section)
    {
        var actual = new StringBuilder(256);
        if (!SetupDiGetActualSectionToInstall(inf, section, actual, (uint)actual.Capacity, out _, IntPtr.Zero))
            throw new IOException($"Cannot resolve the driver INF install section '{section}'.", new Win32Exception(Marshal.GetLastWin32Error()));
        return actual.ToString();
    }

    /// <summary>Reads an explicit DriverVer directive, leaving inheritance to the caller.</summary>
    private static (string Version, string Date)? ReadDriverVer(InfHandle inf, string section)
    {
        if (!SetupFindFirstLine(inf, section, "DriverVer", out var line)) return null;
        var count = SetupGetFieldCount(ref line);
        var date = count >= 1 && DateOnly.TryParse(ReadField(ref line, 1), CultureInfo.InvariantCulture, out var parsed)
            ? parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
        return (count >= 2 ? ReadField(ref line, 2) : "", date);
    }

    private static string HashFile(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    private static string VersionField(InfHandle inf, string key) =>
        SetupFindFirstLine(inf, "Version", key, out var line) ? ReadField(ref line, 1) : "";

    /// <summary>Reads parsed INF fields through SetupAPI, including INF string substitutions.</summary>
    private static IEnumerable<string[]> ReadLines(InfHandle inf, string section)
    {
        if (!SetupFindFirstLine(inf, section, null, out var context)) yield break;
        do
        {
            var count = SetupGetFieldCount(ref context);
            var fields = new string[count];
            for (uint index = 0; index < count; index++) fields[index] = ReadField(ref context, index + 1);
            yield return fields;
        } while (SetupFindNextLine(ref context, out context));
    }

    private static string ReadField(ref InfContext context, uint index)
    {
        if (!SetupGetStringField(ref context, index, null, 0, out var required) && Marshal.GetLastWin32Error() != 122)
            throw new IOException("Cannot determine a driver INF field size.", new Win32Exception(Marshal.GetLastWin32Error()));
        var buffer = new StringBuilder(checked((int)required));
        if (!SetupGetStringField(ref context, index, buffer, (uint)buffer.Capacity, out _))
            throw new IOException("Cannot read a driver INF field.", new Win32Exception(Marshal.GetLastWin32Error()));
        return buffer.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InfContext { public IntPtr Inf; public IntPtr CurrentInf; public uint Section; public uint Line; }

    private sealed class InfHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public InfHandle() : base(true) { }
        protected override bool ReleaseHandle() { SetupCloseInfFile(handle); return true; }
    }

    [DllImport("setupapi.dll", EntryPoint = "SetupOpenInfFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern InfHandle SetupOpenInfFile(string path, string? infClass, uint style, out uint errorLine);
    [DllImport("setupapi.dll")]
    private static extern void SetupCloseInfFile(IntPtr inf);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetActualSectionToInstallW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetActualSectionToInstall(InfHandle inf,
        string sectionName, StringBuilder section, uint sectionSize, out uint required, IntPtr extension);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetActualModelsSectionW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetActualModelsSection(ref InfContext manufacturer,
        IntPtr platform, StringBuilder section, uint sectionSize, out uint required, IntPtr reserved);
    [DllImport("setupapi.dll", EntryPoint = "SetupFindFirstLineW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupFindFirstLine(InfHandle inf, string section, string? key, out InfContext context);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupFindNextLine(ref InfContext context, out InfContext next);
    [DllImport("setupapi.dll")]
    private static extern uint SetupGetFieldCount(ref InfContext context);
    [DllImport("setupapi.dll", EntryPoint = "SetupGetStringFieldW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupGetStringField(ref InfContext context, uint field, StringBuilder? buffer, uint size, out uint required);
}
