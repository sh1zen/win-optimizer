using System.Buffers.Binary;
using System.Globalization;
using System.Security;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Management.Deployment;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;

namespace WinOptimizer.Infrastructure.Features.SoftwareApps.Services;

/// <summary>Reads uninstall registrations, package inventory and interactive-user shell launch evidence.</summary>
public sealed class WindowsAppMetadataSource(IInteractiveUserService user, ILogService log) : IAppMetadataSource
{
    internal sealed record Registration(string Key, string Name, DateTimeOffset? Installed, long? Bytes,
        string? Executable, string? KeyPath = null);
    internal sealed record PackageMetadata(string Name, string Family, DateTimeOffset Installed, long? Bytes);

    public Task<IReadOnlyDictionary<string, AppMetadata>> ReadAsync(IReadOnlyList<ItemDefinition> definitions) => Task.Run<IReadOnlyDictionary<string, AppMetadata>>(() =>
    {
        if (definitions.Count == 0)
            return new Dictionary<string, AppMetadata>();
        var registrations = ReadRegistrations();
        var usage = ReadUsage();
        var packages = ReadPackages(definitions);
        return definitions.ToDictionary(item => item.Id,
            item => Match(item, registrations, packages, usage, path => ExpandPath(path, ResolveFolder)), StringComparer.OrdinalIgnoreCase);
    });

    /// <summary>Associates evidence only with explicit fingerprints, exact names, package families or executable paths.</summary>
    internal static AppMetadata Match(ItemDefinition item, IReadOnlyList<Registration> registrations,
        IReadOnlyList<PackageMetadata> packages, IReadOnlyDictionary<string, DateTimeOffset> usage, Func<string, string> expandPath)
    {
        var matches = registrations.Where(entry =>
            item.RegistryUninstallKeyPath is { } keyPath ? string.Equals(entry.KeyPath, keyPath, StringComparison.OrdinalIgnoreCase) :
            (!string.IsNullOrEmpty(item.RegistrySubKeyName) && AppStatusDiscoveryService.MatchesPattern(entry.Key, item.RegistrySubKeyName)) ||
            (!string.IsNullOrEmpty(item.RegistryDisplayName) && AppStatusDiscoveryService.MatchesPattern(entry.Name, item.RegistryDisplayName)) ||
            (string.IsNullOrEmpty(item.RegistrySubKeyName) && string.IsNullOrEmpty(item.RegistryDisplayName) &&
                string.Equals(entry.Name, item.Name, StringComparison.OrdinalIgnoreCase))).ToList();
        var packageMatches = packages.Where(package => item.AppxPackageName?.Contains(package.Name,
            StringComparer.OrdinalIgnoreCase) == true).ToList();
        var dates = matches.Select(entry => entry.Installed).Concat(packageMatches.Select(package => (DateTimeOffset?)package.Installed));
        // Multiple registrations can describe the same product. Use the largest reported footprint instead of double counting it.
        var sizes = matches.Select(entry => entry.Bytes).Concat(packageMatches.Select(package => package.Bytes));
        var paths = matches.Select(entry => entry.Executable).Concat(item.DetectionPaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => expandPath(path!)).ToList();
        var lastUse = usage.Where(entry => paths.Contains(entry.Key, StringComparer.OrdinalIgnoreCase) ||
            packageMatches.Any(package => entry.Key.StartsWith(package.Family + "!", StringComparison.OrdinalIgnoreCase)))
            .Select(entry => (DateTimeOffset?)entry.Value).Max();
        return new AppMetadata(dates.Max(), sizes.Max(), lastUse);
    }

