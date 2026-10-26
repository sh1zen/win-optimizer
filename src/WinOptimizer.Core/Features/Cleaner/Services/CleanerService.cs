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
            var selectedAreas = areas.Distinct().OrderBy(Stage).ToArray();
            for (int index = 0; index < selectedAreas.Length; index++)
            {
                var area = selectedAreas[index];
                ct.ThrowIfCancellationRequested();
                var stageProgress = new StageProgress(progress, index + 1, selectedAreas.Length);
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
                stageProgress.Report(new($"Cleaner_{area}", 1, 1));
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
            foreach (var id in ids.OrderBy(id => Stage(_findings[id].Area)))
            {
                ct.ThrowIfCancellationRequested();
                var item = _findings[id];
                progress?.Report(new(item.Detail, completed + errors.Count, ids.Length));
                // Consume before execution, including cancellation and partial external failures.
                _findings.Remove(id);
                try
                {
                    await backend.CleanAsync(item, _options, ct).ConfigureAwait(false);
                    completed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    errors.Add($"{item.Detail}: {ex.Message}");
                }
                progress?.Report(new(item.Detail, completed + errors.Count, ids.Length));
            }
            return new(completed, errors.AsReadOnly());
        }
        finally { _gate.Release(); }
    }

    /// <summary>Attaches the owning stage to backend measurements without asynchronous reposting.</summary>
    private sealed class StageProgress(IProgress<CleanerProgress>? target, int stage, int stages) : IProgress<CleanerProgress>
    {
        public void Report(CleanerProgress value) => target?.Report(value with
            { Stage = (stage - 1) * value.Stages + value.Stage, Stages = stages * value.Stages });
    }

    private static int Stage(CleanerArea area) => area switch
    {
        CleanerArea.Privacy => 0, CleanerArea.JunkFiles => 1, CleanerArea.Shortcuts => 2,
        CleanerArea.Registry => 3, CleanerArea.RegistryDefrag => 4, CleanerArea.Disk => 5,
        _ => throw new ArgumentOutOfRangeException(nameof(area))
    };
}
