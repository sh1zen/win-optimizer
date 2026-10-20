using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WinOptimizer.Core.Features.Drivers.Models;

namespace WinOptimizer.Infrastructure.Features.Drivers;

public sealed record DriverInfInspection(string Version, DriverCertification Certification);

/// <summary>Inspects a package INF only when its model sections match the offered hardware identifier.</summary>
public interface IDriverInfInspector
{
    DriverInfInspection? Inspect(string infPath, string hardwareId);
}

/// <summary>Uses SetupAPI's INF parser and signer scores; an ordinary or unclassified signature is not WHQL.</summary>
public sealed class WindowsDriverInfInspector : IDriverInfInspector
{
    public DriverInfInspection? Inspect(string infPath, string hardwareId)
    {
        if (string.IsNullOrWhiteSpace(hardwareId)) return null;
        using var inf = SetupOpenInfFile(infPath, null, 2, out _); // INF_STYLE_WIN4
        if (inf.IsInvalid) throw new IOException($"Cannot read driver INF: {infPath}", new Win32Exception(Marshal.GetLastWin32Error()));
        var architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "NTamd64", Architecture.Arm64 => "NTarm64", Architecture.X86 => "NTx86",
            _ => throw new IOException("Unsupported driver platform.")
        };
        var sections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var manufacturer in ReadLines(inf, "Manufacturer"))
        {
            if (manufacturer.Length < 1) continue;
            if (manufacturer.Length == 1) sections.Add(manufacturer[0]);
            foreach (var decoration in manufacturer.Skip(1))
                if (decoration.Equals("NT", StringComparison.OrdinalIgnoreCase) || decoration.StartsWith("NT.", StringComparison.OrdinalIgnoreCase) ||
                    decoration.Equals(architecture, StringComparison.OrdinalIgnoreCase) || decoration.StartsWith(architecture + ".", StringComparison.OrdinalIgnoreCase))
                    sections.Add(manufacturer[0] + "." + decoration);
        }
        if (!sections.SelectMany(section => ReadLines(inf, section)).Any(fields => fields.Skip(1).Contains(hardwareId, StringComparer.OrdinalIgnoreCase))) return null;
        var version = "";
        if (SetupFindFirstLine(inf, "Version", "DriverVer", out var versionLine)) version = ReadField(ref versionLine, 2);
        var signer = new InfSignerInfo { Size = (uint)Marshal.SizeOf<InfSignerInfo>() };
        var verified = SetupVerifyInfFile(infPath, IntPtr.Zero, ref signer);
        // SIGNERSCORE_LOGO_PREMIUM, SIGNERSCORE_LOGO_STANDARD and SIGNERSCORE_WHQL.
        // INBOX (3) and UNCLASSIFIED (4) do not establish that a package passed WHQL testing.
        var certification = verified && IsWhqlScore(signer.Score) ? DriverCertification.Whql : DriverCertification.Unverified;
        return new(version, certification);
    }

    internal static bool IsWhqlScore(uint score) => score is 0x0D000001 or 0x0D000002 or 0x0D000005;

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct InfSignerInfo
    {
        public uint Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string? Catalog;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string? Signer;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string? SignerVersion;
        public uint Score;
    }

    private sealed class InfHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public InfHandle() : base(true) { }
        protected override bool ReleaseHandle() { SetupCloseInfFile(handle); return true; }
    }

    [DllImport("setupapi.dll", EntryPoint = "SetupOpenInfFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern InfHandle SetupOpenInfFile(string path, string? infClass, uint style, out uint errorLine);
    [DllImport("setupapi.dll")]
    private static extern void SetupCloseInfFile(IntPtr inf);
    [DllImport("setupapi.dll", EntryPoint = "SetupFindFirstLineW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupFindFirstLine(InfHandle inf, string section, string? key, out InfContext context);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupFindNextLine(ref InfContext context, out InfContext next);
    [DllImport("setupapi.dll")]
    private static extern uint SetupGetFieldCount(ref InfContext context);
    [DllImport("setupapi.dll", EntryPoint = "SetupGetStringFieldW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupGetStringField(ref InfContext context, uint field, StringBuilder? buffer, uint size, out uint required);
    [DllImport("setupapi.dll", EntryPoint = "SetupVerifyInfFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupVerifyInfFile(string path, IntPtr platform, ref InfSignerInfo signer);
}
