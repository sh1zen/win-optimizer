using Moq;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Management;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.UI.Features.Management.ViewModels;
using WinOptimizer.UI.Tests.Helpers;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

/// <summary>Uses fake event storage and dialogs; no test calls Windows event APIs.</summary>
public sealed class EventsCleanupViewModelTests : IDisposable
{
    private readonly Mock<IEventLogCleanupService> _service = new();
    private readonly Mock<IDialogService> _dialogs = new();
    private readonly Mock<ILocalizationService> _localization = new();
    private readonly Mock<IApplicationModeService> _mode = new();
    private readonly Mock<ITaskProgressService> _tasks = new();
    private readonly List<CancellationTokenSource> _sources = [];
    private CancellationTokenSource? _current;
    private static readonly EventLogChannel[] Rows = [new("Application", 10, 4096, ""), new("System", 20, 8192, ""), new("Protected", null, null, "Access denied")];

    public EventsCleanupViewModelTests()
    {
        _service.Setup(service => service.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Rows);
        _localization.Setup(service => service.GetString(It.IsAny<string>())).Returns((string key) => key switch
        {
            "EventsCleanup_Summary" => "cleared={0}, selected={1}",
            "EventsCleanup_Cancelled" => "cancelled: cleared={0}, selected={1}",
            "EventsCleanup_Selection" => "selected={0}, total={1}",
            "EventsCleanup_SelectionSize" => "size={0}",
            "EventsCleanup_SelectionSizePartial" => "known={0}, unknown={1}",
            _ => key
        });
        _tasks.SetupGet(service => service.CurrentTaskCancellationSource).Returns(() => _current);
        _tasks.Setup(service => service.StartTask(It.IsAny<string>(), true, true, true)).Returns(() =>
        { _current = new(); _sources.Add(_current); return _current; });
        _tasks.Setup(service => service.CompleteTask()).Callback(() => _current = null);
        _dialogs.Setup(dialog => dialog.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse { Confirmed = true });
    }

    private EventsCleanupViewModel Create() => new(_service.Object, _dialogs.Object, _localization.Object,
        _mode.Object, new ExtensionTestDispatcher(), _tasks.Object);

    [Fact]
    public async Task SelectedSizeTracksIndividualBulkAndReloadedSelection()
    {
        using var vm = Create(); await vm.LoadAsync();
        Assert.Equal("size=0 B", vm.SelectionSizeLabel);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        vm.Channels[0].IsSelected = true;
        Assert.Equal($"size={4:N2} KiB", vm.SelectionSizeLabel);
        Assert.Contains(nameof(vm.SelectionSizeLabel), changed);
        vm.SelectAllCommand.Execute(null);
        Assert.Equal($"size={12:N2} KiB", vm.SelectionSizeLabel);
        vm.Channels[0].IsSelected = false;
        Assert.Equal($"size={8:N2} KiB", vm.SelectionSizeLabel);
        vm.SelectNoneCommand.Execute(null);
        Assert.Equal("size=0 B", vm.SelectionSizeLabel);
        vm.SelectAllCommand.Execute(null);
        var previous = vm.Channels[0];
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("size=0 B", vm.SelectionSizeLabel);
        changed.Clear(); previous.IsSelected = false;
        Assert.DoesNotContain(nameof(vm.SelectionSizeLabel), changed);
    }

    [Theory]
    [InlineData(512, 512, "B")]
    [InlineData(1024, 1, "KiB")]
    [InlineData(1L << 20, 1, "MiB")]
    [InlineData(3L << 30, 3, "GiB")]
    [InlineData(1L << 40, 1, "TiB")]
    public async Task SelectedSizeUsesReadableUnitsAndSupportsLargeLogs(long bytes, int amount, string unit)
    {
        _service.Setup(service => service.ReadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EventLogChannel[] { new("Application", 1, bytes, "") });
        using var vm = Create(); await vm.LoadAsync(); vm.SelectAllCommand.Execute(null);
        Assert.Equal("size=" + amount.ToString(unit == "B" ? "N0" : "N2") + " " + unit, vm.SelectionSizeLabel);
    }

    [Fact]
    public async Task SelectedSizeDistinguishesMissingMeasurementsFromEmptyLogs()
    {
        _service.Setup(service => service.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new EventLogChannel[]
        {
            new("Known", 1, 4096, ""), new("Unknown", null, null, ""), new("Empty", 0, 0, ""),
            new("Unavailable", null, null, "Access denied")
        });
        using var vm = Create(); await vm.LoadAsync(); vm.SelectAllCommand.Execute(null);
        Assert.Equal($"known={4:N2} KiB, unknown=1", vm.SelectionSizeLabel);
        vm.Channels[0].IsSelected = false;
        Assert.Equal("known=0 B, unknown=1", vm.SelectionSizeLabel);
        vm.Channels[1].IsSelected = false;
        Assert.Equal("size=0 B", vm.SelectionSizeLabel);
    }

