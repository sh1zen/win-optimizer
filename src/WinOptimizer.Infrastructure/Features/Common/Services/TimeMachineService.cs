using System.Security;
using System.Text.Json;
using WinOptimizer.Core.Features.Cleaner.Models;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Localization;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Infrastructure.Features.Cleaner;
using WinOptimizer.Infrastructure.Features.Drivers;

namespace WinOptimizer.Infrastructure.Features.Common.Services;

/// <summary>Coordinates reviewed setting comparisons and targeted recovery through the Windows feature owners.</summary>
public sealed class TimeMachineService(TimeMachineStore store, IChangeHistoryService history, ICompatibleSettingsRegistry catalogue,
    WindowsRegistryService registry, UserPreferencesService preferences, IScheduledTaskService tasks, TimeMachineScheduledTasks taskWrites,
    CleanerRegistryScanner cleaner, WindowsDriverRemovalBackend drivers, IApplicationModeService mode, ILocalizationService localization,
    ILogService log) : ITimeMachineRetentionService, IDisposable
{
    private readonly object _maintenanceGate = new();
    private readonly SemaphoreSlim _retentionGate = new(1, 1);
    private CancellationTokenSource? _maintenance;

    public Task<int> RetentionDaysAsync() => preferences.ReadTimeMachineRetentionAsync();

    /// <summary>Prunes on startup and during long-running sessions after storage is ready.</summary>
    public void StartRetentionMaintenance()
    {
        lock (_maintenanceGate)
        {
            if (_maintenance is not null) return;
            _maintenance = new();
            _ = RunMaintenanceAsync(_maintenance.Token);
        }
    }

    private async Task RunMaintenanceAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1));
        try
        {
            do
            {
                ct.ThrowIfCancellationRequested();
                try { await PruneAsync().ConfigureAwait(false); }
                catch (Exception ex) when (IsResourceFailure(ex))
                { log.LogWarning($"TimeMachine retention: {ex.Message}"); }
            } while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        lock (_maintenanceGate)
        {
            if (_maintenance is { IsCancellationRequested: false } maintenance)
            {
                maintenance.Cancel();
                maintenance.Dispose();
            }
        }
    }

    public async Task UpdateRetentionAsync(int days)
    {
        if (TimeMachineRetention.Normalize(days) != days) throw new ArgumentOutOfRangeException(nameof(days));
        await _retentionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var result = await preferences.SetPreferenceAsync(TimeMachineRetention.PreferenceKey, days).ConfigureAwait(false);
            if (!result.Success) throw new IOException(result.ErrorMessage);
        }
        finally { _retentionGate.Release(); }

        // Maintenance reads the committed policy when it acquires the gate, even if a newer save came first.
        await PruneAsync().ConfigureAwait(false);
    }

    public Task PruneAsync() => MaintainStoreAsync(listSnapshots: false);

    public Task<IReadOnlyList<TimeMachineSnapshot>> ListSnapshotsAsync() => MaintainStoreAsync(listSnapshots: true);

    /// <summary>Removes the specified saved settings snapshot from the interactive account.</summary>
    public Task DeleteSnapshotAsync(Guid id, CancellationToken ct) => Task.Run(() => store.DeleteSnapshot(id), ct);

    /// <summary>Applies a single committed policy while preference writes are excluded.</summary>
    private Task<IReadOnlyList<TimeMachineSnapshot>> MaintainStoreAsync(bool listSnapshots) => Task.Run(async () =>
    {
        await _retentionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var days = await RetentionDaysAsync().ConfigureAwait(false);
            PruneStore(days);
            return listSnapshots ? store.ListSnapshots(days) : [];
        }
        finally { _retentionGate.Release(); }
    });

    private void PruneStore(int days)
    {
        try { store.Prune(days); }
        catch (Exception ex) when (IsResourceFailure(ex)) { log.LogWarning($"TimeMachine retention: {ex.Message}"); }
    }

    public async Task<(TimeMachineSnapshot Snapshot, int Unavailable)> CaptureAsync(CancellationToken ct)
    {
        var (states, unavailable) = await ReadCurrentAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var snapshot = await Task.Run(() => store.SaveSnapshot(states.Values.ToArray()), ct).ConfigureAwait(false);
        store.Record(LogTopic.Settings, "TimeMachine", "Settings snapshot saved");
        return (snapshot, unavailable);
    }

    /// <summary>Reads the selected account-scoped snapshot under the current retention policy.</summary>
    public async Task<TimeMachineSnapshot> ReadSnapshotAsync(Guid id, CancellationToken ct)
    {
        var days = await RetentionDaysAsync().ConfigureAwait(false);
        return await Task.Run(() => store.ReadSnapshot(id, days), ct).ConfigureAwait(false);
    }

    public async Task<TimeMachineComparison> CompareAsync(Guid id, CancellationToken ct)
    {
        var snapshot = await ReadSnapshotAsync(id, ct).ConfigureAwait(false);
        var (states, _) = await ReadCurrentAsync(ct).ConfigureAwait(false);
        List<TimeMachineDifference> differences = [];
        var unavailable = 0;
        foreach (var before in snapshot.States)
        {
            if (!states.TryGetValue(before.Target, out var current)) unavailable++;
            else if (!before.Value.SameAs(current.Value)) differences.Add(new(current with { Value = before.Value }, current.Value));
        }
        return new(snapshot, differences, unavailable);
    }

    public async Task<TimeMachineHistoryPage> ReadHistoryAsync(long? offset, CancellationToken ct)
    {
        var files = await history.GetFilesAsync(ct).ConfigureAwait(false);
        if (files.Count == 0) return new([], null, 0);
        var account = store.AccountSid;
        var page = await history.ReadRecentAsync(files[0].FileName, offset, cancellationToken: ct).ConfigureAwait(false);
        return new(page.Entries.Where(entry => IsHistoryVisible(entry, account)).ToArray(),
            page.HasMore ? page.NextOffset : null, page.UnreadableEntries);
    }

    public static DateOnly GetHistoryDay(ChangeHistoryEntry entry) =>
        DateOnly.FromDateTime(entry.Timestamp!.Value.ToLocalTime().DateTime);

    /// <summary>Reads the rest of one day in journal order, even if the clock moved between recorded changes.</summary>
    public async Task<(IReadOnlyList<ChangeHistoryEntry> Entries, int Unreadable)> ReadHistoryDayRemainderAsync(
        DateOnly date, long beforeOffset, CancellationToken ct)
    {
        var files = await history.GetFilesAsync(ct).ConfigureAwait(false);
        if (files.Count == 0) return ([], 0);

        var account = store.AccountSid;
        List<ChangeHistoryEntry> entries = [];
        var unreadable = 0;
        long? offset = beforeOffset;
        while (offset is { } current)
        {
            var page = await history.ReadRecentAsync(files[0].FileName, current, cancellationToken: ct).ConfigureAwait(false);
            foreach (var entry in page.Entries)
                if (IsHistoryVisible(entry, account) && GetHistoryDay(entry) == date) entries.Add(entry);
            unreadable += page.UnreadableEntries;
            offset = page.HasMore ? page.NextOffset : null;
        }
        return (entries, unreadable);
    }

    private static bool IsHistoryVisible(ChangeHistoryEntry entry, string account) =>
        entry.AccountSid is null || entry.AccountSid == account;

    /// <summary>Loads recovery details only for the open day and checks that its expected state still exists.</summary>
    public async Task<IReadOnlyList<TimeMachineHistoryItem>> ReviewHistoryAsync(IReadOnlyList<ChangeHistoryEntry> receipts, CancellationToken ct)
    {
        var days = await RetentionDaysAsync().ConfigureAwait(false);
        var account = store.AccountSid;
        var results = new List<TimeMachineHistoryItem>(receipts.Count);
        IReadOnlyDictionary<TimeMachineTarget, TimeMachineState>? currentPreferences = null;
        foreach (var receipt in receipts)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsHistoryVisible(receipt, account)) continue;
            if (receipt.RecoveryId is not Guid id)
            {
                results.Add(new(receipt, null, false));
                continue;
            }
            TimeMachineUndo? undo = null;
            try
            {
                undo = await Task.Run(() => store.ReadUndo(id, days), ct).ConfigureAwait(false);
                if (undo?.Before.Target.Area == TimeMachineArea.Preference && currentPreferences is null)
                    currentPreferences = (await preferences.ReadTimeMachinePreferencesAsync().ConfigureAwait(false)).ToDictionary(x => x.Target);
                var available = undo is not null && await CanRestoreAsync(undo, currentPreferences, ct).ConfigureAwait(false);
                results.Add(new(receipt, undo, available));
            }
            catch (Exception ex) when (IsResourceFailure(ex)) { results.Add(new(receipt, undo, false, ex.Message)); }
        }
        return results;
    }

    public async Task<TimeMachineRestoreBatch> RestoreSnapshotAsync(Guid id,
        IReadOnlyList<TimeMachineDifference> selected, CancellationToken ct)
    {
        using var operation = BeginRestore();
        var snapshot = await ReadSnapshotAsync(id, ct).ConfigureAwait(false);
        var saved = snapshot.States.ToDictionary(x => x.Target);
        var targets = CatalogueTargets().Select(x => x.Target).ToHashSet();
        return await RestoreBatchAsync(selected.DistinctBy(x => x.Baseline.Target), async difference =>
        {
            var target = difference.Baseline.Target;
            if (!saved.TryGetValue(target, out var before) ||
                (target.Area == TimeMachineArea.Preference ? !UserPreferencesService.TimeMachineKeys.Contains(target.Key) : !targets.Contains(target)))
                return new(difference.Baseline.Label, "The selected setting is no longer tracked.");
            return await RestoreOneAsync(before, difference.Current, null, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task<TimeMachineRestoreBatch> RestoreHistoryAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        using var operation = BeginRestore();
        var days = await RetentionDaysAsync().ConfigureAwait(false);
        return await RestoreBatchAsync(ids.Distinct(), async id =>
        {
            TimeMachineUndo? undo;
            try { undo = await Task.Run(() => store.ReadUndo(id, days), ct).ConfigureAwait(false); }
            catch (Exception ex) when (IsResourceFailure(ex))
            {
                return new(id.ToString(), ex.Message);
            }
            return undo is null
                ? new(id.ToString(), "The recovery copy is unavailable or has expired.")
                : await RestoreOneAsync(undo.Before, undo.After, undo, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Retains the outcome of every completed write when cancellation stops the batch.</summary>
    private static async Task<TimeMachineRestoreBatch> RestoreBatchAsync<T>(IEnumerable<T> selected,
        Func<T, Task<TimeMachineRestoreResult>> restore, CancellationToken ct)
    {
        List<TimeMachineRestoreResult> results = [];
        try
        {
            foreach (var item in selected)
            {
                ct.ThrowIfCancellationRequested();
                results.Add(await restore(item).ConfigureAwait(false));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new(results, true); }
        return new(results, false);
    }

    private async Task<TimeMachineRestoreResult> RestoreOneAsync(TimeMachineState before, TimeMachineValue expected,
        TimeMachineUndo? undo, CancellationToken ct)
    {
        try
        {
            var target = before.Target;
            switch (target.Area)
            {
                case TimeMachineArea.Registry:
                    await Task.Run(() => registry.WriteTimeMachineValue(before, before.Value, expected), ct).ConfigureAwait(false);
                    break;
                case TimeMachineArea.Preference:
                    await preferences.RestoreTimeMachinePreferenceAsync(target.Key, before.Value, expected).ConfigureAwait(false);
                    break;
                case TimeMachineArea.ScheduledTask:
                    var result = await taskWrites.SetAsync(target.Key, before.Label, ReadBoolean(before.Value), ReadBoolean(expected)).ConfigureAwait(false);
                    if (!result.Success) throw new IOException(result.ErrorMessage);
                    break;
                case TimeMachineArea.CleanedRegistry:
                    await Task.Run(() => cleaner.Restore(ReadCleanerTarget(before.Value)), ct).ConfigureAwait(false);
                    store.Record(LogTopic.Cleaner, before.Label, "Registry value restored");
                    break;
                case TimeMachineArea.Driver when undo is not null:
                    await drivers.RestoreDriverAsync(undo, ct).ConfigureAwait(false);
                    break;
                default:
                    throw new IOException("This resource does not support selective restore.");
            }
            return new(before.Label, null);
        }
        catch (Exception ex) when (IsResourceFailure(ex)) { return new(before.Label, ex.Message); }
    }

    private async Task<bool> CanRestoreAsync(TimeMachineUndo undo,
        IReadOnlyDictionary<TimeMachineTarget, TimeMachineState>? currentPreferences, CancellationToken ct)
    {
        var target = undo.Before.Target;
        switch (target.Area)
        {
            case TimeMachineArea.Registry:
                return (await Task.Run(() => registry.ReadTimeMachineValue(target), ct).ConfigureAwait(false)).SameAs(undo.After);
            case TimeMachineArea.Preference:
                return currentPreferences?.GetValueOrDefault(target)?.Value.SameAs(undo.After) == true;
            case TimeMachineArea.ScheduledTask:
                return await tasks.IsTaskEnabledAsync(target.Key).ConfigureAwait(false) == ReadBoolean(undo.After);
            case TimeMachineArea.CleanedRegistry:
                return await Task.Run(() => cleaner.IsMissing(ReadCleanerTarget(undo.Before.Value)), ct).ConfigureAwait(false);
            case TimeMachineArea.Driver:
                return await Task.Run(() => drivers.CanRestoreDriver(undo), ct).ConfigureAwait(false);
            default:
                return false;
        }
    }

    private async Task<(Dictionary<TimeMachineTarget, TimeMachineState> States, int Unavailable)> ReadCurrentAsync(CancellationToken ct)
    {
        var targets = CatalogueTargets().ToArray();
        var current = (await preferences.ReadTimeMachinePreferencesAsync().ConfigureAwait(false)).ToDictionary(x => x.Target);
        var registryStates = await Task.Run(() =>
        {
            List<TimeMachineState> states = [];
            foreach (var target in targets.Where(x => x.Target.Area == TimeMachineArea.Registry))
            {
                ct.ThrowIfCancellationRequested();
                try { states.Add(target with { Value = registry.ReadTimeMachineValue(target.Target) }); }
                catch (Exception ex) when (IsResourceFailure(ex))
                { log.LogWarning($"TimeMachine could not read '{target.Label}': {ex.Message}"); }
            }
            return states;
        }, ct).ConfigureAwait(false);
        foreach (var state in registryStates) current.Add(state.Target, state);

        var taskTargets = targets.Where(x => x.Target.Area == TimeMachineArea.ScheduledTask).ToArray();
        if (taskTargets.Length > 0)
        {
            var readings = await tasks.GetTaskStatesAsync(taskTargets.Select(x => x.Target.Key).ToArray()).ConfigureAwait(false);
            for (var i = 0; i < taskTargets.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (readings[i].Error is { } error) log.LogWarning($"TimeMachine could not read '{taskTargets[i].Label}': {error.Message}");
                if (readings[i].Enabled is { } enabled)
                    current.Add(taskTargets[i].Target, taskTargets[i] with { Value = TimeMachineValue.From(enabled) });
            }
        }
        return (current, targets.Length + UserPreferencesService.TimeMachineKeys.Count - current.Count);
    }

    private IEnumerable<TimeMachineState> CatalogueTargets() =>
        catalogue.GetAllFilteredSettings().Values.SelectMany(x => x)
            .Where(x => x.InputType != InputType.Action && !x.RequiresAdvancedUnlock)
            .SelectMany(setting =>
            {
                var name = localization.GetString(SettingLocalizationKeys.Name(setting));
                var label = name.StartsWith('[') ? setting.Name : name;
                return WindowsRegistryService.GetTimeMachineTargets(setting, label)
                    .Concat(setting.ScheduledTaskSettings.Select(x => new TimeMachineState(
                        new(TimeMachineArea.ScheduledTask, x.TaskPath), label, TimeMachineValue.Missing)));
            })
            .DistinctBy(x => (x.Target.Area, x.Target.Key.ToUpperInvariant(), x.Target.Name?.ToUpperInvariant()));

    private IDisposable BeginRestore()
    {
        var operation = mode.BeginSystemOperation();
        if (mode.CurrentMode == WinOptimizerMode.Normal) return operation;
        operation.Dispose();
        throw new InvalidOperationException(localization.GetString("TrialMode_Blocked"));
    }

    private static bool ReadBoolean(TimeMachineValue value) =>
        value.Json is { } json ? JsonSerializer.Deserialize<bool>(json) : throw new IOException("Missing scheduled-task state.");

    private static CleanerRegistryTarget ReadCleanerTarget(TimeMachineValue value) =>
        JsonSerializer.Deserialize<CleanerRegistryTarget>(value.Json ?? "") ?? throw new IOException("Missing registry cleanup evidence.");

    private static bool IsResourceFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or SecurityException or JsonException
            or InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException;
}
