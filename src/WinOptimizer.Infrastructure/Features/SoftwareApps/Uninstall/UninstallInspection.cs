using Microsoft.Win32;
using System.Diagnostics;
using WinOptimizer.Core.Features.Cleaner.Models;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.Infrastructure.Features.Cleaner;

namespace WinOptimizer.Infrastructure.Features.SoftwareApps.Uninstall;

/// <summary>Reads running executables without stopping processes or querying unrelated process owners.</summary>
public interface IUninstallProcessSource
{
    IReadOnlyList<string> FindRunningExecutables(IReadOnlyCollection<string> roots, ICollection<string> notices);
}

public sealed class WindowsUninstallProcessSource : IUninstallProcessSource
{
    public IReadOnlyList<string> FindRunningExecutables(IReadOnlyCollection<string> roots, ICollection<string> notices)
    {
        List<string> running = [];
        var executableNames = roots.Where(path => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path != null && roots.Any(root => UninstallResources.Under(path, root))) running.Add(path);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    try
                    {
                        if (!process.HasExited && executableNames.Contains(process.ProcessName))
                        {
                            notices.Add($"{process.ProcessName}: cannot verify the running executable ({ex.Message}).");
                            running.Add(process.ProcessName);
                        }
                    }
                    catch (InvalidOperationException) { /* The process exited during inspection. */ }
                }
            }
        }
        return running;
    }
}

internal sealed record UninstallVerificationContext(UninstallVerification Verification,
    bool NativeSucceeded, bool Deferred, bool ProcessRunning, HashSet<string> Active, HashSet<string> Unknown)
{
    public bool CanDelete(string? sid) => NativeSucceeded && !Deferred && !ProcessRunning &&
        !Active.Contains("machine") && !Unknown.Contains("machine") &&
        (sid == null ? Active.Count == 0 && Unknown.Count == 0 : !Active.Contains(sid) && !Unknown.Contains(sid));
}

