using WinOptimizer.Core.Features.Common.Enums;
using System.Text.Json.Serialization;

namespace WinOptimizer.Core.Features.Common.Models;

/// <summary>A diagnostic event with exception data kept separately from its message.</summary>
public sealed record AppLogEntry
{
    public required DateTimeOffset? Timestamp { get; init; }
    public required LogLevel? Level { get; init; }
    public required string Message { get; init; }
    public LogExceptionInfo? Exception { get; init; }
    [JsonRequired]
    public LogTopic Topic { get; init; }
}

public sealed record LogExceptionInfo(string Type, string Message, string? StackTrace, string? InnerException);

public enum HistoryEntryKind { SettingChange, SettingAction, AppInstalled, AppRemoved }

/// <summary>A change receipt retaining the original setting values and enclosing batch.</summary>
public sealed record ChangeHistoryEntry
{
    public required DateTimeOffset? Timestamp { get; init; }
    public required HistoryEntryKind Kind { get; init; }
    public required string Name { get; init; }
    public string? Group { get; init; }
    public string? Before { get; init; }
    public string? After { get; init; }
    public string? Batch { get; init; }
    public Guid? BatchId { get; init; }
    public string? ActionLabel { get; init; }
}

/// <summary>A service-owned saved file identifier; callers never supply arbitrary filesystem paths.</summary>
public sealed record LogFileInfo(string FileName, DateTimeOffset LastWriteTime);

/// <summary>A bounded journal page and the exact byte offset of the next page.</summary>
public sealed record LogPage<T>(IReadOnlyList<T> Entries, long NextOffset, bool HasMore, int UnreadableEntries = 0);