    /// <summary>Reads both machine registry views and the interactive user's registrations once per refresh.</summary>
    private List<Registration> ReadRegistrations()
    {
        const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        var result = new List<Registration>();
        foreach (var path in new[] { uninstall, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            ReadRegistrationKey(Registry.LocalMachine, path, @"HKLM\" + path, result);
            ReadRegistrationKey(UserHive, UserPath(path), @"HKCU\" + path, result);
        }
        return result;
    }

    private RegistryKey UserHive => user.IsOtsElevation ? Registry.Users : Registry.CurrentUser;
    private string UserPath(string path) => user.IsOtsElevation
        ? $@"{user.InteractiveUserSid ?? throw new InvalidOperationException("Interactive user identity is unavailable.")}\{path}" : path;

    /// <summary>Retains readable records while logging inaccessible registry boundaries.</summary>
    private void ReadRegistrationKey(RegistryKey hive, string path, string logicalPath, List<Registration> result)
    {
        try
        {
            using var root = hive.OpenSubKey(path);
            if (root is null) return;
            foreach (var name in root.GetSubKeyNames())
            {
                try
                {
                    using var key = root.OpenSubKey(name);
                    if (key is null) continue;
                    var icon = key.GetValue("DisplayIcon") as string;
                    result.Add(new Registration(name, key.GetValue("DisplayName") as string ?? "",
                        ParseInstallDate(key.GetValue("InstallDate") as string), ParseSize(key.GetValue("EstimatedSize")),
                        ParseIconPath(icon), logicalPath + "\\" + name));
                }
                catch (Exception ex) when (IsReadFailure(ex)) { log.LogWarning($"App metadata registration unavailable: {ex.Message}"); }
            }
        }
        catch (Exception ex) when (IsReadFailure(ex)) { log.LogWarning($"App metadata registry inventory unavailable: {ex.Message}"); }
    }

    internal static DateTimeOffset? ParseInstallDate(string? value) =>
        DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc)) : null;

    /// <summary>Registry EstimatedSize is an unsigned DWORD measured in KiB.</summary>
    internal static long? ParseSize(object? value) => value switch
    {
        int size => (long)unchecked((uint)size) * 1024,
        long size when size >= 0 && size <= long.MaxValue / 1024 => size * 1024,
        _ => null,
    };

    internal static string? ParseIconPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var path = value.Trim();
        if (path.StartsWith('"'))
        {
            var end = path.IndexOf('"', 1);
            return end > 1 ? path[1..end] : null;
        }
        var comma = path.LastIndexOf(',');
        return comma >= 0 && int.TryParse(path[(comma + 1)..], out _) ? path[..comma].Trim() : path;
    }