/// <summary>Rechecks installations and enumerates only owned locations and exact executable/path references.</summary>
internal sealed class UninstallInspection(ICleanerRegistrySessions sessions, IUninstallPackageSource packages,
    IUninstallScanFolders folders, IUninstallProcessSource processes, ICleanerShortcutReader shortcuts,
    IReadOnlyList<UninstallAccount> accounts, ICollection<string> notices)
{
    private const int MaximumRegistryKeys = 100000;
    private static readonly string[] ReferenceBranches =
    [
        @"Software\Microsoft\Windows\CurrentVersion\App Paths",
        @"Software\Microsoft\Windows\CurrentVersion\Run",
        @"Software\Microsoft\Windows\CurrentVersion\RunOnce",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage",
        @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers",
        @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store",
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache",
        @"Software\Classes\CLSID",
        @"Software\Classes\Applications",
        @"Software\Classes\Directory\shell",
        @"Software\Classes\Directory\shellex",
        @"Software\Classes\*\shell",
        @"Software\Classes\*\shellex"
    ];

    public List<UninstallRegistration> CurrentRegistrations { get; } = [];

    /// <summary>Builds one installation/process snapshot shared by all selected products.</summary>
    public IReadOnlyDictionary<string, UninstallVerificationContext> VerifyAll(IReadOnlyList<UninstallProductPlan> plans,
        IReadOnlyList<UninstallNativeOutcome> outcomes, CancellationToken ct)
    {
        var active = plans.ToDictionary(plan => plan.Application.Id, _ => new HashSet<string>(StringComparer.Ordinal));
        var unknown = plans.ToDictionary(plan => plan.Application.Id, plan => new HashSet<string>(plan.UnreadableAccounts, StringComparer.Ordinal));
        CurrentRegistrations.Clear();
        var packageNames = plans.SelectMany(plan => plan.Application.AppxPackageName ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var account in accounts)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                IReadOnlyList<UninstallPackage> installed = [];
                if (packageNames.Length > 0)
                {
                    try
                    {
                        installed = packages.GetPackages(account.Sid, packageNames);
                        foreach (var package in installed.Where(package => package.Notice != null)) notices.Add(package.Notice!);
                    }
                    catch (Exception ex) when (UninstallResources.IsResourceError(ex) || ex is System.Runtime.InteropServices.COMException)
                    {
                        foreach (var plan in plans.Where(plan => plan.Application.AppxPackageName?.Length > 0)) unknown[plan.Application.Id].Add(account.Sid);
                        notices.Add($"{account.Name}: package inventory unavailable ({ex.Message}).");
                    }
                }
                using var session = sessions.Open(account.Sid);
                if (!session.GetFolderPath(Environment.SpecialFolder.UserProfile).Equals(account.ProfileDirectory, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The account's profile registration changed during uninstall.");
                foreach (var plan in plans)
                    if (installed.Any(package => (plan.Application.AppxPackageName ?? []).Contains(package.Name, StringComparer.OrdinalIgnoreCase)))
                        active[plan.Application.Id].Add(account.Sid);
                CheckRegistrations(plans, session, session.UserPrefix, account.Sid, active, ct);
            }
            catch (Exception ex) when (UninstallResources.IsResourceError(ex) || ex is System.Runtime.InteropServices.COMException)
            {
                foreach (var scope in unknown.Values) scope.Add(account.Sid);
                notices.Add($"{account.Name}: {ex.Message}");
            }
        }
        try
        {
            using var machine = sessions.Open(null);
            CheckRegistrations(plans, machine, "HKEY_LOCAL_MACHINE", "machine", active, ct);
        }
        catch (Exception ex) when (UninstallResources.IsResourceError(ex))
        {
            foreach (var scope in unknown.Values) scope.Add("machine");
            notices.Add(ex.Message);
        }
        var allRoots = plans.SelectMany(plan => plan.ReferencePaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var running = processes.FindRunningExecutables(allRoots, notices);
        Dictionary<string, UninstallVerificationContext> result = [];
        foreach (var plan in plans)
        {
            var outcome = outcomes.SingleOrDefault(outcome => outcome.AppId == plan.Application.Id)
                ?? new UninstallNativeOutcome(plan.Application.Id, false, false, "No native uninstall outcome is available.");
            var id = plan.Application.Id;
            var hasProof = plan.Registrations.Count > 0 || plan.Packages.Values.Any(values => values.Length > 0) || plan.Executables.Count > 0;
            foreach (var (executable, sid) in plan.Executables)
            {
                try { if (UninstallResources.Exists(executable, directory: false)) active[id].Add(sid ?? "machine"); }
                catch (Exception ex) when (UninstallResources.IsResourceError(ex))
                { unknown[id].Add(sid ?? "machine"); notices.Add($"{executable}: {ex.Message}"); }
            }
            var productProcesses = running.Where(path => plan.ReferencePaths.Any(root => UninstallResources.Under(path, root)) ||
                plan.Executables.Keys.Any(executable => Path.GetFileNameWithoutExtension(executable).Equals(path, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (productProcesses.Length > 0) notices.Add($"{plan.Application.Name}: running: {string.Join(", ", productProcesses)}");
            if (!hasProof) unknown[id].Add("machine");
            var state = outcome.Deferred ? UninstallVerificationState.Deferred : active[id].Count > 0 || productProcesses.Length > 0
                ? UninstallVerificationState.StillInstalled : !outcome.Success || unknown[id].Count > 0
                    ? UninstallVerificationState.Unknown : UninstallVerificationState.Removed;
            var detail = outcome.Deferred ? outcome.Message ?? "Removal is pending restart; resources are protected."
                : active[id].Count > 0 ? "Registration or application executable remains: " + string.Join(", ", active[id].Select(AccountName))
                : productProcesses.Length > 0 ? "The application still has a running process."
                : !outcome.Success ? outcome.Message ?? "The native uninstaller did not confirm success."
                : unknown[id].Count > 0 ? "Removal could not be verified in every account."
                : "Package/registration and application executable checks completed across all registered accounts.";
            result[id] = new(new(id, plan.Application.Name, state, detail), outcome.Success, outcome.Deferred,
                productProcesses.Length > 0, active[id], unknown[id]);
        }
        return result;
    }

    private void CheckRegistrations(IReadOnlyList<UninstallProductPlan> plans, ICleanerRegistrySession session, string prefix,
        string scope, IReadOnlyDictionary<string, HashSet<string>> active, CancellationToken ct)
    {
        foreach (var view32 in new[] { false, true })
        {
            var branch = prefix + "\\" + (scope != "machine" && view32 ? @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall" : UninstallResources.UninstallBranch);
            foreach (var child in session.Reader.GetSubKeyNames(branch, view32))
            {
                ct.ThrowIfCancellationRequested();
                var values = session.Reader.GetValues(branch + "\\" + child, view32, ct);
                string Text(string name) => values.FirstOrDefault(value => value.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Data as string ?? string.Empty;
                var sid = scope == "machine" ? null : scope;
                var executable = UninstallResources.ExecutablePath(UninstallResources.ExpandMetadata(Text("DisplayIcon"), session, sid, view32));
                var location = UninstallResources.LocalPath(UninstallResources.ExpandMetadata(Text("InstallLocation"), session, sid, view32));
                CurrentRegistrations.Add(new(scope == "machine" ? null : scope, branch + "\\" + child, view32,
                    Text("DisplayName"), location ?? string.Empty, executable ?? string.Empty));
                foreach (var plan in plans)
                {
                    if (!UninstallResources.MatchesRegistration(plan.Application, child, Text("DisplayName"))) continue;
                    if (executable != null ? UninstallResources.Exists(executable, directory: false) : location == null || UninstallResources.Exists(location, directory: true))
                        active[plan.Application.Id].Add(scope);
                }
            }
        }
    }

    public bool IsSharedRoot(UninstallProductPlan plan, UninstallFileRoot root) => CurrentRegistrations.Any(registration =>
        !UninstallResources.MatchesRegistration(plan.Application, Path.GetFileName(registration.Path), registration.Name) &&
        UninstallResources.LocalPath(registration.InstallDirectory) is { } directory &&
        (UninstallResources.Under(directory, root.Path) || UninstallResources.Under(root.Path, directory)));

    public IReadOnlyList<UninstallEvidence> ScanFiles(UninstallProductPlan plan, IReadOnlyDictionary<string, UninstallVerificationContext>? verifications,
        IProgress<UninstallResidueProgress>? progress, CancellationToken ct)
    {
        var verification = verifications?.GetValueOrDefault(plan.Application.Id);
        Dictionary<string, UninstallEvidence> found = new(StringComparer.OrdinalIgnoreCase);
        void Add(UninstallEvidence evidence) => found.TryAdd(UninstallResources.ResourceIdentity(evidence.Finding), evidence);
        foreach (var root in plan.FileRoots)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(root.Path));
            try
            {
                UninstallResources.ValidateRoot(root, sessions, folders);
                if (IsSharedRoot(plan, root)) { notices.Add($"{root.Path}: shared with another registered product; preserved."); continue; }
                if (root.SingleFile)
                {
                    if (File.Exists(root.Path)) AddFile(root.Path, root);
                    continue;
                }
                if (!Directory.Exists(root.Path)) continue;
                var pending = new Stack<string>();
                pending.Push(root.Path);
                while (pending.TryPop(out var directory))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        CleanerPaths.ValidateDescendant(Path.GetDirectoryName(root.Path)!, directory);
                        var info = new DirectoryInfo(directory);
                        Add(new(Finding(UninstallArtifactKind.Directory, directory, root.Sid, verification, plan,
                            modified: info.LastWriteTimeUtc), root));
                        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                        {
                            ct.ThrowIfCancellationRequested();
                            var attributes = File.GetAttributes(entry);
                            if ((attributes & FileAttributes.ReparsePoint) != 0) { notices.Add($"{entry}: link/junction preserved."); continue; }
                            if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                            else AddFile(entry, root);
                        }
                    }
                    catch (Exception ex) when (UninstallResources.IsResourceError(ex)) { notices.Add($"{directory}: {ex.Message}"); }
                }
            }
            catch (Exception ex) when (UninstallResources.IsResourceError(ex)) { notices.Add($"{root.Path}: {ex.Message}"); }
        }
        return found.Values.ToArray();

        void AddFile(string path, UninstallFileRoot root)
        {
            var info = new FileInfo(path);
            if (root.ShortcutTarget != null && !shortcuts.ReadTarget(path).Equals(root.ShortcutTarget, StringComparison.OrdinalIgnoreCase))
            {
                // Environment-bearing shortcut targets are resolved when checking cleanup, rather than borrowing this process' profile.
                using var account = sessions.Open(root.Sid);
                if (!CleanerAccountEnvironment.Expand(shortcuts.ReadTarget(path), RegistryView.Registry64, account)
                    .Equals(root.ShortcutTarget, StringComparison.OrdinalIgnoreCase)) return;
            }
            var evidence = new CleanerFileTarget(Path.GetDirectoryName(root.Path)!, path, info.Length,
                info.LastWriteTimeUtc, DateTime.UtcNow, root.ShortcutTarget);
            Add(new(Finding(UninstallArtifactKind.File, path, root.Sid, verification, plan,
                bytes: info.Length, modified: info.LastWriteTimeUtc), root, evidence));
        }
    }

    /// <summary>Scans shared history/COM/reference branches once for the entire uninstall batch.</summary>
    public IReadOnlyList<UninstallEvidence> ScanRegistry(IReadOnlyList<UninstallProductPlan> plans,
        IReadOnlyDictionary<string, UninstallVerificationContext>? verifications, CancellationToken ct)
    {
        Dictionary<string, UninstallEvidence> found = new(StringComparer.OrdinalIgnoreCase);
        foreach (var sid in new string?[] { null }.Concat(accounts.Select(account => (string?)account.Sid)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var account = sessions.Open(sid);
                foreach (var plan in plans)
                    foreach (var root in plan.RegistryRoots.Where(root => root.Sid == sid))
                {
                    // Uninstall keys can be reused by an installation that appeared after capture.
                    if (root.Path.Contains(@"\Microsoft\Windows\CurrentVersion\Uninstall\", StringComparison.OrdinalIgnoreCase))
                    {
                        var values = account.Reader.GetValues(root.Path, root.View32, ct, notices);
                        var name = values.FirstOrDefault(value => value.Name.Equals("DisplayName", StringComparison.OrdinalIgnoreCase))?.Data as string;
                        if (name != null && !UninstallResources.MatchesRegistration(plan.Application, Path.GetFileName(root.Path), name))
                        { notices.Add($"{root.Path}: registration identity changed; preserved."); continue; }
                    }
                    WalkRegistry(account, root.Path, root.View32, sid, true, [plan], verifications,
                        evidence => found.TryAdd(UninstallResources.ResourceIdentity(evidence.Finding), evidence), ct);
                }
                foreach (var view32 in new[] { false, true })
                    foreach (var branch in ReferenceBranches)
                    {
                        if (view32 && branch.StartsWith(@"Software\Classes\", StringComparison.OrdinalIgnoreCase) &&
                            !branch.StartsWith(@"Software\Classes\CLSID", StringComparison.OrdinalIgnoreCase)) continue;
                        var relative = sid != null && view32 && !branch.StartsWith(@"Software\Classes\", StringComparison.OrdinalIgnoreCase)
                            ? @"Software\Wow6432Node\" + branch[@"Software\".Length..] : branch;
                        WalkRegistry(account, (sid == null ? "HKEY_LOCAL_MACHINE" : account.UserPrefix) + "\\" + relative,
                            view32, sid, false, plans, verifications,
                            evidence => found.TryAdd(UninstallResources.ResourceIdentity(evidence.Finding), evidence), ct);
                    }
            }
            catch (Exception ex) when (UninstallResources.IsResourceError(ex)) { notices.Add($"{AccountName(sid)}: {ex.Message}"); }
        }
        return found.Values.ToArray();
    }

    private void WalkRegistry(ICleanerRegistrySession account, string root, bool view32, string? sid, bool owned,
        IReadOnlyList<UninstallProductPlan> plans, IReadOnlyDictionary<string, UninstallVerificationContext>? verifications,
        Action<UninstallEvidence> add, CancellationToken ct)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        int count = 0;
        while (pending.TryPop(out var entry))
        {
            ct.ThrowIfCancellationRequested();
            if (++count > MaximumRegistryKeys) { notices.Add($"{root}: registry scan limit reached; coverage is incomplete."); return; }
            try
            {
                if (!account.Reader.KeyExists(entry.Path, view32)) continue;
                if (owned)
                    foreach (var plan in plans)
                        add(new(Finding(UninstallArtifactKind.RegistryKey, entry.Path, sid, verifications?.GetValueOrDefault(plan.Application.Id), plan, view32: view32), Sid: sid));
                foreach (var value in account.Reader.GetValues(entry.Path, view32, ct, notices))
                {
                    var text = value.Data is string[] strings ? string.Join("\n", strings) : value.Data as string ?? string.Empty;
                    var name = value.Name.Replace(".FriendlyAppName", "", StringComparison.OrdinalIgnoreCase)
                        .Replace(".ApplicationCompany", "", StringComparison.OrdinalIgnoreCase);
                    var referenceName = ExpandReference(name, account, view32);
                    var referenceData = ExpandReference(text, account, view32);
                    foreach (var plan in plans)
                    {
                        if (!owned && !UninstallResources.References(referenceName, plan.ReferencePaths) &&
                            !UninstallResources.References(referenceData, plan.ReferencePaths)) continue;
                        var finding = Finding(UninstallArtifactKind.RegistryValue, entry.Path, sid, verifications?.GetValueOrDefault(plan.Application.Id), plan,
                            value.Name, text.Length > 0 ? text : UninstallResources.Serialize(value), view32: view32);
                        if (!UninstallResources.Supported(value.Kind)) finding = finding with { CanDelete = false, Reason = "Unsupported registry value type; preserved." };
                        add(new(finding, RegistryData: UninstallResources.Serialize(value), RegistryKind: (int)value.Kind, Sid: sid));
                    }
                }
                if (entry.Depth >= (owned ? 64 : 5))
                {
                    if (account.Reader.GetSubKeyNames(entry.Path, view32).Length > 0) notices.Add($"{entry.Path}: deeper keys were not scanned.");
                    continue;
                }
                foreach (var child in account.Reader.GetSubKeyNames(entry.Path, view32)) pending.Push((entry.Path + "\\" + child, entry.Depth + 1));
            }
            catch (Exception ex) when (UninstallResources.IsResourceError(ex)) { notices.Add($"{entry.Path}: {ex.Message}"); }
        }
    }

    private string ExpandReference(string value, ICleanerRegistrySession account, bool view32)
    {
        value = CleanerAccountEnvironment.Expand(value, view32 ? RegistryView.Registry32 : RegistryView.Registry64, account);
        // FeatureUsage and compatibility records may encode the Program Files known-folder ID.
        value = value.Replace("{6D809377-6AF0-444B-8957-A3773F02200E}", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase)
            .Replace("{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), StringComparison.OrdinalIgnoreCase);
        return value;
    }

    private UninstallResidue Finding(UninstallArtifactKind kind, string path, string? sid, UninstallVerificationContext? verification,
        UninstallProductPlan plan, string? valueName = null, string data = "", long bytes = 0, DateTime? modified = null, bool view32 = false)
    {
        var canDelete = verification?.CanDelete(sid) == true;
        return new(Guid.NewGuid(), plan.Application.Id, plan.Application.Name, AccountName(sid), kind, path, valueName,
            data, bytes, modified, view32, canDelete, canDelete ? "Product-specific location or exact application path reference."
                : verification?.Verification.Detail ?? "Pre-uninstall inventory.");
    }

    private string AccountName(string? sid) => sid == null || sid == "machine" ? "All accounts (machine)"
        : accounts.FirstOrDefault(account => account.Sid == sid) is { } account ? $"{account.Name} ({sid})" : sid;
}