    [Fact]
    public async Task SelectedSizeUpdatesWhenChannelMetadataChanges()
    {
        using var vm = Create(); await vm.LoadAsync(); vm.Channels[0].IsSelected = true;
        var changed = new List<string?>();
        vm.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        vm.Channels[0].Apply(new("Application", false, "", "Read failed", new("Application", null, null, "")), "Failed");
        Assert.Equal("known=0 B, unknown=1", vm.SelectionSizeLabel);
        Assert.Contains(nameof(vm.SelectionSizeLabel), changed);
    }

    [Fact]
    public async Task LoadSelectsNothingAndSelectAllSkipsUnavailableLogs()
    {
        using var vm = Create(); await vm.LoadAsync(); await vm.LoadAsync();
        Assert.Equal("Nav_Cleaner", vm.SectionTitle);
        Assert.False(vm.CanClear); Assert.All(vm.Channels, row => Assert.False(row.IsSelected));
        vm.SelectAllCommand.Execute(null);
        Assert.Equal(new[] { "Application", "System" }, vm.Channels.Where(row => row.IsSelected).Select(row => row.Name));
        Assert.Equal("Access denied", vm.Channels[2].ResultText);
        vm.SelectNoneCommand.Execute(null); Assert.False(vm.CanClear);
        _service.Verify(service => service.ReadAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DismissingConfirmationDoesNotClearAnything()
    {
        _dialogs.Setup(dialog => dialog.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse());
        using var vm = Create(); await vm.LoadAsync(); vm.Channels[0].IsSelected = true;
        await vm.ClearCommand.ExecuteAsync(null);
        _service.Verify(service => service.ClearAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<IProgress<EventLogCleanupProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.False(vm.IsBusy); Assert.True(vm.Channels[0].IsSelected); Assert.True(vm.WaitForWriteAsync().IsCompleted);
        _tasks.Verify(service => service.CompleteTask(), Times.Once);
    }

    [Fact]
    public async Task ConfirmedSelectionKeepsPerLogErrorsAndActualPostClearCounts()
    {
        var result = new EventLogCleanupResult([
            new("Application", true, "archive/application.evtx", "", new("Application", 2, 1024, "")),
            new("System", false, "", "Disk full")], false);
        _service.Setup(service => service.ClearAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<IProgress<EventLogCleanupProgress>>(), It.IsAny<CancellationToken>())).ReturnsAsync(result);
        using var vm = Create(); await vm.LoadAsync(); vm.SelectAllCommand.Execute(null);
        await vm.ClearCommand.ExecuteAsync(null);
        _dialogs.Verify(dialog => dialog.ShowConfirmationAsync(It.Is<ConfirmationRequest>(request =>
            request.Items != null && request.Items.SequenceEqual(new[] { "Application", "System" }))), Times.Once);
        _service.Verify(service => service.ClearAsync(It.Is<IReadOnlyList<string>>(names => names.SequenceEqual(new[] { "Application", "System" })),
            It.IsAny<IProgress<EventLogCleanupProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("2", vm.Channels[0].CountText); Assert.Contains("archive/application.evtx", vm.Channels[0].ResultText);
        Assert.Equal($"size={8:N2} KiB", vm.SelectionSizeLabel);
        Assert.False(vm.Channels[0].IsSelected); Assert.True(vm.Channels[1].IsSelected);
        Assert.Contains("Disk full", vm.Channels[1].ResultText);
        Assert.False(vm.IsError); Assert.Equal(InfoBarSeverity.Success, vm.StatusSeverity);
        Assert.Equal("cleared=1, selected=2", vm.Status);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CleanupSummaryReflectsCompleteFailureAndArchiveProblems(bool cleared, bool archiveError)
    {
        var error = archiveError ? "EVTX archive could not be verified" : cleared ? "" : "Access denied";
        _service.Setup(service => service.ClearAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<IProgress<EventLogCleanupProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EventLogCleanupResult([new("Application", cleared, cleared ? "archive/application.evtx" : "", error)], false));
        using var vm = Create(); await vm.LoadAsync(); vm.Channels[0].IsSelected = true;
        await vm.ClearCommand.ExecuteAsync(null);
        Assert.Equal(!cleared || archiveError, vm.IsError);
        Assert.Equal(!cleared || archiveError ? InfoBarSeverity.Error : InfoBarSeverity.Success, vm.StatusSeverity);
        Assert.Equal($"cleared={(cleared ? 1 : 0)}, selected=1", vm.Status);
        Assert.Equal(!cleared, vm.Channels[0].IsSelected);
        if (error.Length != 0) Assert.Contains(error, vm.Channels[0].ResultText);
    }

    [Theory]
    [InlineData(WinOptimizerMode.Builder)]
    [InlineData(WinOptimizerMode.ConfigReview)]
    public async Task TestAndReviewModesPermitDiscoveryButBlockConfirmationAndCleanup(WinOptimizerMode mode)
    {
        _mode.SetupGet(service => service.CurrentMode).Returns(mode);
        using var vm = Create(); await vm.LoadAsync(); vm.Channels[0].IsSelected = true;
        Assert.False(vm.CanClear); Assert.False(vm.ClearCommand.CanExecute(null));
        await vm.ClearCommand.ExecuteAsync(null);
        _dialogs.Verify(dialog => dialog.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()), Times.Never);
        _service.Verify(service => service.ReadAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InFlightCleanupBlocksReloadAndShutdownWaitsForItsReceipt()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<EventLogCleanupResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.Setup(service => service.ClearAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<IProgress<EventLogCleanupProgress>>(), It.IsAny<CancellationToken>()))
            .Returns(() => { started.TrySetResult(); return completion.Task; });
        using var vm = Create(); await vm.LoadAsync(); vm.Channels[0].IsSelected = true;
        var clearing = vm.ClearCommand.ExecuteAsync(null);
        await started.Task;
        Assert.True(vm.IsBusy); Assert.False(vm.RefreshCommand.CanExecute(null));
        Assert.False(vm.WaitForWriteAsync().IsCompleted);
        completion.SetResult(new([new("Application", true, "archive/application.evtx", "")], false));
        await clearing; await vm.WaitForWriteAsync();
        Assert.False(vm.IsBusy); Assert.Contains("archive/application.evtx", vm.Channels[0].ResultText);
    }

    [Fact]
    public async Task CancelledCleanupKeepsCompletedArchivesAndUnprocessedSelection()
    {
        _service.Setup(service => service.ClearAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<IProgress<EventLogCleanupProgress>>(), It.IsAny<CancellationToken>()))
            .Returns(() => { _current!.Cancel(); return Task.FromResult(new EventLogCleanupResult([new("Application", true, "archive/application.evtx", "")], true)); });
        using var vm = Create(); await vm.LoadAsync(); vm.SelectAllCommand.Execute(null);
        await vm.ClearCommand.ExecuteAsync(null);
        Assert.Equal("cancelled: cleared=1, selected=2", vm.Status);
        Assert.False(vm.IsError);
        Assert.False(vm.Channels[0].IsSelected); Assert.True(vm.Channels[1].IsSelected);
        Assert.Equal($"size={8:N2} KiB", vm.SelectionSizeLabel);
        Assert.Contains("archive/application.evtx", vm.Channels[0].ResultText);
    }

    [Fact]
    public async Task CancellationBeforeResultsKeepsTheSelectedCountAndNormalStatus()
    {
        _service.Setup(service => service.ClearAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<IProgress<EventLogCleanupProgress>>(), It.IsAny<CancellationToken>()))
            .Returns(() => { _current!.Cancel(); return Task.FromCanceled<EventLogCleanupResult>(_current.Token); });
        using var vm = Create(); await vm.LoadAsync(); vm.SelectAllCommand.Execute(null);
        await vm.ClearCommand.ExecuteAsync(null);
        Assert.Equal("cancelled: cleared=0, selected=2", vm.Status);
        Assert.False(vm.IsError);
        Assert.True(vm.Channels[0].IsSelected); Assert.True(vm.Channels[1].IsSelected);
    }

    [Fact]
    public async Task DiscoveryFailureIsVisibleAndCanBeRetried()
    {
        _service.SetupSequence(service => service.ReadAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UtilityOperationException("Event service unavailable")).ReturnsAsync(Rows);
        using var vm = Create(); await vm.LoadAsync();
        Assert.True(vm.IsError); Assert.Equal("Event service unavailable", vm.Status);
        await vm.LoadAsync(); Assert.Equal(3, vm.Channels.Count); Assert.False(vm.IsError);
    }

    [Fact]
    public async Task ActiveGlobalTaskPreventsConfirmationAndASecondTask()
    {
        _tasks.SetupGet(service => service.IsTaskRunning).Returns(true);
        using var vm = Create(); await vm.LoadAsync(); vm.Channels[0].IsSelected = true;
        await vm.ClearCommand.ExecuteAsync(null);
        Assert.True(vm.IsError); Assert.Equal("Utilities_Wait", vm.Status);
        _dialogs.Verify(dialog => dialog.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()), Times.Never);
    }

    public void Dispose() { foreach (var source in _sources) source.Dispose(); }
}
