using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Management;

namespace WinOptimizer.Infrastructure.Features.Management;

/// <summary>Serializes cleanup, validates the entire request and preserves partial outcomes on cancellation.</summary>
internal sealed class EventLogCleanupService(IEventLogStore store, IApplicationModeService mode) : IEventLogCleanupService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<IReadOnlyList<EventLogChannel>> ReadAsync(CancellationToken cancellationToken) => Task.Run<IReadOnlyList<EventLogChannel>>(() =>
    {
        var channels = new List<EventLogChannel>();
        foreach (var name in store.GetLogNames().Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            channels.Add(store.Read(name));
        }
        return channels;
    }, cancellationToken);

    public async Task<EventLogCleanupResult> ClearAsync(IReadOnlyList<string> logNames,
        IProgress<EventLogCleanupProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logNames);
        var names = logNames.ToArray();
        if (names.Length == 0 || names.Any(string.IsNullOrWhiteSpace) || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
            throw new ArgumentException("Select one or more distinct event logs.", nameof(logNames));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var reservation = mode.BeginSystemOperation();
            if (mode.CurrentMode != WinOptimizerMode.Normal)
                throw new InvalidOperationException("Event log cleanup requires normal application mode.");
            return await Task.Run(() => Clear(names, progress, cancellationToken), CancellationToken.None).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Cancellation stops between native calls; already completed log operations remain reported.</summary>
    private EventLogCleanupResult Clear(string[] names, IProgress<EventLogCleanupProgress>? progress, CancellationToken token)
    {
        store.EnsureCanClear();
        var known = store.GetLogNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Any(name => !known.Contains(name)))
            throw new ArgumentException("An event log is no longer registered. Reload the list before clearing.");
        var outcomes = new List<EventLogCleanupOutcome>();
        if (token.IsCancellationRequested) return new(outcomes, true);
        var backupDirectory = store.CreateBackupDirectory();
        foreach (var name in names)
        {
            if (token.IsCancellationRequested) return new(outcomes, true);
            outcomes.Add(store.ClearWithBackup(name, backupDirectory));
            progress?.Report(new(outcomes.Count, names.Length, name));
        }
        return new(outcomes, false);
    }
}

/// <summary>Isolates Windows event APIs, elevation checks and archive storage from orchestration tests.</summary>
internal interface IEventLogStore
{
    IReadOnlyList<string> GetLogNames();
    EventLogChannel Read(string name);
    void EnsureCanClear();
    string CreateBackupDirectory();
    EventLogCleanupOutcome ClearWithBackup(string name, string backupDirectory);
}
