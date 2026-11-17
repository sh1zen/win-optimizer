using System.Buffers.Binary;
using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<(string FullName, string Path), Lazy<long?>> _packageSizes = new();

    public Task<IReadOnlyDictionary<string, AppMetadata>> ReadAsync(IReadOnlyList<ItemDefinition> definitions) =>
        ReadMetadataAsync(definitions, null);

    public Task<IReadOnlyDictionary<string, AppMetadata>> ReadAsync(IReadOnlyList<ItemDefinition> definitions,
        IReadOnlyList<AppxRegistration> registrations) => ReadMetadataAsync(definitions, registrations);

    /// <summary>Overlaps independent registry, usage and package reads before matching their snapshots.</summary>
    private async Task<IReadOnlyDictionary<string, AppMetadata>> ReadMetadataAsync(IReadOnlyList<ItemDefinition> definitions,
        IReadOnlyList<AppxRegistration>? appxRegistrations)
    {
        if (definitions.Count == 0)
            return new Dictionary<string, AppMetadata>();
        var registrationsTask = Task.Run(ReadRegistrations);
        var usageTask = Task.Run(ReadUsage);
        var packagesTask = Task.Run(() => ReadPackages(definitions, appxRegistrations));
        await Task.WhenAll(registrationsTask, usageTask, packagesTask).ConfigureAwait(false);
        var snapshot = new MetadataSnapshot(await registrationsTask.ConfigureAwait(false),
            await packagesTask.ConfigureAwait(false), await usageTask.ConfigureAwait(false));
        return definitions.ToDictionary(item => item.Id,
            item => snapshot.Match(item, path => AppPaths.Expand(path, ResolveFolder)), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Indexes one refresh's exact identities and usage evidence for all installed definitions.</summary>
    internal sealed class MetadataSnapshot
    {
        private readonly IReadOnlyList<Registration> _registrations;
        private readonly ILookup<string?, Registration> _registrationPaths;
        private readonly ILookup<string, Registration> _registrationNames;
        private readonly ILookup<string, PackageMetadata> _packageNames;
        private readonly Dictionary<string, DateTimeOffset> _pathUsage = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTimeOffset> _familyUsage = new(StringComparer.OrdinalIgnoreCase);

        public MetadataSnapshot(IReadOnlyList<Registration> registrations, IReadOnlyList<PackageMetadata> packages,
            IReadOnlyDictionary<string, DateTimeOffset> usage)
        {
            _registrations = registrations;
            _registrationPaths = registrations.ToLookup(entry => entry.KeyPath, StringComparer.OrdinalIgnoreCase);
            _registrationNames = registrations.ToLookup(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
            _packageNames = packages.ToLookup(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var (identity, time) in usage)
            {
                RecordUsage(_pathUsage, identity, time);
                var separator = identity.IndexOf('!');
                if (separator >= 0) RecordUsage(_familyUsage, identity[..separator], time);
            }
        }

        /// <summary>Associates evidence only with explicit fingerprints, exact names, package families or executable paths.</summary>
        public AppMetadata Match(ItemDefinition item, Func<string, string> expandPath)
        {
            IEnumerable<Registration> registrations;
            if (item.RegistryUninstallKeyPath is { } keyPath) registrations = _registrationPaths[keyPath];
            else if (string.IsNullOrEmpty(item.RegistrySubKeyName) && string.IsNullOrEmpty(item.RegistryDisplayName))
                registrations = _registrationNames[item.Name];
            else registrations = _registrations.Where(entry =>
                (item.RegistrySubKeyName is { Length: > 0 } key && AppStatusDiscoveryService.MatchesPattern(entry.Key, key)) ||
                (item.RegistryDisplayName is { Length: > 0 } name && AppStatusDiscoveryService.MatchesPattern(entry.Name, name)));

            DateTimeOffset? installed = null, used = null;
            long? bytes = null;
            foreach (var registration in registrations)
            {
                if (installed is null || registration.Installed > installed) installed = registration.Installed;
                // Multiple registrations can describe the same product. Keep the largest reported footprint.
                if (bytes is null || registration.Bytes > bytes) bytes = registration.Bytes;
                ReadPathUsage(registration.Executable);
            }
            foreach (var path in item.DetectionPaths ?? []) ReadPathUsage(path);
            foreach (var name in item.AppxPackageName ?? [])
            foreach (var package in _packageNames[name])
            {
                if (installed is null || package.Installed > installed) installed = package.Installed;
                if (bytes is null || package.Bytes > bytes) bytes = package.Bytes;
                if (_familyUsage.TryGetValue(package.Family, out var time) && (used is null || time > used)) used = time;
            }
            return new AppMetadata(installed, bytes, used);

            void ReadPathUsage(string? path)
            {
                if (!string.IsNullOrWhiteSpace(path) && _pathUsage.TryGetValue(expandPath(path), out var time) &&
                    (used is null || time > used)) used = time;
            }
        }

        private static void RecordUsage(Dictionary<string, DateTimeOffset> usage, string identity, DateTimeOffset time)
        {
            if (!usage.TryGetValue(identity, out var previous) || time > previous) usage[identity] = time;
        }
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
                    var path = AppPaths.Expand(DecodeUsageName(encoded), ResolveFolder);
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

    internal static DateTimeOffset? ParseUsageTime(byte[] bytes)
    {
        if (bytes.Length != 72) return null;
        var fileTime = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(60, 8));
        if (fileTime <= 0 || fileTime > DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc).ToFileTimeUtc()) return null;
        return new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime));
    }

    /// <summary>Collects only matching package metadata; package size counts regular files without following reparse points.</summary>
    internal List<PackageMetadata> ReadPackages(IReadOnlyList<ItemDefinition> definitions, IReadOnlyList<AppxRegistration>? registrations)
    {
        var result = new List<PackageMetadata>();
        var names = definitions.SelectMany(item => item.AppxPackageName ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0) return result;
        if (registrations is not null)
        {
            foreach (var registration in registrations)
            {
                if (!names.Contains(registration.Name)) continue;
                try { result.Add(MeasurePackage(registration.Name, registration.ReadDetails())); }
                catch (Exception ex) when (IsReadFailure(ex)) { log.LogWarning($"App package metadata unavailable: {ex.Message}"); }
            }
            return result;
        }
        try
        {
            foreach (var package in new PackageManager().FindPackagesForUser(user.IsOtsElevation ? user.InteractiveUserSid! : ""))
            {
                var name = package.Id.Name;
                if (!names.Contains(name)) continue;
                try
                {
                    result.Add(MeasurePackage(name, WindowsAppxDeployment.ReadRegistrationDetails(package)));
                }
                catch (Exception ex) when (IsReadFailure(ex)) { log.LogWarning($"App package metadata unavailable: {ex.Message}"); }
            }
        }
        catch (Exception ex) when (IsReadFailure(ex)) { log.LogWarning($"App package metadata inventory unavailable: {ex.Message}"); }
        return result;
    }

    private PackageMetadata MeasurePackage(string name, AppxRegistrationDetails details) =>
        new(name, details.Family, details.Installed, GetPackageSize(details.FullName, details.Path, details.IsDevelopmentMode));

    /// <summary>Reuses immutable installed-package measurements; development files and failures stay fresh.</summary>
    internal long? GetPackageSize(string fullName, string path, bool isDevelopmentMode)
    {
        if (isDevelopmentMode) return ReadPackageSize(path);
        var key = (FullName: fullName, Path: path);
        var size = _packageSizes.GetOrAdd(key, entry => new Lazy<long?>(() => ReadPackageSize(entry.Path)));
        var bytes = size.Value;
        if (bytes is null)
            _packageSizes.TryRemove(new KeyValuePair<(string FullName, string Path), Lazy<long?>>(key, size));
        return bytes;
    }

    /// <summary>Measures regular package files without following filesystem redirects.</summary>
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
