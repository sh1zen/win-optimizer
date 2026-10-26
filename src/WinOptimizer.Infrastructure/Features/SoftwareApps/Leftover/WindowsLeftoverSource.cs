using Microsoft.Win32;
using WinOptimizer.Core.Features.Cleaner.Models;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.Infrastructure.Features.Cleaner;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Uninstall;

namespace WinOptimizer.Infrastructure.Features.SoftwareApps.Leftover;

internal sealed record LeftoverCandidate(string Identity, LeftoverItem Item, IReadOnlyList<UninstallEvidence> Evidence)
{
    public string Fingerprint => string.Join("\n", Evidence.OrderBy(entry => entry.Finding.Path, StringComparer.OrdinalIgnoreCase)
        .ThenBy(entry => entry.Finding.ValueName, StringComparer.OrdinalIgnoreCase).Select(entry =>
            $"{UninstallResources.PhysicalIdentity(entry)}|{entry.RegistryKind}|{entry.RegistryData}|{entry.File?.Length}|{entry.File?.LastWriteUtc.Ticks}|{entry.Finding.ModifiedUtc?.Ticks}"));
}
internal sealed record LeftoverInventory(IReadOnlyList<LeftoverCandidate> Candidates, IReadOnlyList<string> Notices);
internal interface ILeftoverSource { LeftoverInventory Read(CancellationToken ct); }

