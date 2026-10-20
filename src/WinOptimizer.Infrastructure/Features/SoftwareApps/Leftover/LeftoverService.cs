using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.Infrastructure.Features.Cleaner;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Uninstall;

namespace WinOptimizer.Infrastructure.Features.SoftwareApps.Leftover;

/// <summary>Owns scan authority, normal-mode cleanup and reuse of uninstall backup/recovery for standalone leftovers.</summary>
internal sealed class LeftoverService(ILeftoverSource source, IUninstallAccountSessions sessions, IUninstallScanFolders folders,
    ICleanerShortcutReader shortcuts, ICleanerBackupVault vault, IApplicationModeService mode) : ILeftoverService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, LeftoverCandidate> _issued = [];
    private long _revision;
    private long _issuedRevision;

    public Task<LeftoverScan> ScanAsync(CancellationToken ct = default)
    {
        var revision = Interlocked.Increment(ref _revision);
        return Task.Run(async () =>
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _issued.Clear();
                var inventory = source.Read(ct);
                foreach (var candidate in inventory.Candidates) _issued.Add(candidate.Item.Id, candidate);
                _issuedRevision = revision;
                return new LeftoverScan(inventory.Candidates.Select(candidate => candidate.Item).ToArray(), inventory.Notices);
            }
            finally { _gate.Release(); }
        }, ct);
    }

    public Task<LeftoverCleanupResult> RemoveAsync(IReadOnlyCollection<Guid> selected, CancellationToken ct = default) => Task.Run(async () =>
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (mode.CurrentMode != WinOptimizerMode.Normal) throw new InvalidOperationException("Leftover cleanup requires normal mode.");
            var ids = selected.Distinct().ToArray();
            if (ids.Length == 0) return new LeftoverCleanupResult(0, [], null);
            if (_issuedRevision != Interlocked.Read(ref _revision) || ids.Any(id => !_issued.TryGetValue(id, out var candidate) || !candidate.Item.CanRemove))
                throw new IOException("The selection contains protected or expired items. Scan again.");
            using var operation = mode.BeginSystemOperation();
            var chosen = ids.Select(id => _issued[id]).ToArray();
            VerifyCurrent(chosen, source.Read(ct));
            var editor = new UninstallResourceEditor(sessions, folders, shortcuts);
            var backups = new UninstallBackupStore(vault, editor);
            var entries = chosen.SelectMany(candidate => candidate.Evidence).ToArray();
            var backup = await backups.CreateAsync(entries, ct).ConfigureAwait(false);
            // Backup can take time; recheck MSI ownership and registrations once more before the first mutation.
            VerifyCurrent(chosen, source.Read(ct));
            List<string> errors = [];
            int removed = 0;
            foreach (var candidate in chosen)
            {
                try
                {
                    foreach (var entry in candidate.Evidence.OrderBy(entry => entry.Finding.Kind switch
                        {
                            UninstallArtifactKind.File => 0, UninstallArtifactKind.Directory => 1,
                            UninstallArtifactKind.RegistryValue => 2, _ => 3
                        })
                        .ThenByDescending(entry => entry.Finding.Path.Count(character => character == '\\')))
                        await editor.DeleteAsync(entry, backup.Hashes.GetValueOrDefault(entry.Finding.Id), ct).ConfigureAwait(false);
                    removed++;
                }
                catch (OperationCanceledException) { errors.Add("Cleanup cancelled. Removed resources can be restored from the saved backup."); break; }
                catch (Exception ex) when (UninstallResources.IsResourceError(ex)) { errors.Add($"{candidate.Item.Location}: {ex.Message}"); }
            }
            return new LeftoverCleanupResult(removed, errors, backup.Info.Id);
        }
        finally { _issued.Clear(); _gate.Release(); }
    }, ct);

    /// <summary>Rejects changed registrations, new installer references, incomplete inventory and changed file evidence.</summary>
    private static void VerifyCurrent(IReadOnlyList<LeftoverCandidate> selected, LeftoverInventory current)
    {
        var candidates = current.Candidates.ToDictionary(candidate => candidate.Identity, StringComparer.OrdinalIgnoreCase);
        if (selected.Any(before => !candidates.TryGetValue(before.Identity, out var now) || !now.Item.CanRemove || now.Fingerprint != before.Fingerprint))
            throw new IOException("An item changed or is now required by an installed application. Scan again; nothing was deleted.");
    }
}
