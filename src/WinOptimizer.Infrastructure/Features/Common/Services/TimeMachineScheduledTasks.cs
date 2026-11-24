using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Infrastructure.Features.Common.Services;

/// <summary>Serializes verified task toggles and records previous scheduler state.</summary>
public sealed class TimeMachineScheduledTasks(IScheduledTaskService tasks, TimeMachineStore store)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<OperationResult> SetAsync(string path, string label, bool enabled, bool? expected = null)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var before = await tasks.IsTaskEnabledAsync(path).ConfigureAwait(false);
            if (expected is not null && before != expected)
                return OperationResult.Failed("The scheduled task changed after review. Compare again.");
            var after = TimeMachineValue.From(enabled);
            var undo = before is not null && before != enabled
                ? store.PrepareUndo(new(new(TimeMachineArea.ScheduledTask, path), label, TimeMachineValue.From(before.Value)), after)
                : null;
            var result = await (enabled ? tasks.EnableTaskAsync(path) : tasks.DisableTaskAsync(path)).ConfigureAwait(false);
            if (undo is null) return result;
            var observed = await tasks.IsTaskEnabledAsync(path).ConfigureAwait(false);
            if (observed == enabled)
                store.Record(LogTopic.Settings, label, "Scheduled task changed", undo);
            else if (observed == before) store.DiscardUndo(undo.Id);
            else store.Record(LogTopic.Settings, label, "Scheduled task change requires review", undo);
            return result.Success && observed != enabled
                ? OperationResult.Failed("Windows did not confirm the scheduled task change.") : result;
        }
        finally { _gate.Release(); }
    }
}
