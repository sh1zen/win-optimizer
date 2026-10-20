using System.Collections.Concurrent;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.Infrastructure.Features.Cleaner;
using WinOptimizer.Core.Features.Common.Interfaces;

namespace WinOptimizer.Infrastructure.Features.SoftwareApps.Uninstall;

/// <summary>Owns all-account uninstall inventories, targeted change monitors and durable selected-resource cleanup.</summary>
public sealed class UninstallResidueService(IUninstallAccountSessions sessions, IUninstallRegistryProfiles profiles,
    IUninstallPackageSource packages, IUninstallScanFolders folders, IUninstallProcessSource processes,
    ICleanerShortcutReader shortcuts, ICleanerBackupVault vault, IApplicationModeService mode,
    TimeProvider? clock = null) : IUninstallResidueService
{
    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    public Task<IUninstallResidueSession> CaptureAsync(IReadOnlyList<ItemDefinition> applications,
        IProgress<UninstallResidueProgress>? progress, CancellationToken ct) => Task.Run<IUninstallResidueSession>(() =>
    {
        var discovery = new UninstallDiscovery(sessions, profiles, packages, folders, shortcuts);
        var plans = discovery.Capture(applications, progress, ct);
        var session = new Session(sessions, packages, folders, processes, shortcuts, vault, discovery, plans, mode,
            _mutationGate, clock ?? TimeProvider.System);
        try { session.CaptureBaseline(progress, ct); return session; }
        catch { session.StopMonitors(); throw; }
    }, ct);

    public Task<IReadOnlyList<UninstallCleanupBackup>> GetBackupsAsync(CancellationToken ct) => Task.Run(() =>
        CreateBackupStore().List(ct), ct);

    public Task<UninstallRecoveryResult> RestoreAsync(Guid backupId, CancellationToken ct) => Task.Run(async () =>
    {
        using var operation = mode.BeginSystemOperation();
        await _mutationGate.WaitAsync(ct).ConfigureAwait(false);
        try { return await CreateBackupStore().RestoreAsync(backupId, ct).ConfigureAwait(false); }
        finally { _mutationGate.Release(); }
    }, ct);

    private UninstallBackupStore CreateBackupStore() => new(vault, new(sessions, folders, shortcuts));

    /// <summary>Retains the original evidence while the native uninstaller runs and a review dialog remains open.</summary>
    private sealed class Session : IUninstallResidueSession
    {
        private readonly UninstallDiscovery _discovery;
        private readonly IReadOnlyList<UninstallProductPlan> _plans;
        private readonly UninstallInspection _inspection;
        private readonly UninstallResourceEditor _editor;
        private readonly UninstallBackupStore _backups;
        private readonly IApplicationModeService _mode;
        private readonly SemaphoreSlim _mutationGate;
        private readonly TimeProvider _clock;
        private readonly List<FileSystemWatcher> _monitors = [];
        private readonly ConcurrentQueue<string> _monitorNotices = new();
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Dictionary<Guid, UninstallEvidence> _evidence = [];
        private IReadOnlyList<UninstallNativeOutcome> _outcomes = [];
        private int _observedChanges;
        private long _lastChangeAt;
        private bool _disposed;

        public Session(ICleanerRegistrySessions sessions, IUninstallPackageSource packages, IUninstallScanFolders folders,
            IUninstallProcessSource processes, ICleanerShortcutReader shortcuts, ICleanerBackupVault vault,
            UninstallDiscovery discovery, IReadOnlyList<UninstallProductPlan> plans, IApplicationModeService mode,
            SemaphoreSlim mutationGate, TimeProvider clock)
        {
            _discovery = discovery;
            _plans = plans;
            _inspection = new(sessions, packages, folders, processes, shortcuts, discovery.Accounts, discovery.Notices);
            _editor = new(sessions, folders, shortcuts);
            _backups = new(vault, _editor);
            _mode = mode; _mutationGate = mutationGate; _clock = clock;
            _lastChangeAt = clock.GetTimestamp();
        }

        public void CaptureBaseline(IProgress<UninstallResidueProgress>? progress, CancellationToken ct)
        {
            foreach (var root in _plans.SelectMany(plan => plan.FileRoots).DistinctBy(root => root.Path, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                var directory = root.SingleFile ? Path.GetDirectoryName(root.Path)! : root.Path;
                if (!Directory.Exists(directory)) continue;
                try
                {
                    var monitor = new FileSystemWatcher(directory)
                    {
                        IncludeSubdirectories = !root.SingleFile,
                        Filter = root.SingleFile ? Path.GetFileName(root.Path) : "*",
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                    };
                    monitor.Changed += OnFileChanged;
                    monitor.Created += OnFileChanged;
                    monitor.Deleted += OnFileChanged;
                    monitor.Renamed += OnFileChanged;
                    monitor.Error += (_, args) => _monitorNotices.Enqueue($"{directory}: change-monitor coverage interrupted ({args.GetException().Message}); final targeted scan is still required.");
                    _monitors.Add(monitor);
                    monitor.EnableRaisingEvents = true;
                }
                catch (Exception ex) when (UninstallResources.IsResourceError(ex) || ex is ArgumentException)
                { _discovery.Notices.Add($"{directory}: {ex.Message}"); }
            }
            foreach (var entry in _inspection.ScanRegistry(_plans, null, ct))
            {
                var plan = _plans.Single(plan => plan.Application.Id == entry.Finding.AppId);
                plan.RegistryBefore[UninstallResources.ResourceIdentity(entry.Finding)] = UninstallResources.Fingerprint(entry);
            }
            progress?.Report(new("Pre-uninstall account and registry inventory captured."));
        }

        private void OnFileChanged(object sender, FileSystemEventArgs args)
        {
            Interlocked.Increment(ref _observedChanges);
            Interlocked.Exchange(ref _lastChangeAt, _clock.GetTimestamp());
        }

        public Task<UninstallResidueScan> InspectAsync(IReadOnlyList<UninstallNativeOutcome> outcomes,
            IProgress<UninstallResidueProgress>? progress, CancellationToken ct) => ScanAsync(outcomes, true, progress, ct);

        public Task<UninstallResidueScan> RescanAsync(IProgress<UninstallResidueProgress>? progress, CancellationToken ct) =>
            ScanAsync(null, false, progress, ct);

        private Task<UninstallResidueScan> ScanAsync(IReadOnlyList<UninstallNativeOutcome>? outcomes, bool wait,
            IProgress<UninstallResidueProgress>? progress, CancellationToken ct) => Task.Run(async () =>
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (outcomes != null) _outcomes = outcomes.ToArray();
                if (wait) await WaitForCompletionAsync(progress, ct).ConfigureAwait(false);
                StopMonitors();
                _discovery.RefreshLocations(_plans, ct);
                var verification = _inspection.VerifyAll(_plans, _outcomes, ct);
                List<UninstallEvidence> findings = [];
                foreach (var plan in _plans) findings.AddRange(_inspection.ScanFiles(plan, verification, progress, ct));
                findings.AddRange(_inspection.ScanRegistry(_plans, verification, ct));
                _evidence.Clear();
                var sharedAccounts = findings.Where(entry => entry.Root != null).GroupBy(entry => entry.Finding.Path, StringComparer.OrdinalIgnoreCase)
                    .Where(group => group.Select(entry => entry.Root!.Sid).Distinct().Count() > 1)
                    .Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var group in findings.GroupBy(UninstallResources.PhysicalIdentity, StringComparer.OrdinalIgnoreCase))
                {
                    var first = group.First();
                    var protectedEntry = group.FirstOrDefault(entry => !entry.Finding.CanDelete);
                    var shared = first.Root != null && sharedAccounts.Contains(first.Finding.Path);
                    var entry = first with
                    {
                        OwnerAppIds = group.Select(entry => entry.Finding.AppId).Distinct(StringComparer.Ordinal).ToArray(),
                        Finding = first.Finding with
                        {
                            AppName = string.Join(", ", group.Select(entry => entry.Finding.AppName).Distinct()),
                            CanDelete = protectedEntry == null && !shared,
                            Reason = shared ? "This path is shared by different Windows accounts; it has been preserved."
                                : protectedEntry?.Finding.Reason ?? first.Finding.Reason
                        }
                    };
                    _evidence.Add(entry.Finding.Id, entry);
                }
                var after = findings.Where(entry => entry.Root == null).ToDictionary(entry => UninstallResources.ResourceIdentity(entry.Finding),
                    UninstallResources.Fingerprint, StringComparer.OrdinalIgnoreCase);
                int registryChanges = _plans.Sum(plan => plan.RegistryBefore.Count(entry => !after.TryGetValue(entry.Key, out var data) || data != entry.Value));
                var before = _plans.SelectMany(plan => plan.RegistryBefore.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);
                registryChanges += after.Keys.Count(key => !before.Contains(key));
                var notices = _discovery.Notices.Concat(_monitorNotices).Distinct().ToArray();
                return new UninstallResidueScan(_evidence.Values.Select(entry => entry.Finding).ToArray(),
                    verification.Values.Select(context => context.Verification).ToArray(), notices,
                    Volatile.Read(ref _observedChanges) + registryChanges);
            }
            finally { _gate.Release(); }
        }, ct);

        /// <summary>Allows launcher/child-process work to settle before the final resource scan; never treats a timeout as success.</summary>
        private async Task<IReadOnlyDictionary<string, UninstallVerificationContext>> WaitForCompletionAsync(
            IProgress<UninstallResidueProgress>? progress, CancellationToken ct)
        {
            var started = _clock.GetTimestamp();
            var quietSince = started;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new("Monitoring uninstaller completion and remaining application registrations..."));
                var verification = _inspection.VerifyAll(_plans, _outcomes, ct);
                var now = _clock.GetTimestamp();
                var busy = verification.Values.Any(context => context.NativeSucceeded && !context.Deferred &&
                    (context.ProcessRunning || context.Active.Count > 0 && !SomeInstallationRemoved(context)));
                if (_clock.GetElapsedTime(quietSince, now) >= TimeSpan.FromSeconds(2) &&
                    _clock.GetElapsedTime(Interlocked.Read(ref _lastChangeAt), now) >= TimeSpan.FromSeconds(2) && !busy) return verification;
                if (_clock.GetElapsedTime(started, now) >= TimeSpan.FromSeconds(30))
                {
                    if (busy) _discovery.Notices.Add("Uninstall monitoring reached its deadline. Remaining installations/processes are reported and their resources are protected.");
                    return verification;
                }
                await Task.Delay(TimeSpan.FromSeconds(1), _clock, ct).ConfigureAwait(false);
            }

            bool SomeInstallationRemoved(UninstallVerificationContext context)
            {
                var plan = _plans.Single(plan => plan.Application.Id == context.Verification.AppId);
                var original = plan.Registrations.Select(registration => registration.Sid ?? "machine")
                    .Concat(plan.Packages.Where(pair => pair.Value.Length > 0).Select(pair => pair.Key))
                    .Concat(plan.Executables.Values.Select(sid => sid ?? "machine")).ToHashSet(StringComparer.Ordinal);
                return original.Any(scope => !context.Active.Contains(scope));
            }
        }

        public Task<UninstallCleanupResult> CleanAsync(IReadOnlyCollection<Guid> selected,
            IProgress<UninstallResidueProgress>? progress, CancellationToken ct) => Task.Run(async () =>
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var ids = selected.Distinct().ToArray();
                if (ids.Length == 0) return new UninstallCleanupResult([], [], null);
                if (ids.Any(id => !_evidence.TryGetValue(id, out var entry) || !entry.Finding.CanDelete))
                    throw new IOException("The selection contains an unavailable or protected resource. Scan again.");
                var entries = ids.Select(id => _evidence[id]).ToArray();
                using var operation = _mode.BeginSystemOperation();
                await _mutationGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                var verification = _inspection.VerifyAll(_plans, _outcomes, ct);
                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    foreach (var owner in entry.OwnerAppIds ?? [entry.Finding.AppId])
                    {
                        var plan = _plans.Single(plan => plan.Application.Id == owner);
                        if (!verification[owner].CanDelete(entry.Root?.Sid ?? entry.Sid) ||
                            entry.Root != null && _inspection.IsSharedRoot(plan, entry.Root))
                            throw new IOException($"{entry.Finding.Path}: the application is still installed/running, or its account/ownership changed. Scan again.");
                    }
                }
                progress?.Report(new("Creating and verifying uninstall cleanup backup..."));
                var backup = await _backups.CreateAsync(entries, ct).ConfigureAwait(false);
                List<Guid> removed = [];
                List<string> errors = [];
                // Delete leaves first. A selected parent is removed only if it is empty afterwards.
                foreach (var entry in entries.OrderBy(entry => entry.Finding.Kind is UninstallArtifactKind.Directory or UninstallArtifactKind.RegistryKey ? 1 : 0)
                    .ThenByDescending(entry => entry.Finding.Path.Count(character => character == '\\')))
                {
                    if (ct.IsCancellationRequested)
                    {
                        errors.Add("Cleanup cancelled. Removed resources remain recoverable from the saved backup.");
                        break;
                    }
                    progress?.Report(new(entry.Finding.Path, removed.Count + errors.Count, entries.Length));
                    try
                    {
                        await _editor.DeleteAsync(entry, backup.Hashes.GetValueOrDefault(entry.Finding.Id), ct).ConfigureAwait(false);
                        removed.Add(entry.Finding.Id);
                        _evidence.Remove(entry.Finding.Id);
                    }
                    catch (OperationCanceledException)
                    {
                        errors.Add("Cleanup cancelled. Removed resources remain recoverable from the saved backup.");
                        break;
                    }
                    catch (Exception ex) when (UninstallResources.IsResourceError(ex)) { errors.Add($"{entry.Finding.Path}: {ex.Message}"); }
                }
                return new UninstallCleanupResult(removed, errors, backup.Info.Id);
                }
                finally { _mutationGate.Release(); }
            }
            finally { _gate.Release(); }
        }, ct);

        public void StopMonitors()
        {
            foreach (var monitor in _monitors) monitor.Dispose();
            _monitors.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try { StopMonitors(); _evidence.Clear(); _disposed = true; }
            finally { _gate.Release(); }
        }
    }
}
