namespace WinOptimizer.Core.Features.Management;

/// <summary>A dated inventory row; unavailable metadata retains its diagnostic.</summary>
public sealed record EventLogChannel(string Name, long? RecordCount, long? FileSize, string Diagnostic);
public sealed record EventLogCleanupProgress(int Completed, int Total, string LogName);
public sealed record EventLogCleanupOutcome(string LogName, bool Cleared, string BackupPath, string Error, EventLogChannel? Current = null);
public sealed record EventLogCleanupResult(IReadOnlyList<EventLogCleanupOutcome> Outcomes, bool Cancelled);

/// <summary>Inventories local channels and clears only explicitly requested names with EVTX archives.</summary>
public interface IEventLogCleanupService
{
    Task<IReadOnlyList<EventLogChannel>> ReadAsync(CancellationToken cancellationToken);
    Task<EventLogCleanupResult> ClearAsync(IReadOnlyList<string> logNames,
        IProgress<EventLogCleanupProgress>? progress, CancellationToken cancellationToken);
}
