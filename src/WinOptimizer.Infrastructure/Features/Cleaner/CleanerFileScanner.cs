using Microsoft.Win32;
using System.Runtime.InteropServices;
using WinOptimizer.Core.Features.Cleaner.Models;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;

namespace WinOptimizer.Infrastructure.Features.Cleaner;

/// <summary>Windows-owned locations are independent of the selected user profiles.</summary>
public interface ICleanerSystemFolders { string GetFolderPath(Environment.SpecialFolder folder); }
public sealed class WindowsCleanerSystemFolders : ICleanerSystemFolders
{
    public string GetFolderPath(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);
}

/// <summary>Discovers selected profiles' privacy data, aged caches and broken shortcuts, retaining their source account.</summary>
public sealed class CleanerFileScanner(ICleanerRegistrySessions accounts, IAppxPackageSource packages,
    ICleanerProcessMonitor processes, ICleanerShortcutReader shortcuts, ICleanerSystemFolders systemFolders)
{
    private sealed record Browser(string Process, string RelativePath, bool Roaming = false, bool FlatProfile = false);
    private sealed record Source(string Path, bool Shortcuts, string[]? Patterns, string LabelKey,
        CleanerProfilePath? Profile = null, string? UnavailableReason = null, bool Recursive = false);
    private enum PrivacyCacheKind { Folder, Telegram, Package }
    private sealed record PrivacyCache(Environment.SpecialFolder Folder, string RelativePath, string Label,
        PrivacyCacheKind Kind = PrivacyCacheKind.Folder, string? Process = null);
    private static readonly PrivacyCache[] PrivacyCaches =
    [
        new(Environment.SpecialFolder.LocalApplicationData, @"Microsoft\OneDrive\logs", "Cleaner_OneDriveLogs"),
        new(Environment.SpecialFolder.ApplicationData, @"Telegram Desktop\tdata", "Cleaner_TelegramCache", PrivacyCacheKind.Telegram, "Telegram"),
        new(Environment.SpecialFolder.LocalApplicationData, @"Packages\Microsoft.WindowsStore_8wekyb3d8bbwe", "Cleaner_StoreCache", PrivacyCacheKind.Package, "WinStore.App"),
        new(Environment.SpecialFolder.LocalApplicationData, @"Packages\Microsoft.ZuneVideo_8wekyb3d8bbwe", "Cleaner_MoviesCache", PrivacyCacheKind.Package, "Video.UI"),
        new(Environment.SpecialFolder.LocalApplicationData, @"Packages\Microsoft.Windows.ContentDeliveryManager_cw5n1h2txyewy\LocalState\Assets", "Cleaner_ContentDeliveryCache")
    ];
    private static readonly string[] ChromiumCaches = ["Cache", "Code Cache", "GPUCache", "Media Cache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache", "GPUPersistentCache"];
    private static readonly Browser[] Browsers =
    [
        new("msedge", @"Microsoft\Edge\User Data"), new("chrome", @"Google\Chrome\User Data"),
        new("brave", @"BraveSoftware\Brave-Browser\User Data"), new("vivaldi", @"Vivaldi\User Data"),
        new("opera", @"Opera Software\Opera Stable", true, true), new("opera", @"Opera Software\Opera GX Stable", true, true),
        new("opera", @"Opera Software\Opera Stable", FlatProfile: true), new("opera", @"Opera Software\Opera GX Stable", FlatProfile: true),
        new("firefox", @"Mozilla\Firefox\Profiles")
    ];

    private static string BrowserRoot(Browser browser, ICleanerRegistrySession account) => Path.Combine(
        ProfileFolder(account, browser.Roaming ? Environment.SpecialFolder.ApplicationData : Environment.SpecialFolder.LocalApplicationData), browser.RelativePath);

    private static string ProfileFolder(ICleanerRegistrySession account, Environment.SpecialFolder folder) =>
        WindowsCleanerRegistryProfiles.ValidateProfileDirectory(account.GetFolderPath(folder));

    public async Task<IReadOnlyList<CleanerApplication>> GetApplicationsAsync(CancellationToken ct)
    {
        var inventory = await GetApplicationsAsync(new CleanerOptions(), ct).ConfigureAwait(false);
        if (inventory.Notices.Count > 0) throw new IOException(string.Join(Environment.NewLine, inventory.Notices));
        return inventory.Applications;
    }

    /// <summary>Builds a union of installed cache choices from the selected profiles, retaining discovery failures.</summary>
    public Task<CleanerApplicationInventory> GetApplicationsAsync(CleanerOptions options, CancellationToken ct) => Task.Run(async () =>
    {
        options.Validate();
        List<string> notices = [];
        Dictionary<string, CleanerApplication> apps = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, account) in CleanerAccounts.OpenSelected(accounts, options.JunkAccountSids, notices, ct))
        {
            try { foreach (var app in await ReadApplicationsAsync(account, ct).ConfigureAwait(false)) apps.TryAdd(app.Id, app); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
            { notices.Add($"{account.UserPrefix}: {ex.Message}"); }
        }
        return new CleanerApplicationInventory(apps.Values.OrderBy(app => app.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray(), notices.AsReadOnly());
    }, ct);

    /// <summary>Matches cache directories to packages installed for this exact account.</summary>
    private async Task<IReadOnlyList<CleanerApplication>> ReadApplicationsAsync(ICleanerRegistrySession account, CancellationToken ct)
    {
        var installed = account.IsInteractive ? await packages.GetInstalledPackageNamesAsync(ct).ConfigureAwait(false) :
            await packages.GetInstalledPackageNamesForUserAsync(account.UserPrefix[@"HKEY_USERS\".Length..], ct).ConfigureAwait(false);
        var root = Path.Combine(ProfileFolder(account, Environment.SpecialFolder.LocalApplicationData), "Packages");
        if (!Directory.Exists(root)) return [];
        CleanerPaths.ValidateDescendant(root, Path.Combine(root, ".boundary"));
        List<CleanerApplication> result = [];
        foreach (var path in Directory.GetDirectories(root))
        {
            ct.ThrowIfCancellationRequested();
            var id = Path.GetFileName(path);
            var separator = id.LastIndexOf('_');
            if (separator > 0 && installed.Contains(id[..separator]) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 &&
                (Directory.Exists(Path.Combine(path, "LocalCache")) || Directory.Exists(Path.Combine(path, "TempState"))))
                result.Add(new(id, id[..separator]));
        }
        return result;
    }

    public Task<CleanerScanResult> ScanAsync(CleanerArea area, CleanerOptions options, CancellationToken ct,
        IProgress<CleanerProgress>? progress = null) => Task.Run(async () =>
    {
        options.Validate();
        List<string> notices = [];
        List<Source> roots = [];
        var selection = area switch
        {
            CleanerArea.Privacy => options.PrivacyAccountSids,
            CleanerArea.JunkFiles => options.JunkAccountSids,
            CleanerArea.Shortcuts => options.ShortcutAccountSids,
            _ => throw new ArgumentOutOfRangeException(nameof(area))
        };
        foreach (var (sid, account) in CleanerAccounts.OpenSelected(accounts, selection, notices, ct))
        {
            try
            {
                IReadOnlyList<CleanerApplication> apps = [];
                if (area == CleanerArea.JunkFiles && options.ApplicationCaches.Length > 0)
                {
                    try { apps = await ReadApplicationsAsync(account, ct).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
                    { notices.Add($"{account.UserPrefix}: {ex.Message}"); }
                }
                AddProfileSources(area, options, sid, account, apps, roots, notices, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
            { notices.Add($"{account.UserPrefix}: {ex.Message}"); }
        }
        // Common locations belong to the machine and are independent of profile selection.
        if (area == CleanerArea.JunkFiles)
        {
            AddSystemJunkSources(options, roots, notices, ct);
        }
        if (area == CleanerArea.Privacy && options.DefenderLogs)
        {
            try
            {
                var common = WindowsCleanerRegistryProfiles.ValidateProfileDirectory(systemFolders.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
                roots.Add(new(Path.Combine(common, @"Microsoft\Windows Defender\Support"), false, ["MPLog-*.log"], "Cleaner_DefenderLogs"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { notices.Add($"Microsoft Defender support logs: {ex.Message}"); }
        }
        if (area == CleanerArea.Shortcuts)
        {
            if (options.DesktopShortcuts)
            {
                int index = roots.FindIndex(root => root.LabelKey != "Cleaner_Desktop");
                roots.Insert(index < 0 ? roots.Count : index,
                    new(systemFolders.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), true, null, "Cleaner_Desktop"));
            }
            if (options.StartMenuShortcuts)
            {
                roots.Add(new(systemFolders.GetFolderPath(Environment.SpecialFolder.CommonPrograms), true, null, "Cleaner_StartMenu"));
                roots.Add(new(systemFolders.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), true, null, "Cleaner_StartMenu"));
            }
        }
        return Inspect(area, options, roots, notices, ct, progress);
    }, ct);

    /// <summary>Adds allowlisted machine diagnostics once, validating each location independently.</summary>
    private void AddSystemJunkSources(CleanerOptions options, List<Source> roots, List<string> notices, CancellationToken ct)
    {
        void Add(Environment.SpecialFolder folder, string relative, string label, string[]? patterns = null, bool recursive = false)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var basePath = WindowsCleanerRegistryProfiles.ValidateProfileDirectory(systemFolders.GetFolderPath(folder));
                var path = Path.Combine(basePath, relative);
                CleanerPaths.ValidateDescendant(basePath, Path.Combine(path, ".boundary"));
                var source = new Source(path, false, patterns, label, Recursive: recursive);
                if (label == "Cleaner_WindowsTemp")
                {
                    int index = roots.FindIndex(root => root.LabelKey != "Cleaner_UserTemp");
                    roots.Insert(index < 0 ? roots.Count : index, source);
                }
                else roots.Add(source);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { notices.Add($"{label}: {ex.Message}"); }
        }

        if (options.WindowsTemp) Add(Environment.SpecialFolder.Windows, "Temp", "Cleaner_WindowsTemp");
        if (options.WindowsLogs)
        {
            Add(Environment.SpecialFolder.Windows, "Logs", "Cleaner_WindowsLogs", ["*.log", "*.etl"], recursive: true);
            Add(Environment.SpecialFolder.Windows, @"Logs\CBS", "Cleaner_WindowsLogs", ["CbsPersist_*.cab"]);
            Add(Environment.SpecialFolder.Windows, @"Logs\DISM", "Cleaner_WindowsLogs", ["dism.log.bak"]);
            Add(Environment.SpecialFolder.Windows, "", "Cleaner_WindowsLogs", ["*.log"]);
            Add(Environment.SpecialFolder.Windows, "Panther", "Cleaner_WindowsLogs", ["*.log"], recursive: true);
            Add(Environment.SpecialFolder.Windows, "INF", "Cleaner_WindowsLogs", ["setupapi*.log"]);
        }
        if (options.ErrorReports)
            foreach (var report in new[] { "ReportArchive", "ReportQueue", "Temp" })
                Add(Environment.SpecialFolder.CommonApplicationData, @"Microsoft\Windows\WER\" + report, "Cleaner_ErrorReports");
        if (options.CrashDumps)
        {
            Add(Environment.SpecialFolder.Windows, "Minidump", "Cleaner_CrashDumps", ["*.dmp"]);
            Add(Environment.SpecialFolder.Windows, "LiveKernelReports", "Cleaner_CrashDumps", ["*.dmp"], recursive: true);
            Add(Environment.SpecialFolder.Windows, "", "Cleaner_CrashDumps", ["MEMORY.DMP"]);
        }
    }

    /// <summary>Resolves each profile once and carries its special-folder identity into every discovered root.</summary>
    private void AddProfileSources(CleanerArea area, CleanerOptions options, string? sid, ICleanerRegistrySession account,
        IReadOnlyList<CleanerApplication> apps, List<Source> roots, List<string> notices, CancellationToken ct)
    {
        void Add(Environment.SpecialFolder folder, string relative, bool links, string? exact, string label, string? unavailableReason = null)
        {
            ct.ThrowIfCancellationRequested();
            var basePath = ProfileFolder(account, folder);
            var path = Path.Combine(basePath, relative);
            CleanerPaths.ValidateDescendant(basePath, Path.Combine(path, ".boundary"));
            roots.Add(new(path, links, exact is null ? null : [exact], label, new(sid, folder, relative), unavailableReason));
        }

        // Detect active applications once per source without hiding their privacy inventory.
        string? PrivacyBlockReason(string? process)
        {
            if (process is null || !processes.IsRunning(process)) return null;
            var reason = $"Close {process} and scan again to clean its privacy data.";
            notices.Add($"{account.UserPrefix}: {reason}");
            return reason;
        }
        if (area == CleanerArea.JunkFiles)
        {
            if (options.UserTemp) Add(Environment.SpecialFolder.LocalApplicationData, "Temp", false, null, "Cleaner_UserTemp");
            if (options.ShaderCache) Add(Environment.SpecialFolder.LocalApplicationData, "D3DSCache", false, null, "Cleaner_ShaderCache");
            if (options.ThumbnailCache) Add(Environment.SpecialFolder.LocalApplicationData, @"Microsoft\Windows\Explorer", false, "thumbcache_*.db", "Cleaner_ThumbnailCache");
            if (options.ErrorReports)
                foreach (var report in new[] { "ReportArchive", "ReportQueue", "Temp" })
                    Add(Environment.SpecialFolder.LocalApplicationData, @"Microsoft\Windows\WER\" + report, false, null, "Cleaner_ErrorReports");
            if (options.CrashDumps) Add(Environment.SpecialFolder.LocalApplicationData, "CrashDumps", false, "*.dmp", "Cleaner_CrashDumps");
            if (options.InternetCache) Add(Environment.SpecialFolder.LocalApplicationData, @"Microsoft\Windows\INetCache", false, null, "Cleaner_InternetCache");
            foreach (var app in apps.Where(app => options.ApplicationCaches.Contains(app.Id, StringComparer.OrdinalIgnoreCase)))
                foreach (var cache in new[] { "LocalCache", "TempState" })
                    Add(Environment.SpecialFolder.LocalApplicationData, Path.Combine("Packages", app.Id, cache), false, null, "Cleaner_ApplicationCaches");
        }
        if (area == CleanerArea.Shortcuts)
        {
            // Resolve sources independently so an unavailable folder does not hide the account's other links.
            void AddShortcuts(Environment.SpecialFolder folder, string relative, string label)
            {
                try { Add(folder, relative, true, null, label); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
                { notices.Add($"{account.UserPrefix} ({label}): {ex.Message}"); }
            }
            if (options.DesktopShortcuts) AddShortcuts(Environment.SpecialFolder.DesktopDirectory, "", "Cleaner_Desktop");
            if (options.StartMenuShortcuts)
            {
                AddShortcuts(Environment.SpecialFolder.Programs, "", "Cleaner_StartMenu");
                AddShortcuts(Environment.SpecialFolder.StartMenu, "", "Cleaner_StartMenu");
            }
            if (options.RecentShortcuts) AddShortcuts(Environment.SpecialFolder.ApplicationData, @"Microsoft\Windows\Recent", "Cleaner_RecentFiles");
        }
        if (area != CleanerArea.Privacy) return;
        if (options.RecentFiles) Add(Environment.SpecialFolder.ApplicationData, @"Microsoft\Windows\Recent", true, null, "Cleaner_RecentFiles");
        if (options.JumpLists)
            foreach (var (directory, pattern) in new[] { ("AutomaticDestinations", "*.automaticDestinations-ms"), ("CustomDestinations", "*.customDestinations-ms") })
                Add(Environment.SpecialFolder.ApplicationData, @"Microsoft\Windows\Recent\" + directory, false, pattern, "Cleaner_JumpLists");
        if (options.ApplicationHistory) Add(Environment.SpecialFolder.ApplicationData, @"Microsoft\Office\Recent", true, null, "Cleaner_OfficeHistory");
        if (options.ApplicationPrivacyCaches)
            foreach (var cache in PrivacyCaches)
            {
                var path = Path.Combine(ProfileFolder(account, cache.Folder), cache.RelativePath);
                if (!Directory.Exists(path)) continue;
                try
                {
                    CleanerPaths.ValidateDescendant(path, Path.Combine(path, ".boundary"));
                    var unavailableReason = PrivacyBlockReason(cache.Process);
                    if (cache.Kind == PrivacyCacheKind.Telegram)
                    {
                        foreach (var data in Directory.GetDirectories(path).Where(directory =>
                            Path.GetFileName(directory) == "user_data" ||
                            Path.GetFileName(directory).StartsWith("user_data#", StringComparison.Ordinal) &&
                            Path.GetFileName(directory)[10..] is { Length: > 0 } suffix && suffix.All(char.IsAsciiDigit)))
                            foreach (var kind in new[] { "cache", "media_cache" })
                                Add(cache.Folder, Path.Combine(cache.RelativePath, Path.GetFileName(data), kind), false, null, cache.Label, unavailableReason);
                    }
                    else if (cache.Kind == PrivacyCacheKind.Package)
                        foreach (var directory in new[] { "LocalCache", "TempState", @"AC\INetCache" })
                            Add(cache.Folder, Path.Combine(cache.RelativePath, directory), false, null, cache.Label, unavailableReason);
                    else Add(cache.Folder, cache.RelativePath, false, null, cache.Label, unavailableReason);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { notices.Add($"{path}: {ex.Message}"); }
            }
        if (options.CertificateCache)
            foreach (var directory in new[] { "Content", "MetaData" })
                Add(Environment.SpecialFolder.UserProfile, @"AppData\LocalLow\Microsoft\CryptnetUrlCache\" + directory, false, null, "Cleaner_CertificateCache");
        if (!options.BrowserCache && !options.BrowserHistory && !options.BrowserOfflineCache) return;
        foreach (var browser in Browsers)
        {
            ct.ThrowIfCancellationRequested();
            var browserRoot = BrowserRoot(browser, account);
            if (!Directory.Exists(browserRoot)) continue;
            try
            {
                CleanerPaths.ValidateDescendant(browserRoot, Path.Combine(browserRoot, ".boundary"));
                var unavailableReason = PrivacyBlockReason(browser.Process);
                var folder = browser.Roaming ? Environment.SpecialFolder.ApplicationData : Environment.SpecialFolder.LocalApplicationData;
                var profiles = Directory.GetDirectories(browserRoot)
                    .Where(path => browser.Process == "firefox" || Path.GetFileName(path) == "Default" ||
                        Path.GetFileName(path).StartsWith("Profile ", StringComparison.Ordinal) || File.Exists(Path.Combine(path, "Preferences")));
                if (browser.FlatProfile) profiles = profiles.Prepend(browserRoot);
                if (options.BrowserCache && browser.Process != "firefox")
                    foreach (var cache in new[] { "ShaderCache", "GrShaderCache", "GraphiteDawnCache", "GPUPersistentCache" })
                        Add(folder, Path.Combine(browser.RelativePath, cache), false, null, $"Cleaner_BrowserCache_{browser.Process}", unavailableReason);
                foreach (var profile in profiles)
                {
                    ct.ThrowIfCancellationRequested();
                    CleanerPaths.ValidateDescendant(browserRoot, Path.Combine(profile, ".boundary"));
                    var relative = profile == browserRoot ? browser.RelativePath : Path.Combine(browser.RelativePath, Path.GetFileName(profile));
                    if (options.BrowserCache)
                        foreach (var cache in browser.Process == "firefox" ? new[] { "cache2", "startupCache" } : ChromiumCaches)
                            Add(folder, Path.Combine(relative, cache), false, null, $"Cleaner_BrowserCache_{browser.Process}", unavailableReason);
                    if (options.BrowserOfflineCache && browser.Process != "firefox")
                        foreach (var cache in new[] { @"Service Worker\CacheStorage", @"Service Worker\ScriptCache" })
                            Add(folder, Path.Combine(relative, cache), false, null, "Cleaner_BrowserOfflineCache", unavailableReason);
                    if (options.BrowserHistory && browser.Process != "firefox") Add(folder, relative, false, "History", "Cleaner_BrowserHistory", unavailableReason);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { notices.Add($"{browserRoot}: {ex.Message}"); }
        }
    }

    /// <summary>Inventories unique files before checking metadata, sharing access and shortcut destinations.</summary>
    private CleanerScanResult Inspect(CleanerArea area, CleanerOptions options, List<Source> roots, List<string> notices,
        CancellationToken ct, IProgress<CleanerProgress>? progress)
    {
        List<CleanerFinding> findings = [];
        var scanTime = DateTime.UtcNow;
        var cutoff = area == CleanerArea.JunkFiles ? scanTime.AddDays(-options.MinimumAgeDays) : scanTime;
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        List<(Source Source, string Path)> candidates = [];
        progress?.Report(new($"Cleaner_{area}", 0, 0));
        foreach (var root in roots.Where(root => !string.IsNullOrWhiteSpace(root.Path)))
        {
            try
            {
                IEnumerable<string> files;
                if (root.Patterns is null || root.Recursive) files = CleanerPaths.EnumerateFiles(root.Path, notices, ct);
                else
                {
                    if (!Directory.Exists(root.Path)) continue;
                    CleanerPaths.ValidateDescendant(root.Path, Path.Combine(root.Path, ".boundary"));
                    files = Directory.EnumerateFiles(root.Path);
                }
                if (root.Patterns is { } patterns)
                    files = files.Where(path => patterns.Any(pattern =>
                        System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(path))));
                foreach (var path in files)
                {
                    ct.ThrowIfCancellationRequested();
                    if (root.Shortcuts && !path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || !visited.Add(path)) continue;
                    candidates.Add((root, path));
                    if (candidates.Count % 256 == 0) progress?.Report(new($"Cleaner_{area}", candidates.Count, 0));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { notices.Add($"{root.Path}: {ex.Message}"); }
        }
        int processed = 0;
        progress?.Report(new($"Cleaner_{area}", 0, candidates.Count));
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var path = candidate.Path;
            try
            {
                CleanerPaths.ValidateDescendant(candidate.Source.Path, path);
                var info = new FileInfo(path);
                // Junk inventory includes protected recent files; privacy retains its scan-time filter.
                if (area == CleanerArea.Privacy && (info.LastWriteTimeUtc > cutoff || info.LastAccessTimeUtc > scanTime)) continue;
                string? target = null;
                if (area == CleanerArea.Shortcuts)
                {
                    using var account = accounts.Open(candidate.Source.Profile?.AccountSid);
                    target = ReadShortcut(path, account);
                    if (string.IsNullOrEmpty(target) || !CleanerPaths.IsMissingLocalFile(target)) continue;
                }
                var evidence = new CleanerFileTarget(candidate.Source.Path, path, info.Length, info.LastWriteTimeUtc, scanTime, target)
                    { Profile = candidate.Source.Profile };
                var unavailableReason = candidate.Source.UnavailableReason;
                if (area == CleanerArea.JunkFiles)
                {
                    if (info.LastWriteTimeUtc > scanTime || info.LastAccessTimeUtc > scanTime)
                        unavailableReason = "The file changed or was used after scanning began. Scan again.";
                    else if (options.MinimumAgeDays > 0 && info.LastWriteTimeUtc > cutoff)
                        unavailableReason = $"Modified within the last {options.MinimumAgeDays} days. Review the minimum file age in Cleaner settings and scan again.";
                }
                if (unavailableReason is null)
                {
                    try { using (CleanerFileDeletion.OpenVerified(evidence, deleting: !options.BackupBeforeCleaning)) { } }
                    catch (Exception ex) when (area is CleanerArea.Privacy or CleanerArea.JunkFiles && ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                    {
                        unavailableReason = ex.Message;
                        notices.Add($"{path}: {ex.Message}");
                    }
                }
                findings.Add(new(Guid.NewGuid(), area, candidate.Source.LabelKey, path, info.Length, unavailableReason is null, evidence)
                    { UnavailableReason = unavailableReason });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or System.ComponentModel.Win32Exception)
            { notices.Add($"{path}: {ex.Message}"); }
            finally
            {
                processed++;
                if (processed % 64 == 0 || processed == candidates.Count) progress?.Report(new($"Cleaner_{area}", processed, candidates.Count));
            }
        }
        return new(findings.AsReadOnly(), notices.AsReadOnly()) { CheckedItems = candidates.Count };
    }

    /// <summary>Rechecks the original account, root and evidence before deleting a scanned file.</summary>
    public void Clean(CleanerFileTarget file)
    {
        ValidateForCleanup(file);
        CleanerFileDeletion.Delete(file);
    }

    public void ValidateForCleanup(CleanerFileTarget file)
    {
        ValidateForRestore(file);
        var info = new FileInfo(file.Path);
        // Reading a Shell link can update access time; its unchanged target and contents govern shortcut cleanup.
        if (!info.Exists || info.Length != file.Length || info.LastWriteTimeUtc != file.LastWriteUtc ||
            file.ShortcutTarget is null && info.LastAccessTimeUtc > file.CutoffUtc)
            throw new IOException("The file changed or was used after the scan. Scan again.");
        if (file.ShortcutTarget is not null)
        {
            using var account = accounts.Open(file.Profile?.AccountSid);
            if (ReadShortcut(file.Path, account) != file.ShortcutTarget || !CleanerPaths.IsMissingLocalFile(file.ShortcutTarget))
                throw new IOException("The shortcut target changed. Scan again.");
        }
    }

    public void ValidateForRestore(CleanerFileTarget file)
    {
        CleanerPaths.ValidateDescendant(file.Root, file.Path);
        using var account = accounts.Open(file.Profile?.AccountSid);
        if (file.Profile is { } profile)
        {
            if (profile.Folder is not (Environment.SpecialFolder.LocalApplicationData or Environment.SpecialFolder.ApplicationData or
                Environment.SpecialFolder.DesktopDirectory or Environment.SpecialFolder.Programs or Environment.SpecialFolder.StartMenu or Environment.SpecialFolder.UserProfile) || profile.RelativePath is null)
                throw new IOException("Invalid account cleanup folder.");
            var basePath = ProfileFolder(account, profile.Folder);
            var resolved = Path.GetFullPath(Path.Combine(basePath, profile.RelativePath));
            CleanerPaths.ValidateDescendant(basePath, Path.Combine(resolved, ".boundary"));
            if (!string.Equals(resolved, Path.GetFullPath(file.Root), StringComparison.OrdinalIgnoreCase))
                throw new IOException("The account cleanup folder changed. Scan again.");
        }
        foreach (var browser in Browsers)
            if (file.Path.StartsWith(BrowserRoot(browser, account) + "\\", StringComparison.OrdinalIgnoreCase) && processes.IsRunning(browser.Process))
                throw new IOException($"Close {browser.Process} before cleaning or restoring its privacy data.");
        foreach (var cache in PrivacyCaches)
            if (cache.Process is { } process &&
                file.Path.StartsWith(Path.Combine(ProfileFolder(account, cache.Folder), cache.RelativePath) + "\\", StringComparison.OrdinalIgnoreCase) &&
                processes.IsRunning(process))
                throw new IOException($"Close {process} before cleaning or restoring its privacy data.");
    }

    private string ReadShortcut(string path, ICleanerRegistrySession account) =>
        CleanerAccountEnvironment.Expand(shortcuts.ReadTarget(path), RegistryView.Registry64, account);
}