    /// <summary>Reads the Windows 7+ UserAssist format. Missing tracking data remains unknown, never file access time.</summary>
    private Dictionary<string, DateTimeOffset> ReadUsage()
    {
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = UserHive.OpenSubKey(UserPath(@"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist"));
            if (root is null) return result;
            foreach (var group in root.GetSubKeyNames())
            {
                using var count = root.OpenSubKey(group + @"\Count");
                if (count is null) continue;
                foreach (var encoded in count.GetValueNames())
                {
                    if (count.GetValue(encoded) is not byte[] bytes || ParseUsageTime(bytes) is not { } time) continue;
                    var path = ExpandPath(DecodeUsageName(encoded), ResolveFolder);
                    if (!result.TryGetValue(path, out var previous) || time > previous) result[path] = time;
                }
            }
        }
        catch (Exception ex) when (IsReadFailure(ex)) { log.LogWarning($"App usage metadata unavailable: {ex.Message}"); }
        return result;
    }

    internal static string DecodeUsageName(string encoded) => new(encoded.Select(character => character switch
    {
        >= 'a' and <= 'z' => (char)('a' + (character - 'a' + 13) % 26),
        >= 'A' and <= 'Z' => (char)('A' + (character - 'A' + 13) % 26),
        _ => character,
    }).ToArray());

    /// <summary>Resolves account-specific paths against the interactive user, including under alternate-account elevation.</summary>
    private string ResolveFolder(Environment.SpecialFolder folder) => folder is Environment.SpecialFolder.LocalApplicationData
        or Environment.SpecialFolder.ApplicationData or Environment.SpecialFolder.UserProfile
        ? user.GetInteractiveUserFolderPath(folder) : Environment.GetFolderPath(folder);

    /// <summary>Normalizes documented Known Folder identifiers and user variables before comparing executable evidence.</summary>
    internal static string ExpandPath(string path, Func<Environment.SpecialFolder, string> resolveFolder)
    {
        foreach (var (variable, folder) in new[]
        {
            ("%LOCALAPPDATA%", Environment.SpecialFolder.LocalApplicationData),
            ("%APPDATA%", Environment.SpecialFolder.ApplicationData),
            ("%USERPROFILE%", Environment.SpecialFolder.UserProfile),
        })
        {
            if (path.Contains(variable, StringComparison.OrdinalIgnoreCase))
                path = path.Replace(variable, resolveFolder(folder), StringComparison.OrdinalIgnoreCase);
        }
        if (path.Length > 38 && path[38] == '\\' && Guid.TryParse(path.AsSpan(0, 38), out var id))
        {
            Environment.SpecialFolder? folder = id.ToString("D").ToUpperInvariant() switch
            {
                "905E63B6-C1BF-494E-B29C-65B732D3D21A" or "6D809377-6AF0-444B-8957-A3773F02200E" => Environment.SpecialFolder.ProgramFiles,
                "7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E" => Environment.SpecialFolder.ProgramFilesX86,
                "F1B32785-6FBA-4FCF-9D55-7B8E7F157091" => Environment.SpecialFolder.LocalApplicationData,
                "3EB685DB-65F9-4CF6-A03A-E3EF65729F3D" => Environment.SpecialFolder.ApplicationData,
                "5E6C858F-0E22-4760-9AFE-EA3317B67173" => Environment.SpecialFolder.UserProfile,
                "F38BF404-1D43-42F2-9305-67DE0B28FC23" => Environment.SpecialFolder.Windows,
                "1AC14E77-02E7-4E5D-B744-2EB1AE5198B7" => Environment.SpecialFolder.System,
                _ => null,
            };
            if (folder is { } known && resolveFolder(known) is { Length: > 0 } root)
                path = root.TrimEnd('\\') + path[38..];
        }
        return Environment.ExpandEnvironmentVariables(path);
    }

    internal static DateTimeOffset? ParseUsageTime(byte[] bytes)
    {
        if (bytes.Length != 72) return null;
        var fileTime = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(60, 8));
        if (fileTime <= 0 || fileTime > DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc).ToFileTimeUtc()) return null;
        return new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime));
    }

    /// <summary>Collects only matching package metadata; package size counts regular files without following reparse points.</summary>
    private List<PackageMetadata> ReadPackages(IReadOnlyList<ItemDefinition> definitions)
    {
        var result = new List<PackageMetadata>();
        var names = definitions.SelectMany(item => item.AppxPackageName ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0) return result;
        try
        {
            foreach (var package in new PackageManager().FindPackagesForUser(user.IsOtsElevation ? user.InteractiveUserSid! : ""))
            {
                if (!names.Contains(package.Id.Name)) continue;
                try
                {
                    result.Add(new PackageMetadata(package.Id.Name, package.Id.FamilyName, package.InstalledDate,
                        ReadPackageSize(package.InstalledLocation.Path)));
                }
                catch (Exception ex) when (IsReadFailure(ex)) { log.LogWarning($"App package metadata unavailable: {ex.Message}"); }
            }
        }
        catch (Exception ex) when (IsReadFailure(ex)) { log.LogWarning($"App package metadata inventory unavailable: {ex.Message}"); }
        return result;
    }

    private long? ReadPackageSize(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path)) return null;
            var root = new DirectoryInfo(path);
            if ((root.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            return root.EnumerateFiles("*", new EnumerationOptions
            {
                RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint,
            }).Sum(file => file.Length);
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            log.LogWarning($"App package size unavailable: {ex.Message}");
            return null;
        }
    }

    private static bool IsReadFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or SecurityException or COMException;
}
