using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.Settings.ViewModels;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

/// <summary>Exercises topic navigation, merged pagination, cancellation and diagnostic clearing through production commands.</summary>
public sealed class LogsAndHistoryViewModelTests
{
    private readonly Mock<ILogService> _logs = new();
    private readonly Mock<IActivityJournalService> _journal = new();
    private readonly Mock<IActivityJournalSession> _session = new();
    private readonly Mock<ILocalizationService> _localization = new();
    private readonly Mock<IDialogService> _dialogs = new();
    private static JournalRecord Receipt(string name) => JournalRecord.From("ChangeHistory.jsonl", new ChangeHistoryEntry
    {
        Timestamp = DateTimeOffset.UnixEpoch, Kind = HistoryEntryKind.SettingChange, Name = name,
        Group = "Privacy", Before = "before", After = "after", Batch = "Config import"
    });
    private static JournalRecord Event(string name) => JournalRecord.From("session.jsonl", new AppLogEntry
    { Timestamp = DateTimeOffset.UnixEpoch, Level = LogLevel.Error, Topic = LogTopic.Software, Message = name });
    private static JournalPage Page(params JournalRecord[] rows) => new(rows, false, 0, []);

    public LogsAndHistoryViewModelTests()
    {
        _localization.Setup(service => service.GetString(It.IsAny<string>())).Returns((string key) => key);
        _localization.Setup(service => service.GetString(It.IsAny<string>(), It.IsAny<object[]>()))
            .Returns((string key, object[] args) => key + ": " + string.Join(", ", args));
        _journal.Setup(service => service.OpenAsync(It.IsAny<JournalQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync(_session.Object);
        _session.Setup(session => session.ReadNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Page(Receipt("Original setting"), Event("Application event")));
    }
    private LogsAndHistoryViewModel CreateViewModel() => new(_journal.Object, _logs.Object, _localization.Object, _dialogs.Object);

    [Fact]
    public void UnsetOrInvalidNativeSelectionsDoNotCorruptTheQuery()
    {
        using var vm = CreateViewModel();
        vm.TopicIndex = -1; vm.TopicIndex = vm.Topics.Count;
        vm.FilterIndex = -1; vm.FilterIndex = vm.Filters.Count;
        Assert.Equal(0, vm.TopicIndex); Assert.Equal(0, vm.FilterIndex);
        _journal.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExplicitRefreshGroupsOriginalRecordsBySubjectAcrossSources()
    {
        using var vm = CreateViewModel();
        _journal.VerifyNoOtherCalls(); _logs.VerifyNoOtherCalls();
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Groups.Count);
        var group = vm.Groups.Single(group => group.Topic == LogTopic.Settings);
        var row = Assert.Single(group.Items);
        Assert.Same(vm.Entries[0], row);
        Assert.Equal("Original setting", row.Message);
        Assert.Equal("before", row.Before); Assert.Equal("after", row.After); Assert.Equal("Privacy", row.Group);
        Assert.Equal("Config import", row.Batch); Assert.False(group.IsExpanded);
        Assert.False(vm.Groups.Single(group => group.Topic == LogTopic.Software).IsExpanded);
        Assert.True(vm.Groups.Single(group => group.Topic == LogTopic.Software).HasProblems);
        Assert.True(vm.CanClear); Assert.False(vm.IsEmpty);
        _logs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LoadMoreUsesTheSameSessionAndPreservesExpansionAndRows()
    {
        _session.SetupSequence(session => session.ReadNextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JournalPage([Receipt("First")], true, 0, []))
            .ReturnsAsync(Page(Receipt("Second")));
        using var vm = CreateViewModel();
        await vm.RefreshCommand.ExecuteAsync(null); vm.Groups[0].IsExpanded = true;
        var row = vm.Entries[0];
        await vm.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "First", "Second" }, vm.Entries.Select(entry => entry.Message));
        Assert.Same(row, vm.Groups[0].Items[0]); Assert.True(vm.Groups[0].IsExpanded);
        Assert.False(vm.HasMore);
        _journal.Verify(service => service.OpenAsync(It.IsAny<JournalQuery>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClearRequiresConfirmationAndRetainsChangeReceipts(bool confirmed)
    {
        _dialogs.Setup(dialog => dialog.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()))
            .ReturnsAsync(new ConfirmationResponse { Confirmed = confirmed });
        _logs.Setup(service => service.ClearAsync(It.IsAny<CancellationToken>()))
            .Callback(() => _session.Setup(session => session.ReadNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Page(Receipt("Original setting"))))
            .Returns(Task.CompletedTask);
        using var vm = CreateViewModel();
        await vm.RefreshCommand.ExecuteAsync(null); await vm.ClearAppLogsCommand.ExecuteAsync(null);
        _logs.Verify(service => service.ClearAsync(It.IsAny<CancellationToken>()), confirmed ? Times.Once() : Times.Never());
        Assert.Contains(vm.Entries, entry => entry.Record.Change is not null);
        if (confirmed) { Assert.Single(vm.Entries); Assert.Equal("Logs_Cleared", vm.Notice); }
        else Assert.Equal(2, vm.Entries.Count);
    }

    [Fact]
    public async Task FailedClearPreservesDisplayedActivityAndReportsFailure()
    {
        _dialogs.Setup(dialog => dialog.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse { Confirmed = true });
        _logs.Setup(service => service.ClearAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("log in use"));
        using var vm = CreateViewModel(); await vm.RefreshCommand.ExecuteAsync(null);
        await vm.ClearAppLogsCommand.ExecuteAsync(null);
        Assert.Equal("log in use", vm.Error); Assert.Equal(2, vm.Entries.Count);
        Assert.NotEqual("Logs_Cleared", vm.Notice);
    }

    [Fact]
    public async Task ChangingFilterCancelsPendingQueryAndRejectsItsLatePage()
    {
        var oldSession = new Mock<IActivityJournalSession>();
        var pending = new TaskCompletionSource<JournalPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        oldSession.Setup(session => session.ReadNextAsync(It.IsAny<CancellationToken>())).Returns(pending.Task);
        _journal.Setup(service => service.OpenAsync(It.Is<JournalQuery>(query => query.Filter == JournalFilter.All), It.IsAny<CancellationToken>()))
            .ReturnsAsync(oldSession.Object);
        using var vm = CreateViewModel();
        var first = vm.RefreshCommand.ExecuteAsync(null);
        vm.FilterIndex = (int)JournalFilter.Diagnostics; await vm.PendingRead;
        pending.SetResult(Page(Receipt("Late history"))); await first;
        Assert.DoesNotContain(vm.Entries, entry => entry.Message == "Late history");
        Assert.Empty(vm.Error);
        _journal.Verify(service => service.OpenAsync(It.Is<JournalQuery>(query => query.Filter == JournalFilter.Diagnostics), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SearchDebouncesAndCombinesWithTheSelectedTopic()
    {
        using var vm = CreateViewModel();
        vm.TopicIndex = 1 + (int)LogTopic.Software; await vm.PendingRead;
        vm.SearchText = "obsolete"; var old = vm.PendingRead;
        vm.SearchText = "final search"; await vm.PendingRead; await old;
        _journal.Verify(service => service.OpenAsync(It.Is<JournalQuery>(query => query.Topic == LogTopic.Software && query.Search == "final search"), It.IsAny<CancellationToken>()), Times.Once);
        _journal.Verify(service => service.OpenAsync(It.Is<JournalQuery>(query => query.Search == "obsolete"), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ErrorsUnreadableSourcesAndSparsePagesAreDistinctFromAnEmptyJournal()
    {
        _session.Setup(session => session.ReadNextAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("unavailable"));
        using var vm = CreateViewModel(); await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("unavailable", vm.Error); Assert.False(vm.IsEmpty);
        _session.Setup(session => session.ReadNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new JournalPage([], false, 2, ["source failed"]));
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Empty(vm.Error); Assert.Contains("Logs_Unreadable: 2", vm.Notice);
        Assert.Contains("source failed", vm.Notice); Assert.False(vm.IsEmpty);
        _session.Setup(session => session.ReadNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new JournalPage([], false, 0, ["source failed"]));
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Contains("source failed", vm.Notice); Assert.False(vm.IsEmpty);
        _session.Setup(session => session.ReadNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new JournalPage([], true, 0, []));
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.IsEmpty); Assert.True(vm.CanLoadMore); Assert.Equal("Logs_NoMatchesYet", vm.EmptyLabel);
    }

    [Fact]
    public async Task ClearingDiagnosticsRetainsIncompleteHistoryNotices()
    {
        _dialogs.Setup(dialog => dialog.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse { Confirmed = true });
        _logs.Setup(service => service.ClearAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _session.Setup(session => session.ReadNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new JournalPage([], false, 0, ["history unavailable"]));
        using var vm = CreateViewModel();
        await vm.RefreshCommand.ExecuteAsync(null); await vm.ClearAppLogsCommand.ExecuteAsync(null);
        Assert.Contains("Logs_Cleared", vm.Notice); Assert.Contains("history unavailable", vm.Notice);
        Assert.False(vm.IsEmpty);
    }

    [Fact]
    public async Task DisposalRejectsLatePublication()
    {
        var pending = new TaskCompletionSource<JournalPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _session.Setup(session => session.ReadNextAsync(It.IsAny<CancellationToken>())).Returns(pending.Task);
        var vm = CreateViewModel(); var read = vm.RefreshCommand.ExecuteAsync(null);
        vm.Dispose(); pending.SetResult(Page(Receipt("Late"))); await read;
        Assert.Empty(vm.Entries);
    }

    [Fact]
    public async Task ExceptionDetailsAndLocalizationRemainAvailableWithoutFilenameNavigation()
    {
        var row = new LogDisplayEntry(JournalRecord.From("technical.jsonl", new AppLogEntry
        { Timestamp = DateTimeOffset.UnixEpoch, Level = LogLevel.Error, Topic = LogTopic.Software, Message = "Failed",
            Exception = new("IOException", "Disk full", "stack frame", "inner exception") }), _localization.Object);
        Assert.Contains("IOException: Disk full", row.Details); Assert.Contains("stack frame", row.Details);
        Assert.Contains("technical.jsonl", row.Details); Assert.Contains("inner exception", row.Details);
        using var vm = CreateViewModel(); await vm.RefreshCommand.ExecuteAsync(null);
        var topic = vm.Topics[1 + (int)LogTopic.Software];
        _localization.Setup(service => service.GetString("Logs_Topic_Software")).Returns("Applicazioni");
        vm.RefreshLabels();
        Assert.Same(topic, vm.Topics[1 + (int)LogTopic.Software]); Assert.Equal("Applicazioni", topic.Title);
        Assert.Equal("Applicazioni", vm.Groups.Single(group => group.Topic == LogTopic.Software).Title);
    }
}
