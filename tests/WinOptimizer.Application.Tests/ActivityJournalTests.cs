using Moq;
using WinOptimizer.Application.Journal;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using Xunit;

namespace WinOptimizer.Application.Tests;

/// <summary>Exercises the production merger using deterministic paged storage boundaries.</summary>
public sealed class ActivityJournalTests
{
    private readonly Mock<ILogService> _logs = new();
    private readonly Mock<IChangeHistoryService> _history = new();
    private readonly DateTimeOffset _time = DateTimeOffset.UnixEpoch;
    private AppLogEntry Event(int minute, string name, LogTopic topic = LogTopic.General, LogLevel level = LogLevel.Info) =>
        new() { Timestamp = _time.AddMinutes(minute), Message = name, Level = level, Topic = topic };
    private ChangeHistoryEntry Change(int minute) => new() { Timestamp = _time.AddMinutes(minute), Kind = HistoryEntryKind.AppRemoved, Name = "Removed app" };
    private ActivityJournalService Create() => new(_logs.Object, _history.Object);
    public ActivityJournalTests()
    {
        _logs.Setup(s => s.GetFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<LogFileInfo>());
        _history.Setup(s => s.GetFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<LogFileInfo>());
    }

    private void Diagnostics(params (string File, AppLogEntry[] Entries)[] sources)
    {
        _logs.Setup(s => s.GetFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(sources.Select(source => new LogFileInfo(source.File, _time)).ToArray());
        foreach (var source in sources)
            _logs.Setup(s => s.ReadRecentAsync(source.File, It.IsAny<long?>(), 32, It.IsAny<CancellationToken>()))
                .Returns((string _, long? cursor, int size, CancellationToken token) => Task.FromResult(Page(source.Entries, cursor, size)));
    }
    private static LogPage<T> Page<T>(T[] entries, long? cursor, int size)
    {
        var remaining = (int)(cursor ?? entries.Length);
        var result = entries.Skip(entries.Length - remaining).Take(size).ToArray();
        var next = remaining - result.Length;
        return new(result, next, next > 0);
    }

    [Fact]
    public async Task ConstructionDoesNoIo_AndMergingIncludesAllSourcesNewestFirst()
    {
        var service = Create(); _logs.VerifyNoOtherCalls(); _history.VerifyNoOtherCalls();
        Diagnostics(("first.jsonl", [Event(9, "new"), Event(2, "old")]), ("second.log", [Event(6, "middle")]));
        _history.Setup(s => s.GetFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { new LogFileInfo("ChangeHistory.jsonl", _time) });
        _history.Setup(s => s.ReadRecentAsync("ChangeHistory.jsonl", null, 32, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LogPage<ChangeHistoryEntry>([Change(8)], 0, false));
        var session = await service.OpenAsync(new());
        var page = await session.ReadNextAsync();
        Assert.Equal(new[] { "new", "Removed app", "middle", "old" }, page.Entries.Select(entry => entry.Diagnostic?.Message ?? entry.Change?.Name));
        Assert.Equal(LogTopic.Software, page.Entries[1].Topic);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task PaginationKeepsCursorsAcrossFilesWithoutRepeatingRows()
    {
        Diagnostics(("a.jsonl", Enumerable.Range(0, 130).Select(i => Event(260 - i * 2, "a" + i, LogTopic.Software)).ToArray()),
            ("b.jsonl", Enumerable.Range(0, 130).Select(i => Event(259 - i * 2, "b" + i, LogTopic.Updates)).ToArray()));
        var session = await Create().OpenAsync(new());
        var first = await session.ReadNextAsync(); var second = await session.ReadNextAsync();
        Assert.Equal(200, first.Entries.Count); Assert.True(first.HasMore);
        Assert.Equal(60, second.Entries.Count); Assert.False(second.HasMore);
        Assert.Equal(260, first.Entries.Concat(second.Entries).Select(entry => entry.Diagnostic!.Message).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 260).Reverse().Select(i => (DateTimeOffset?)_time.AddMinutes(i)), first.Entries.Concat(second.Entries).Select(entry => entry.Timestamp));
    }

    [Fact]
    public async Task TopicProblemAndExceptionSearchUseStoredTypedFields()
    {
        Diagnostics(("events.jsonl", [Event(3, "Operation failed", LogTopic.Software, LogLevel.Error) with
            { Exception = new("IOException", "Disk full", "frame", null) }, Event(2, "[DismSession] inventory"), Event(1, "Unclassified text")]));
        var page = await (await Create().OpenAsync(new(LogTopic.Software, JournalFilter.Problems, "disk full"))).ReadNextAsync();
        Assert.Single(page.Entries); Assert.NotNull(page.Entries[0].Diagnostic!.Exception);
        Assert.Equal(LogTopic.General, JournalRecord.From("events.jsonl", Event(1, "unclassified")).Topic);
        Assert.Equal(LogTopic.Updates, JournalRecord.From("new.jsonl", Event(1, "[DismSession] literal text", LogTopic.Updates)).Topic);
        _history.Verify(s => s.GetFilesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SourceErrorsAndUnreadableRowsRemainVisibleAlongsideSuccessfulRecords()
    {
        Diagnostics(("broken.jsonl", []), ("good.jsonl", []));
        _logs.Setup(s => s.ReadRecentAsync("broken.jsonl", null, 32, It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("unavailable"));
        _logs.Setup(s => s.ReadRecentAsync("good.jsonl", null, 32, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LogPage<AppLogEntry>([Event(1, "retained")], 0, false, 2));
        var page = await (await Create().OpenAsync(new())).ReadNextAsync();
        Assert.Single(page.Entries); Assert.Equal(2, page.UnreadableEntries);
        Assert.Contains("broken.jsonl: unavailable", page.Notices);
    }

    [Fact]
    public async Task SparseQueriesBoundWorkAndCanContinueToOlderMatches()
    {
        Diagnostics(("events.jsonl", Enumerable.Range(0, 2200).Select(i => Event(2200 - i, i == 2199 ? "match" : "other")).ToArray()));
        var session = await Create().OpenAsync(new(Search: "match"));
        var first = await session.ReadNextAsync();
        Assert.Empty(first.Entries); Assert.True(first.HasMore);
        var second = await session.ReadNextAsync(); Assert.Single(second.Entries); Assert.False(second.HasMore);
    }
}