/// <summary>Finds selectable stale registrations, owned installation remnants and aged unreferenced repair-cache files.</summary>
internal sealed class WindowsLeftoverSource(IUninstallAccountSessions sessions, IUninstallScanFolders folders,
    IInstallerInventory installer, TimeProvider? clock = null) : ILeftoverSource
{
    public LeftoverInventory Read(CancellationToken ct)
    {
        var references = installer.Read(ct);
        List<string> notices = [.. references.Notices];
        List<LeftoverCandidate> candidates = [];
        List<LeftoverDirectory> directories = [];
        List<UninstallFileRoot> searchRoots = folders.ApplicationDirectories.Select(directory =>
        {
            var path = UninstallResources.LocalPath(directory) ?? throw new IOException("An application scan root is not a resolved local directory.");
            return new UninstallFileRoot(path, null, null, path);
        }).ToList();
        var installationRoots = folders.InstallationDirectories.Select(path => UninstallResources.LocalPath(path) ??
            throw new IOException("An installation root is not a resolved local directory.")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<UninstallFileRoot> emptyRoots = searchRoots.Where(root => installationRoots.Contains(root.Path)).ToList();
        HashSet<string> activeDirectories = new(StringComparer.OrdinalIgnoreCase);
        HashSet<(string? Sid, string Name, string? Location)> activeProducts = [];
        HashSet<string> metadataReferences = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> activeMetadataReferences = new(StringComparer.OrdinalIgnoreCase);
        var complete = references.Complete;
        // MSI's all-user inventory owns the system cache; ARP profile coverage also governs bundle caches and app folders.
        var systemCacheComplete = references.Complete;
        bool directoriesComplete = true;
        bool? restartPending = null;
        HashSet<string> pendingPaths = new(StringComparer.OrdinalIgnoreCase);
        var accounts = new List<string?> { null };
        try { accounts.AddRange(sessions.GetAccounts().Select(account => account.Sid)); }
        catch (Exception ex) when (UninstallResources.IsResourceError(ex)) { notices.Add(ex.Message); complete = false; }
        foreach (var sid in accounts.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var account = sessions.Open(sid);
                if (sid != null)
                {
                    foreach (var folder in new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData })
                    {
                        var directory = UninstallResources.LocalPath(account.GetFolderPath(folder)) ??
                            throw new IOException($"The account's {folder} scan root is not a resolved local directory.");
                        searchRoots.Add(new(directory, sid, folder, directory));
                        if (folder == Environment.SpecialFolder.LocalApplicationData)
                            emptyRoots.Add(new(Path.Combine(directory, "Programs"), sid, folder, directory));
                    }
                    var localLow = UninstallResources.LocalLow(account);
                    searchRoots.Add(new(localLow, sid, null, localLow, KnownFolderId: UninstallResources.LocalLowId));
                }
                if (sid == null)
                {
                    var hasRestartMarker = account.Reader.KeyExists(@"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Installer\InProgress", false) ||
                        account.Reader.KeyExists(@"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending", false) ||
                        account.Reader.KeyExists(@"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired", false);
                    pendingPaths.UnionWith(ReadPendingFilePaths(account.Reader, ct));
                    // Both ends of a queued operation protect overlapping cache files and application trees.
                    references.SourceDirectories.UnionWith(pendingPaths);
                    restartPending = hasRestartMarker;
                }
                foreach (var view32 in new[] { false, true })
                {
                    var branch = (sid == null ? "HKEY_LOCAL_MACHINE" : account.UserPrefix) + "\\" +
                        (sid != null && view32 ? @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" : UninstallResources.UninstallBranch);
                    foreach (var key in account.Reader.GetSubKeyNames(branch, view32))
                    {
                        ct.ThrowIfCancellationRequested();
                        var path = branch + "\\" + key;
                        try
                        {
                            var values = account.Reader.GetValues(path, view32, ct).ToDictionary(value => value.Name, StringComparer.OrdinalIgnoreCase);
                            string Text(string name) => values.TryGetValue(name, out var value) && value.Data is string text ? text : "";
                            bool Flag(string name) => values.TryGetValue(name, out var value) && value.Data is int number && number == 1;
                            string Expand(string value) => UninstallResources.ExpandMetadata(value, account, sid, view32);
                            List<string> registrationReferences = [];
                            string? icon = null, command = null;
                            bool validReferences = true;
                            // Hidden registrations can own repair caches too. Read references before filtering display entries.
                            foreach (var field in new[] { "UninstallString", "QuietUninstallString", "ModifyPath", "RepairPath", "DisplayIcon", "BundleCachePath", "InstallSource" })
                            {
                                if (values.TryGetValue(field, out var value) && value.Data is not string)
                                { validReferences = complete = systemCacheComplete = false; notices.Add($"{path}: {field} has an invalid registry value type."); continue; }
                                var raw = Text(field);
                                if (string.IsNullOrWhiteSpace(raw)) continue;
                                var expanded = Expand(raw);
                                registrationReferences.Add(expanded);
                                metadataReferences.Add(expanded);
                                var target = field == "DisplayIcon" ? CleanerRegistryScanner.ExtractIconPath(expanded) :
                                    field is "UninstallString" or "QuietUninstallString" or "ModifyPath" or "RepairPath" ? CleanerRegistryScanner.ExtractCommandPath(expanded) :
                                    UninstallResources.LocalPath(expanded);
                                if (field == "DisplayIcon") icon = target;
                                else if (field == "UninstallString") command = target;
                                if (target == null)
                                {
                                    // Relative launchers do not prove cache ownership; unresolved cache paths block cache deletion.
                                    if (field is "BundleCachePath" or "InstallSource" ||
                                        expanded.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase) ||
                                        expanded.Contains(@"\Installer\", StringComparison.OrdinalIgnoreCase))
                                    { validReferences = complete = systemCacheComplete = false; notices.Add($"{path}: the installer reference in {field} could not be resolved."); }
                                    continue;
                                }
                                target = CleanerFileDeletion.ResolveReferencePath(target);
                                if (field == "InstallSource") references.SourceDirectories.Add(target);
                                else if (field == "BundleCachePath") references.SourceDirectories.Add(Path.GetDirectoryName(target)!);
                                else references.Files.Add(target);
                            }
                            var rawLocation = Expand(Text("InstallLocation"));
                            if (!string.IsNullOrWhiteSpace(rawLocation)) metadataReferences.Add(rawLocation);
                            var location = UninstallResources.LocalPath(rawLocation);
                            var name = Text("DisplayName");
                            // Every preserved registration still protects its readable ownership evidence.
                            void ProtectRegistration()
                            {
                                var activeLocation = location == null ? null : CleanerFileDeletion.ResolveReferencePath(location);
                                if (!string.IsNullOrWhiteSpace(name)) activeProducts.Add((sid, UninstallResources.NormalizeName(name), activeLocation));
                                activeMetadataReferences.UnionWith(registrationReferences);
                                if (activeLocation != null) activeDirectories.Add(activeLocation);
                            }
                            if (values.TryGetValue("InstallLocation", out var locationValue) && locationValue.Data is not string ||
                                !string.IsNullOrWhiteSpace(rawLocation) && location == null)
                            {
                                directoriesComplete = false;
                                notices.Add($"{path}: installation-directory metadata could not be resolved; application folders were excluded.");
                                ProtectRegistration();
                                continue;
                            }
                            var invalidField = new[] { "DisplayName", "ParentKeyName", "ReleaseType" }
                                .FirstOrDefault(field => values.TryGetValue(field, out var value) && value.Data is not string)
                                ?? new[] { "WindowsInstaller", "SystemComponent" }
                                    .FirstOrDefault(field => values.TryGetValue(field, out var value) && value.Data is not int);
                            if (!validReferences || invalidField != null)
                            {
                                if (invalidField != null)
                                {
                                    directoriesComplete = false;
                                    notices.Add($"{path}: {invalidField} has an invalid registry value type; application folders were excluded.");
                                }
                                ProtectRegistration();
                                continue;
                            }
                            if (string.IsNullOrWhiteSpace(rawLocation) && icon != null && command != null)
                            {
                                // Two explicit executable paths establish a common install boundary without optional ARP metadata.
                                var common = Path.GetDirectoryName(icon);
                                while (common != null && !UninstallResources.Under(command, common)) common = Path.GetDirectoryName(common);
                                if (common != null && LeftoverDirectoryScanner.FindRoot(common, sid, searchRoots) != null) location = common;
                            }
                            bool msi = Flag("WindowsInstaller");
                            if (string.IsNullOrWhiteSpace(name) || Flag("SystemComponent") ||
                                !string.IsNullOrWhiteSpace(Text("ParentKeyName")) || !string.IsNullOrWhiteSpace(Text("ReleaseType")) ||
                                !IsStale(location, icon, command, msi, key, references))
                            {
                                ProtectRegistration();
                                continue;
                            }
                            string[] productPaths = pendingPaths.Count == 0 ? [] : new[] { location, icon, command }
                                .OfType<string>().Select(CleanerFileDeletion.ResolveReferencePath).ToArray();
                            if (references.Busy || restartPending != false || pendingPaths.Any(path => productPaths.Any(productPath =>
                                UninstallResources.Under(path, productPath) || UninstallResources.Under(productPath, path)))) continue;
                            var identity = $"registry:{sid}:{view32}:{path}";
                            bool directoryMissing = location == null || CleanerPaths.IsMissingLocalFile(location);
                            var item = new LeftoverItem(Guid.NewGuid(), name, LeftoverKind.Registration, path, 0, true,
                                msi ? "Windows Installer no longer registers this product." :
                                    icon != null ? "The application executable and uninstaller are missing." :
                                    directoryMissing ? "The registered installation directory and uninstaller are missing." :
                                    "The registered uninstaller is missing.");
                            List<UninstallEvidence> evidence = [];
                            CaptureRegistration(account.Reader, path, sid, view32, item, evidence, 0, ct);
                            if (directoryMissing) candidates.Add(new(identity, item, evidence));
                            else if ((icon == null || UninstallResources.Under(icon, location!)) &&
                                (msi || command != null && UninstallResources.Under(command, location!)) &&
                                LeftoverDirectoryScanner.FindRoot(location!, sid, searchRoots) is { } root)
                                directories.Add(new(name, root, evidence, item.Reason));
                            else activeDirectories.Add(CleanerFileDeletion.ResolveReferencePath(location!));
                        }
                        catch (Exception ex) when (UninstallResources.IsResourceError(ex))
                        { complete = systemCacheComplete = false; notices.Add($"{path}: {ex.Message}"); }
                    }
                }
            }
            catch (Exception ex) when (UninstallResources.IsResourceError(ex))
            { complete = false; notices.Add($"{sid ?? "Machine"}: {ex.Message}"); }
        }
        if (!complete) notices.Add(systemCacheComplete
            ? "Bundle caches and application folders containing data were excluded because account coverage is incomplete. Windows Installer cache files use the complete native product/patch inventory; empty installation folders are checked separately."
            : "Installer files and application folders containing data were excluded because reference coverage is incomplete. Empty installation folders are checked separately. Review the scan errors and scan again.");
        if (references.Busy || restartPending == true) notices.Add("An installation or restart is pending; installer files stay protected until it finishes.");
        else if (restartPending == null) notices.Add("Installation or restart status could not be verified; installer files stay protected. Review the scan errors and scan again.");
        var cutoff = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime.AddDays(-30);
        if (!references.Busy && restartPending == false)
        {
            LeftoverDirectoryScanner.DiscoverEmpty(emptyRoots, directories, notices, ct);
            foreach (var directory in directories)
            {
                var productName = UninstallResources.NormalizeName(directory.Name);
                if (activeProducts.Any(product => product.Name == productName &&
                    (directory.Root.Sid == null || product.Sid == null || product.Sid == directory.Root.Sid) &&
                    (!directory.EmptyOnly || product.Location == null || UninstallResources.Under(directory.Root.Path, product.Location) ||
                        UninstallResources.Under(product.Location, directory.Root.Path))) ||
                    activeMetadataReferences.Any(value => UninstallResources.References(value, [directory.Root.Path])))
                    activeDirectories.Add(directory.Root.Path);
            }
            candidates.AddRange(LeftoverDirectoryScanner.Read(
                directories.Where(directory => complete && directoriesComplete || directory.EmptyOnly).ToArray(),
                activeDirectories, references, cutoff, notices, ct));
        }
        var windowsCache = Path.Combine(folders.WindowsDirectory, "Installer");
        var roots = new[] { new UninstallFileRoot(windowsCache, null, null, windowsCache, InstallerCache: true) }
            .Concat(searchRoots.Where(root => root.Sid == null || root.Folder == Environment.SpecialFolder.LocalApplicationData)
                .Select(root => root with { Path = Path.Combine(root.Path, "Package Cache"), BaseDirectory = Path.Combine(root.Path, "Package Cache"), InstallerCache = true }));
        foreach (var cache in roots.GroupBy(root => root.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (cache.Select(root => root.Sid).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            { notices.Add($"{cache.Key}: the installer cache is shared by multiple account scopes; it was preserved."); continue; }
            var boundary = cache.First();
            var root = boundary.Path;
            var systemCache = root.Equals(windowsCache, StringComparison.OrdinalIgnoreCase);
            foreach (var path in CleanerPaths.EnumerateFiles(root, notices, ct, recursive: !systemCache))
            {
                ct.ThrowIfCancellationRequested();
                var extension = Path.GetExtension(path).ToLowerInvariant();
                if (extension == ".exe" && systemCache) continue;
                if (extension is not (".msi" or ".msp" or ".exe") ||
                    references.Files.Contains(path) || references.SourceDirectories.Any(directory => UninstallResources.Under(path, directory)) ||
                    activeDirectories.Any(directory => UninstallResources.Under(path, directory)) ||
                    HasMetadataReference(path, root, metadataReferences)) continue;
                try
                {
                    var file = new FileInfo(path);
                    var written = file.LastWriteTimeUtc;
                    var accessed = file.LastAccessTimeUtc;
                    if (!(systemCache ? systemCacheComplete : complete) || references.Busy || restartPending != false ||
                        written > cutoff || accessed > cutoff) continue;
                    var item = new LeftoverItem(Guid.NewGuid(), file.Name, extension switch
                    { ".msi" => LeftoverKind.Msi, ".msp" => LeftoverKind.Msp, _ => LeftoverKind.Exe }, path, file.Length, true,
                        "The complete ownership inventory does not require this cache file; it has not been used for at least 30 days.");
                    var target = new CleanerFileTarget(Path.GetDirectoryName(path)!, path, file.Length, written, cutoff);
                    var finding = Finding(item, path, UninstallArtifactKind.File, null, item.Bytes, written, false);
                    candidates.Add(new("file:" + path, item, [new(finding, boundary with { Path = path, SingleFile = true }, target)]));
                }
                catch (Exception ex) when (UninstallResources.IsResourceError(ex)) { notices.Add($"{path}: {ex.Message}"); }
            }
        }
        return new(candidates, notices.Distinct().ToArray());
    }

    /// <summary>Uses native MSI absence or a missing owned uninstaller; existing installation trees require full payload checks.</summary>
    private static bool IsStale(string? location, string? executable, string? command, bool msi, string product,
        InstallerReferences references) =>
        !references.Products.Contains(product) && (msi ? references.Complete && Guid.TryParse(product, out _) :
            location != null && command != null && Path.GetExtension(command).Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
            (executable != null ? Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase) && CleanerPaths.IsMissingLocalFile(executable) :
                UninstallResources.Under(command, location)) && CleanerPaths.IsMissingLocalFile(command));

    /// <summary>Protects explicit file or containing-directory references in installer command arguments and metadata.</summary>
    private static bool HasMetadataReference(string path, string root, IReadOnlyCollection<string> values)
    {
        for (string? current = path; current != null && UninstallResources.Under(current, root); current = Path.GetDirectoryName(current))
            if (values.Any(value => UninstallResources.References(value, [current], includeDescendants: false))) return true;
        return false;
    }

    /// <summary>Reads queued rename/delete pairs as resource references, preserving uncertainty as a scan error.</summary>
    private static IReadOnlyList<string> ReadPendingFilePaths(ICleanerRegistryReader reader, CancellationToken ct)
    {
        List<string> paths = [];
        foreach (var value in reader.GetValues(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager", false, ct)
            .Where(value => value.Name.Equals("PendingFileRenameOperations", StringComparison.OrdinalIgnoreCase) ||
                value.Name.Equals("PendingFileRenameOperations2", StringComparison.OrdinalIgnoreCase)))
        {
            if (value.Kind != RegistryValueKind.MultiString || value.Data is not string[] entries)
                throw new IOException($"{value.Name}: queued file operations have an invalid registry value type.");
            if (entries.All(string.IsNullOrEmpty)) continue;
            if (entries.Length % 2 != 0)
                throw new IOException($"{value.Name}: queued file operations contain an incomplete source/destination pair.");
            for (var index = 0; index < entries.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                var path = entries[index];
                if (string.IsNullOrEmpty(path))
                {
                    if (index % 2 == 1) continue; // Empty destination means delete.
                    throw new IOException($"{value.Name}: a queued file operation has no source path.");
                }
                if (index % 2 == 1 && path.StartsWith('!')) path = path[1..]; // Replace an existing destination.
                if (path.StartsWith('*'))
                {
                    var prefix = 1;
                    while (prefix < path.Length && char.IsAsciiDigit(path[prefix])) prefix++;
                    if (prefix > 1 && path[prefix..].StartsWith(@"\??\", StringComparison.Ordinal)) path = path[prefix..];
                }
                if (path.StartsWith(@"\??\", StringComparison.Ordinal)) path = path[4..];
                var local = UninstallResources.LocalPath(path) ??
                    throw new IOException($"{value.Name}: a queued file operation path could not be resolved locally.");
                paths.Add(CleanerFileDeletion.ResolveReferencePath(local));
            }
        }
        return paths;
    }

    /// <summary>Captures every typed value and child key; deletion later removes only backed-up values and empty keys.</summary>
    private static void CaptureRegistration(ICleanerRegistryReader reader, string path, string? sid, bool view32,
        LeftoverItem item, List<UninstallEvidence> evidence, int depth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (depth > 16 || evidence.Count > 4096) throw new IOException("The registration is too large for bounded leftover cleanup.");
        foreach (var value in reader.GetValues(path, view32, ct))
        {
            if (evidence.Count >= 4096) throw new IOException("The registration is too large for bounded leftover cleanup.");
            evidence.Add(new(Finding(item, path, UninstallArtifactKind.RegistryValue, value.Name, 0, null, view32),
                RegistryData: UninstallResources.Serialize(value), RegistryKind: (int)value.Kind, Sid: sid));
        }
        foreach (var child in reader.GetSubKeyNames(path, view32))
            CaptureRegistration(reader, path + "\\" + child, sid, view32, item, evidence, depth + 1, ct);
        if (evidence.Count >= 4096) throw new IOException("The registration is too large for bounded leftover cleanup.");
        evidence.Add(new(Finding(item, path, UninstallArtifactKind.RegistryKey, null, 0, null, view32), Sid: sid));
    }

    private static UninstallResidue Finding(LeftoverItem item, string path, UninstallArtifactKind kind, string? value,
        long bytes, DateTime? modified, bool view32) => new(Guid.NewGuid(), item.Id.ToString("N"), item.Name, "Leftover", kind,
            path, value, "", bytes, modified, view32, item.CanRemove, item.Reason);
}
