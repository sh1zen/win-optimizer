using Microsoft.Win32;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.Infrastructure.Features.Cleaner;

namespace WinOptimizer.Infrastructure.Features.SoftwareApps.Uninstall;

/// <summary>Discovers product-specific locations across machine views and every registered Windows profile.</summary>
internal sealed class UninstallDiscovery(ICleanerRegistrySessions sessions, ICleanerRegistryProfiles profiles,
    IUninstallPackageSource packages, IUninstallScanFolders folders, ICleanerShortcutReader shortcuts)
{
    public List<UninstallAccount> Accounts { get; } = [];
    public List<string> Notices { get; } = [];
    public List<UninstallRegistration> Registrations { get; } = [];
    private readonly HashSet<ScanLocation> _locations = [];
    private readonly Dictionary<string, string[]> _directoryCandidates = new(StringComparer.OrdinalIgnoreCase);
    private sealed record ScanLocation(string Directory, string? Sid, Environment.SpecialFolder? Folder, string BaseDirectory, string? KnownFolderId);

    /// <summary>Revisits the captured search locations so uninstall-created product folders/keys are included.</summary>
    public void RefreshLocations(IReadOnlyList<UninstallProductPlan> plans, CancellationToken ct)
    {
        _directoryCandidates.Clear();
        foreach (var plan in plans) plan.UnreadableAccounts.Clear();
        foreach (var profile in profiles.Read())
            if (!Accounts.Any(account => account.Sid == profile.Sid)) Accounts.Add(new(profile.Sid, profile.DisplayName, profile.Directory));
        ProtectSharedProfiles(plans);
        foreach (var location in _locations.ToArray())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (location.Sid != null)
                {
                    using var account = sessions.Open(location.Sid);
                    var current = UninstallResources.AccountBase(new(location.Directory, location.Sid, location.Folder,
                        location.BaseDirectory, KnownFolderId: location.KnownFolderId), account);
                    if (!current.Equals(location.BaseDirectory, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The account's search directory changed during uninstall.");
                    foreach (var plan in plans)
                        DiscoverDirectories(plan, location.Directory, location.Sid, location.Folder, ct, location.BaseDirectory, location.KnownFolderId, account);
                }
                else foreach (var plan in plans)
                    DiscoverDirectories(plan, location.Directory, null, location.Folder, ct, location.BaseDirectory, location.KnownFolderId);
            }
            catch (Exception ex) when (UninstallResources.IsResourceError(ex))
            {
                Notices.Add($"{location.Directory}: {ex.Message}");
                foreach (var plan in plans) plan.UnreadableAccounts.Add(location.Sid ?? "machine");
            }
        }
        foreach (var sid in new string?[] { null }.Concat(Accounts.Select(account => (string?)account.Sid)))
        {
            try
            {
                using var account = sessions.Open(sid);
                if (sid != null && !account.GetFolderPath(Environment.SpecialFolder.UserProfile).Equals(
                    Accounts.Single(item => item.Sid == sid).ProfileDirectory, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The account's profile registration changed during uninstall.");
                foreach (var plan in plans)
                {
                    DiscoverSoftwareKeys(plan, account, sid, sid == null ? "HKEY_LOCAL_MACHINE" : account.UserPrefix, ct);
                    if (sid == null) continue;
                    foreach (var folder in new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData })
                        DiscoverDirectories(plan, account.GetFolderPath(folder), sid, folder, ct, account: account);
                    var localLow = UninstallResources.LocalLow(account);
                    DiscoverDirectories(plan, localLow, sid, null, ct, localLow, UninstallResources.LocalLowId, account);
                    DiscoverPackageData(plan, account, sid, ct);
                    AddDetectionPaths(plan, account, sid);
                }
            }
            catch (Exception ex) when (UninstallResources.IsResourceError(ex))
            {
                Notices.Add($"{sid ?? "machine"}: {ex.Message}");
                foreach (var plan in plans) plan.UnreadableAccounts.Add(sid ?? "machine");
            }
        }
        foreach (var plan in plans)
        {
            foreach (var root in plan.FileRoots) plan.ReferencePaths.Add(root.Path);
            DiscoverShortcuts(plan, ct);
        }
    }

    private void ProtectSharedProfiles(IReadOnlyList<UninstallProductPlan> plans)
    {
        foreach (var shared in Accounts.GroupBy(account => account.ProfileDirectory, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            Notices.Add($"{shared.Key}: multiple account registrations share this profile directory; these account resources are protected.");
            foreach (var plan in plans) foreach (var account in shared) plan.UnreadableAccounts.Add(account.Sid);
        }
    }

    /// <summary>Finds exact package-family data directories even when that account's registration was removed.</summary>
    private void DiscoverPackageData(UninstallProductPlan plan, ICleanerRegistrySession account, string sid, CancellationToken ct)
    {
        if (plan.Application.AppxPackageName?.Length is not > 0) return;
        var local = account.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var packagesDirectory = Path.Combine(local, "Packages");
        if (!UninstallResources.Exists(packagesDirectory, true)) return;
        CleanerPaths.ValidateDescendant(local, Path.Combine(packagesDirectory, ".uninstall-boundary"));
        foreach (var directory in Directory.EnumerateDirectories(packagesDirectory))
        {
            ct.ThrowIfCancellationRequested();
            var family = Path.GetFileName(directory);
            if (plan.Application.AppxPackageName.Any(name => family.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase)
                && family[(name.Length + 1)..].Length == 13 && family[(name.Length + 1)..].All(char.IsAsciiLetterOrDigit)))
                AddRoot(plan, new(directory, sid, Environment.SpecialFolder.LocalApplicationData, local), account);
        }
    }

    public IReadOnlyList<UninstallProductPlan> Capture(IReadOnlyList<ItemDefinition> applications,
        IProgress<UninstallResidueProgress>? progress, CancellationToken ct)
    {
        var plans = applications.Select(app => new UninstallProductPlan(app with { })).ToArray();
        foreach (var plan in plans)
        {
            UninstallResources.AddName(plan, plan.Application.Name);
            foreach (var id in plan.Application.WinGetPackageId ?? []) UninstallResources.AddName(plan, id.Split('.').Last());
            foreach (var name in plan.Application.AppxPackageName ?? []) UninstallResources.AddName(plan, name.Split('.').Last());
        }
        foreach (var profile in profiles.Read()) Accounts.Add(new(profile.Sid, profile.DisplayName, profile.Directory));
        ProtectSharedProfiles(plans);
        using (var machine = sessions.Open(null))
            ReadRegistrations(machine, null, "HKEY_LOCAL_MACHINE", ct);
        foreach (var account in Accounts)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new($"{account.Name}: registry / AppData", Accounts.IndexOf(account), Accounts.Count));
            try
            {
                using var session = sessions.Open(account.Sid);
                ReadRegistrations(session, account.Sid, session.UserPrefix, ct);
                IReadOnlyList<UninstallPackage> installed = [];
                var packageNames = plans.SelectMany(plan => plan.Application.AppxPackageName ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (packageNames.Length > 0)
                {
                    try
                    {
                        installed = packages.GetPackages(account.Sid, packageNames);
                        foreach (var package in installed.Where(package => package.Notice != null)) Notices.Add(package.Notice!);
                    }
                    catch (Exception ex) when (UninstallResources.IsResourceError(ex) || ex is System.Runtime.InteropServices.COMException)
                    {
                        Notices.Add($"{account.Name}: package inventory unavailable ({ex.Message}).");
                        foreach (var plan in plans.Where(plan => plan.Application.AppxPackageName?.Length > 0)) plan.UnreadableAccounts.Add(account.Sid);
                    }
                }
                foreach (var plan in plans)
                {
                    plan.Packages[account.Sid] = installed.Where(package => (plan.Application.AppxPackageName ?? [])
                        .Contains(package.Name, StringComparer.OrdinalIgnoreCase)).ToArray();
                    foreach (var package in plan.Packages[account.Sid])
                        if (UninstallResources.LocalPath(package.InstallDirectory) is { } installDirectory) plan.ReferencePaths.Add(installDirectory);
                    foreach (var folder in new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData })
                    {
                        var directory = session.GetFolderPath(folder);
                        DiscoverDirectories(plan, directory, account.Sid, folder, ct, account: session);
                        if (folder == Environment.SpecialFolder.LocalApplicationData)
                            foreach (var package in plan.Packages[account.Sid])
                            {
                                if (!package.FamilyName.StartsWith(package.Name + "_", StringComparison.OrdinalIgnoreCase) ||
                                    !System.Text.RegularExpressions.Regex.IsMatch(package.FamilyName, @"\A[A-Za-z0-9][A-Za-z0-9.\-]+_[A-Za-z0-9]+\z",
                                        System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                                {
                                    plan.UnreadableAccounts.Add(account.Sid);
                                    Notices.Add($"{package.Name}: invalid package family identity; account package data is protected.");
                                    continue;
                                }
                                AddRoot(plan, new(Path.Combine(directory, "Packages", package.FamilyName), account.Sid, folder, directory), session);
                            }
                    }
                    var localLow = UninstallResources.LocalLow(session);
                    DiscoverDirectories(plan, localLow, account.Sid, null, ct, localLow, UninstallResources.LocalLowId, session);
                    AddDetectionPaths(plan, session, account.Sid);
                    DiscoverPackageData(plan, session, account.Sid, ct);
                    DiscoverSoftwareKeys(plan, session, account.Sid, session.UserPrefix, ct);
                }
            }
            catch (Exception ex) when (UninstallResources.IsResourceError(ex) || ex is System.Runtime.InteropServices.COMException)
            {
                Notices.Add($"{account.Name} ({account.Sid}): {ex.Message}");
                foreach (var plan in plans) plan.UnreadableAccounts.Add(account.Sid);
            }
        }
        foreach (var plan in plans)
        {
            foreach (var registration in Registrations.Where(reg => UninstallResources.MatchesRegistration(plan.Application,
                Path.GetFileName(reg.Path), reg.Name)))
            {
                plan.Registrations.Add(registration);
                UninstallResources.AddName(plan, registration.Name);
                plan.RegistryRoots.Add(new(registration.Sid, registration.Path, registration.View32));
                if (UninstallResources.LocalPath(registration.InstallDirectory) is { } directory)
                    AddRoot(plan, RegistrationRoot(registration.Sid, directory));
                if (UninstallResources.LocalPath(registration.Executable) is { } executable)
                {
                    plan.ReferencePaths.Add(executable);
                    if (UninstallResources.Exists(executable, directory: false)) plan.Executables[executable] = registration.Sid;
                    UninstallResources.AddName(plan, Path.GetFileNameWithoutExtension(executable));
                    if (string.IsNullOrWhiteSpace(registration.InstallDirectory))
                    {
                        var ownedDirectory = ProductDirectory(plan, executable);
                        var root = RegistrationRoot(registration.Sid, ownedDirectory ?? executable);
                        AddRoot(plan, root with { SingleFile = ownedDirectory == null });
                    }
                }
            }
            using var machine = sessions.Open(null);
            foreach (var directory in folders.ApplicationDirectories) DiscoverDirectories(plan, directory, null, null, ct);
            AddDetectionPaths(plan, machine, null);
            DiscoverSoftwareKeys(plan, machine, null, "HKEY_LOCAL_MACHINE", ct);
            // Registrations can add a more precise product name than the catalogue caption.
            foreach (var account in Accounts.Where(account => !plan.UnreadableAccounts.Contains(account.Sid)))
            {
                using var session = sessions.Open(account.Sid);
                foreach (var folder in new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData })
                    DiscoverDirectories(plan, session.GetFolderPath(folder), account.Sid, folder, ct, account: session);
                DiscoverSoftwareKeys(plan, session, account.Sid, session.UserPrefix, ct);
            }
            foreach (var root in plan.FileRoots.Where(root => !root.SingleFile)) plan.ReferencePaths.Add(root.Path);
            DiscoverShortcuts(plan, ct);
        }
        return plans;
    }

    private UninstallFileRoot RegistrationRoot(string? sid, string directory)
    {
        if (sid != null)
        {
            var account = Accounts.Single(account => account.Sid == sid);
            if (UninstallResources.Under(directory, account.ProfileDirectory))
                return new(directory, sid, Environment.SpecialFolder.UserProfile, account.ProfileDirectory);
        }
        return new(directory, null, null, Path.GetPathRoot(directory)!);
    }

    private void ReadRegistrations(ICleanerRegistrySession session, string? sid, string prefix, CancellationToken ct)
    {
        foreach (var view32 in new[] { false, true })
        {
            var parent = prefix + "\\" + (sid != null && view32 ? @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall" : UninstallResources.UninstallBranch);
            foreach (var name in session.Reader.GetSubKeyNames(parent, view32))
            {
                ct.ThrowIfCancellationRequested();
                var path = parent + "\\" + name;
                try
                {
                    var values = session.Reader.GetValues(path, view32, ct, Notices);
                    string Text(string value) => values.FirstOrDefault(item => item.Name.Equals(value, StringComparison.OrdinalIgnoreCase))?.Data as string ?? string.Empty;
                    var displayName = Text("DisplayName");
                    if (displayName.Length == 0) continue;
                    Registrations.Add(new(sid, path, view32, displayName,
                        UninstallResources.ExpandMetadata(Text("InstallLocation"), session, sid, view32),
                        UninstallResources.ExecutablePath(UninstallResources.ExpandMetadata(Text("DisplayIcon"), session, sid, view32)) ?? string.Empty));
                }
                catch (Exception ex) when (UninstallResources.IsResourceError(ex)) { Notices.Add($"{path}: {ex.Message}"); }
            }
        }
    }

    private void AddDetectionPaths(UninstallProductPlan plan, ICleanerRegistrySession session, string? sid)
    {
        foreach (var configured in plan.Application.DetectionPaths ?? [])
        {
            try
            {
                var expanded = CleanerAccountEnvironment.Expand(configured, RegistryView.Registry64, session);
                if (UninstallResources.LocalPath(expanded) is not { } path) continue;
                // Per-user paths are expanded for that user; machine paths are scanned once.
                var profile = sid == null ? null : session.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (sid != null && !UninstallResources.Under(path, profile!)) continue;
                if (sid == null && configured.Contains("%USERPROFILE%", StringComparison.OrdinalIgnoreCase) ||
                    sid == null && (configured.Contains("%LOCALAPPDATA%", StringComparison.OrdinalIgnoreCase) || configured.Contains("%APPDATA%", StringComparison.OrdinalIgnoreCase))) continue;
                plan.ReferencePaths.Add(path);
                var file = UninstallResources.Exists(path, directory: false) || !UninstallResources.Exists(path, directory: true) && new[] { ".exe", ".dll", ".lnk" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
                if (UninstallResources.Exists(path, directory: false) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) plan.Executables[path] = sid;
                var directory = file ? ProductDirectory(plan, path) : path;
                AddRoot(plan, new(directory ?? path, sid, sid == null ? null : Environment.SpecialFolder.UserProfile,
                    profile ?? Path.GetPathRoot(path)!, file && directory == null), session);
            }
            catch (Exception ex) when (UninstallResources.IsResourceError(ex)) { Notices.Add($"{plan.Application.Name}: {ex.Message}"); }
        }
    }

    private void AddRoot(UninstallProductPlan plan, UninstallFileRoot root, ICleanerRegistrySession? account = null)
    {
        try
        {
            UninstallResources.ValidateRoot(root, sessions, folders, account);
            if (Registrations.Any(reg => !plan.Registrations.Contains(reg) && !UninstallResources.MatchesRegistration(plan.Application,
                Path.GetFileName(reg.Path), reg.Name) && UninstallResources.LocalPath(reg.InstallDirectory) is { } other &&
                (UninstallResources.Under(other, root.Path) || UninstallResources.Under(root.Path, other))))
            {
                Notices.Add($"{root.Path}: shared with another registered product; preserved.");
                return;
            }
            if (!plan.FileRoots.Any(existing => existing.Sid == root.Sid && existing.Path.Equals(root.Path, StringComparison.OrdinalIgnoreCase)))
                plan.FileRoots.Add(root);
        }
        catch (Exception ex) when (UninstallResources.IsResourceError(ex)) { Notices.Add($"{root.Path}: {ex.Message}"); }
    }

    private void DiscoverDirectories(UninstallProductPlan plan, string directory, string? sid,
        Environment.SpecialFolder? folder, CancellationToken ct, string? baseDirectory = null, string? knownFolder = null,
        ICleanerRegistrySession? account = null)
    {
        _locations.Add(new(directory, sid, folder, baseDirectory ?? directory, knownFolder));
        if (!_directoryCandidates.TryGetValue(directory, out var candidates))
            _directoryCandidates[directory] = candidates = ReadProductDirectories(directory, ct);
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (plan.Names.Contains(UninstallResources.NormalizeName(Path.GetFileName(candidate))))
                AddRoot(plan, new(candidate, sid, folder, baseDirectory ?? directory, KnownFolderId: knownFolder), account);
        }
    }

    /// <summary>Reads product/vendor directory candidates once per location and scan phase for the whole batch.</summary>
    private string[] ReadProductDirectories(string directory, CancellationToken ct)
    {
        List<string> candidates = [];
        try
        {
            if (!UninstallResources.Exists(directory, directory: true)) return [];
            CleanerPaths.ValidateDescendant(Path.GetPathRoot(directory)!, Path.Combine(directory, ".uninstall-boundary"));
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                ct.ThrowIfCancellationRequested();
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                candidates.Add(child);
                try
                {
                    foreach (var grandchild in Directory.EnumerateDirectories(child))
                    {
                        ct.ThrowIfCancellationRequested();
                        if ((File.GetAttributes(grandchild) & FileAttributes.ReparsePoint) == 0) candidates.Add(grandchild);
                    }
                }
                catch (Exception ex) when (UninstallResources.IsResourceError(ex)) { Notices.Add($"{child}: {ex.Message}"); }
            }
        }
        catch (Exception ex) when (UninstallResources.IsResourceError(ex)) { Notices.Add($"{directory}: {ex.Message}"); }
        return candidates.ToArray();
    }

    private void DiscoverSoftwareKeys(UninstallProductPlan plan, ICleanerRegistrySession session, string? sid, string prefix, CancellationToken ct)
    {
        foreach (var view32 in new[] { false, true })
        {
            var software = prefix + (sid != null && view32 ? @"\Software\Wow6432Node" : @"\Software");
            foreach (var name in session.Reader.GetSubKeyNames(software, view32))
            {
                ct.ThrowIfCancellationRequested();
                var path = software + "\\" + name;
                if (plan.Names.Contains(UninstallResources.NormalizeName(name))) AddRegistryRoot(plan, new(sid, path, view32));
                else
                {
                    try
                    {
                        foreach (var child in session.Reader.GetSubKeyNames(path, view32))
                            if (plan.Names.Contains(UninstallResources.NormalizeName(child))) AddRegistryRoot(plan, new(sid, path + "\\" + child, view32));
                    }
                    catch (Exception ex) when (UninstallResources.IsResourceError(ex)) { Notices.Add($"{path}: {ex.Message}"); }
                }
            }
        }
    }

    private static void AddRegistryRoot(UninstallProductPlan plan, UninstallRegistryRoot root)
    {
        if (!plan.RegistryRoots.Contains(root)) plan.RegistryRoots.Add(root);
    }

    private void DiscoverShortcuts(UninstallProductPlan plan, CancellationToken ct)
    {
        using var machine = sessions.Open(null);
        foreach (var directory in folders.ShortcutDirectories) ReadShortcuts(directory, null, null, directory, machine);
        foreach (var account in Accounts.Where(account => !plan.UnreadableAccounts.Contains(account.Sid)))
        {
            try
            {
                using var session = sessions.Open(account.Sid);
                foreach (var folder in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.Programs })
                {
                    var directory = session.GetFolderPath(folder);
                    ReadShortcuts(directory, account.Sid, folder, directory, session);
                }
            }
            catch (Exception ex) when (UninstallResources.IsResourceError(ex)) { Notices.Add($"{account.Name}: {ex.Message}"); }
        }

        void ReadShortcuts(string directory, string? sid, Environment.SpecialFolder? folder, string baseDirectory, ICleanerRegistrySession? session = null)
        {
            foreach (var file in CleanerPaths.EnumerateFiles(directory, Notices, ct).Where(path => path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var target = shortcuts.ReadTarget(file);
                    if (target == null) continue;
                    if (session != null) target = CleanerAccountEnvironment.Expand(target, RegistryView.Registry64, session);
                    if (plan.ReferencePaths.Any(path => UninstallResources.Under(target, path)))
                        AddRoot(plan, new(file, sid, folder, baseDirectory, true, target), session);
                }
                catch (Exception ex) when (UninstallResources.IsResourceError(ex) || ex is System.Runtime.InteropServices.COMException) { Notices.Add($"{file}: {ex.Message}"); }
            }
        }
    }

    private static string? ProductDirectory(UninstallProductPlan plan, string executable)
    {
        for (var directory = Path.GetDirectoryName(executable); directory != null; directory = Path.GetDirectoryName(directory))
            if (plan.Names.Contains(UninstallResources.NormalizeName(Path.GetFileName(directory)))) return directory;
        return null;
    }
}
