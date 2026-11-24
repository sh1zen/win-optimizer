using System.Text.Json;

namespace WinOptimizer.Core.Features.Common.Models;

public enum TimeMachineArea { Registry, Preference, ScheduledTask, CleanedRegistry, Driver }

/// <summary>Identifies a tracked resource in its owning subsystem.</summary>
public sealed record TimeMachineTarget(TimeMachineArea Area, string Key, string? Name = null);

/// <summary>Preserves absence separately from a value, including its native registry type.</summary>
public sealed record TimeMachineValue(string? Json = null, int? RegistryKind = null)
{
    public static TimeMachineValue Missing { get; } = new();

    public static TimeMachineValue From(object? value, int? registryKind = null) =>
        new(JsonSerializer.Serialize(value), registryKind);

    public bool SameAs(TimeMachineValue other) => RegistryKind == other.RegistryKind &&
        (Json == other.Json || Json is not null && other.Json is not null &&
            JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(Json), JsonSerializer.Deserialize<JsonElement>(other.Json)));
}

public sealed record TimeMachineState(TimeMachineTarget Target, string Label, TimeMachineValue Value);
public sealed record TimeMachineDifference(TimeMachineState Baseline, TimeMachineValue Current);
public sealed record TimeMachineSnapshot(Guid Id, DateTimeOffset CreatedUtc, IReadOnlyList<TimeMachineState> States);
public sealed record TimeMachineComparison(TimeMachineSnapshot Snapshot, IReadOnlyList<TimeMachineDifference> Differences,
    int Unavailable);

/// <summary>A verified change keeps the exact previous value and the expected current value.</summary>
public sealed record TimeMachineUndo(Guid Id, DateTimeOffset CreatedUtc, TimeMachineState Before, TimeMachineValue After);
public sealed record TimeMachineRestoreResult(string Label, string? Error);
public sealed record TimeMachineRestoreBatch(IReadOnlyList<TimeMachineRestoreResult> Results, bool Cancelled);
public sealed record TimeMachineHistoryItem(ChangeHistoryEntry Receipt, TimeMachineUndo? Undo, bool CanRestore, string? Notice = null);
public sealed record TimeMachineHistoryPage(IReadOnlyList<ChangeHistoryEntry> Entries, long? NextOffset, int UnreadableEntries);

public static class TimeMachineRetention
{
    public const string PreferenceKey = "TimeMachineRetentionDays";
    public const int DefaultDays = 28;
    public const int MinDays = 1;
    public const int MaxDays = 365;

    public static int Normalize(int days) => days is >= MinDays and <= MaxDays ? days : DefaultDays;

    public static bool IsExpired(DateTimeOffset createdUtc, DateTimeOffset now, int days) =>
        createdUtc <= now.AddDays(-days);
}
