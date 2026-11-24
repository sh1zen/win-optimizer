using WinOptimizer.Core.Features.Cleaner.Interfaces;
using WinOptimizer.Core.Features.Cleaner.Models;

namespace WinOptimizer.Core.Features.Cleaner.Services;

/// <summary>Serializes scans and cleanup, retaining the authoritative immutable scan evidence.</summary>
public sealed class CleanerService(ICleanerBackend backend, ICleanerBackupService backups) : ICleanerService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<Guid, CleanerFinding> _findings = new();
    private CleanerOptions? _options;

    public async Task<CleanerScanResult> ScanAsync(CleanerOptions options, IReadOnlyCollection<CleanerArea> areas,
        IProgress<CleanerProgress>? progress, CancellationToken ct)
    {
        options.Validate();
        if (areas.Any(x => !Enum.IsDefined(x))) throw new ArgumentException("Unknown Cleaner category.", nameof(areas));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _findings.Clear();
            _options = null;
            // Keep a scan-owned snapshot even if a caller later edits its arrays.
            options = options with { RegistryAreas = [.. options.RegistryAreas], ApplicationCaches = [.. options.ApplicationCaches],
                RegistryAccountSids = options.RegistryAccountSids is null ? null : [.. options.RegistryAccountSids],
                PrivacyAccountSids = options.PrivacyAccountSids is null ? null : [.. options.PrivacyAccountSids],
                JunkAccountSids = options.JunkAccountSids is null ? null : [.. options.JunkAccountSids],
                ShortcutAccountSids = options.ShortcutAccountSids is null ? null : [.. options.ShortcutAccountSids] };
            List<CleanerFinding> findings = [];
            HashSet<string> registryValues = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> filePaths = new(StringComparer.OrdinalIgnoreCase);
            List<string> notices = [];
            int checkedItems = 0;
            var selectedAreas = Array.AsReadOnly(areas.Distinct().OrderBy(Stage).ToArray());
            for (int index = 0; index < selectedAreas.Count; index++)
            {
                var area = selectedAreas[index];
                ct.ThrowIfCancellationRequested();
                var stageProgress = new StageProgress(progress, new(selectedAreas, index, area));
                stageProgress.Report(new($"Cleaner_{area}", 0, 0));
                var result = await backend.ScanAsync(area, options, stageProgress, ct).ConfigureAwait(false);
                foreach (var finding in result.Findings)
                {
                    // Privacy and stale-reference scans can identify the same history value.
                    if (finding.Target is CleanerRegistryTarget value &&
                        !registryValues.Add($"{value.View32}\0{value.KeyPath}\0{value.ValueName}")) continue;
                    // File categories can share recent links or application-cache directories.
                    if (finding.Target is CleanerFileTarget file &&
                        !filePaths.Add(file.Path)) continue;
                    findings.Add(finding);
                }
                notices.AddRange(result.Notices);
                checkedItems += result.CheckedItems;
                stageProgress.Complete(new($"Cleaner_{area}", 1, 1));
            }
            // A cancelled or failed scan never publishes a partially applicable plan.
            ct.ThrowIfCancellationRequested();
            _findings = findings.ToDictionary(finding => finding.Id);
            _options = options;
            return new(findings.AsReadOnly(), notices.AsReadOnly()) { CheckedItems = checkedItems };
        }
        finally { _gate.Release(); }
    }

    public async Task<CleanerApplyResult> ApplyAsync(IReadOnlyCollection<Guid> findingIds,
        IProgress<CleanerProgress>? progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var ids = findingIds.Distinct().ToArray();
            if (_options is null || ids.Any(id => !_findings.TryGetValue(id, out var item) || !item.CanClean))
                throw new InvalidOperationException("The selection is no longer valid. Run a new scan.");
            var recoverable = ids.Select(id => _findings[id]).Where(x => x.Target is CleanerFileTarget or CleanerRegistryTarget).ToArray();
            if (_options.BackupBeforeCleaning && recoverable.Length > 0)
            {
                progress?.Report(new("Cleaner_CreatingBackup", 0, 0));
                // All selected recoverable resources must be backed up before the first modification.
                await backups.CreateAsync(recoverable, ct).ConfigureAwait(false);
            }
            int completed = 0;
            List<string> errors = [];
            List<CleanerItemResult> outcomes = [];
            var cancelled = false;
            foreach (var stage in ids.Select(id => _findings[id]).GroupBy(item => Stage(item.Area)).OrderBy(group => group.Key))
            {
                if (stage.First().Area == CleanerArea.RegistryDefrag)
                {
                    // One maintenance boot covers the entire registry selection.
                    var hives = stage.ToArray();
                    await ApplyBatchAsync(hives, () => backend.ScheduleRegistryDefragAsync(
                        hives.Select(item => (CleanerHiveTarget)item.Target).ToArray(), ct)).ConfigureAwait(false);
                }
                else
                {
                    foreach (var item in stage)
                    {
                        await ApplyBatchAsync([item], () => backend.CleanAsync(item, _options, ct)).ConfigureAwait(false);
                        if (cancelled) break;
                    }
                }
                if (cancelled) break;
            }
            return new(completed, errors.AsReadOnly()) { Items = outcomes.AsReadOnly(), IsCancelled = cancelled };

            async Task ApplyBatchAsync(CleanerFinding[] batch, Func<Task> apply)
            {
                if (ct.IsCancellationRequested) { cancelled = true; return; }
                var label = batch[0].Detail;
                progress?.Report(new(label, outcomes.Count, ids.Length));
                // Consume before execution, including cancellation and partial external failures.
                foreach (var item in batch) _findings.Remove(item.Id);
                try
                {
                    await apply().ConfigureAwait(false);
                    foreach (var item in batch)
                    {
                        var outcome = new CleanerItemResult(item, null);
                        outcomes.Add(outcome);
                        if (!outcome.Scheduled) completed++;
                    }
                }
                catch (OperationCanceledException) { cancelled = true; return; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    errors.Add($"{label}: {ex.Message}");
                    foreach (var item in batch) outcomes.Add(new(item, $"{item.Detail}: {ex.Message}"));
                }
                progress?.Report(new(label, outcomes.Count, ids.Length));
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Attaches the owning stage to backend measurements without asynchronous reposting.</summary>
    private sealed class StageProgress(IProgress<CleanerProgress>? target, CleanerScanStage stage) : IProgress<CleanerProgress>
    {
        public void Report(CleanerProgress value) => Publish(value, stage);

        public void Complete(CleanerProgress value) =>
            Publish(value, stage with { CompletedAreas = stage.CompletedAreas + 1, ActiveArea = null });

        private void Publish(CleanerProgress value, CleanerScanStage current) => target?.Report(value with
        {
            Stage = stage.CompletedAreas * value.Stages + value.Stage,
            Stages = stage.Areas.Count * value.Stages,
            ScanStage = current
        });
    }

    private static int Stage(CleanerArea area) => area switch
    {
        CleanerArea.Privacy => 0, CleanerArea.JunkFiles => 1, CleanerArea.Shortcuts => 2,
        CleanerArea.Registry => 3, CleanerArea.RegistryDefrag => 4, CleanerArea.Disk => 5,
        _ => throw new ArgumentOutOfRangeException(nameof(area))
    };
}
