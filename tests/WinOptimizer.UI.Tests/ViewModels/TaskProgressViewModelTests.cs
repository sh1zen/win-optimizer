using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.ViewModels;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

public class TaskProgressViewModelTests : IDisposable
{
    private readonly Mock<ITaskProgressService> _mockTaskProgressService = new();
    private readonly Mock<IDispatcherService> _mockDispatcherService = new();
    private readonly Mock<IDialogService> _mockDialogService = new();
    private readonly Mock<ILocalizationService> _mockLocalizationService = new();
    private readonly Mock<ILogService> _mockLogService = new();

    private readonly TaskProgressViewModel _sut;

    public TaskProgressViewModelTests()
    {
        // Set up dispatcher to execute actions synchronously
        _mockDispatcherService
            .Setup(d => d.RunOnUIThread(It.IsAny<Action>()))
            .Callback<Action>(a => a());
        _mockDispatcherService
            .Setup(d => d.RunOnUIThreadAsync(It.IsAny<Func<Task>>()))
            .Returns<Func<Task>>(f => f());

        // Default localization returns null so fallbacks are used
        _mockLocalizationService
            .Setup(l => l.GetString(It.IsAny<string>()))
            .Returns((string key) => null!);

        _sut = new TaskProgressViewModel(
            _mockTaskProgressService.Object,
            _mockDispatcherService.Object,
            _mockDialogService.Object,
            _mockLocalizationService.Object,
            _mockLogService.Object);
    }

    public void Dispose()
    {
        _sut.Dispose();
    }

    [Fact]
    public void NonCancellableOperation_ShowsFooterProgressWithoutAcceptingCancellation()
    {
        _mockTaskProgressService.Setup(service => service.IsTaskRunning).Returns(true);
        _mockTaskProgressService.Raise(service => service.ProgressUpdated += null, this,
            new TaskProgressDetail { StatusText = "Generate XML", IsIndeterminate = true, CanCancel = false });
        _sut.IsLoading.Should().BeTrue();
        _sut.CanCancel.Should().BeFalse();
        _sut.CancelCommand.Execute(null);
        _mockTaskProgressService.Verify(service => service.CancelCurrentTask(), Times.Never);
        _mockTaskProgressService.Raise(service => service.ProgressUpdated += null, this,
            new TaskProgressDetail { StatusText = "Scan", Progress = 25 });
        _sut.CanCancel.Should().BeTrue();
        _sut.CancelCommand.Execute(null);
        _mockTaskProgressService.Verify(service => service.CancelCurrentTask(), Times.Once);
    }

    [Fact]
    public void PageOwnedTask_HidesGlobalBarThroughFailureAndCompletion_AndNextGlobalTaskAppears()
    {
        _mockTaskProgressService.Setup(service => service.IsTaskRunning).Returns(true);
        _mockTaskProgressService.Raise(service => service.ProgressUpdated += null, this,
            new TaskProgressDetail { ShowInTaskBar = false, StatusText = "Driver scan", LogLevel = LogLevel.Error });
        _sut.IsTaskRunning.Should().BeTrue();
        _sut.IsLoading.Should().BeFalse();
        _sut.IsTaskFailed.Should().BeFalse();
        _mockTaskProgressService.Setup(service => service.IsTaskRunning).Returns(false);
        _mockTaskProgressService.Raise(service => service.ProgressUpdated += null, this,
            new TaskProgressDetail { ShowInTaskBar = false, IsCompletion = true });
        _sut.IsTaskRunning.Should().BeFalse();
        _sut.IsLoading.Should().BeFalse();
        _mockTaskProgressService.Setup(service => service.IsTaskRunning).Returns(true);
        _mockTaskProgressService.Raise(service => service.ProgressUpdated += null, this,
            new TaskProgressDetail { StatusText = "Install application", IsIndeterminate = true });
        _sut.IsLoading.Should().BeTrue();
        _sut.AppName.Should().Be("Install application");
    }

    [Fact]
    public void OnProgressUpdated_TaskRunning_SetsIsLoadingTrue()
    {
        _mockTaskProgressService.Setup(s => s.IsTaskRunning).Returns(true);

        _mockTaskProgressService.Raise(
            s => s.ProgressUpdated += null,
            this,
            new TaskProgressDetail { StatusText = "Working..." });

        _sut.IsLoading.Should().BeTrue();
    }

    [Fact]
    public void NormalZeroPercentStart_IsNotAFailure_AndMeasuredProgressIsExposed()
    {
        _mockTaskProgressService.Setup(x => x.IsTaskRunning).Returns(true);
        _mockTaskProgressService.Raise(x => x.ProgressUpdated += null, this,
            new TaskProgressDetail { Progress = 0, StatusText = "Cleaner", IsIndeterminate = true });
        _sut.IsTaskFailed.Should().BeFalse();
        _sut.IsTaskRunning.Should().BeTrue();
        _mockTaskProgressService.Raise(x => x.ProgressUpdated += null, this,
            new TaskProgressDetail { Progress = 42, StatusText = "Checking 420/1000" });
        _sut.ProgressValue.Should().Be(42);
        _sut.IsIndeterminate.Should().BeFalse();
        _mockTaskProgressService.Setup(x => x.IsTaskRunning).Returns(false);
        _mockTaskProgressService.Raise(x => x.ProgressUpdated += null, this,
            new TaskProgressDetail { Progress = 100, IsCompletion = true });
        _sut.IsTaskRunning.Should().BeFalse();
        _sut.IsTaskFailed.Should().BeFalse();
    }

    [Fact]
    public void QueuedLifecycle_CapturesCancellationBeforeTheServiceReleasesItsToken()
    {
        Queue<Action> updates = new();
        _mockDispatcherService.Setup(x => x.RunOnUIThread(It.IsAny<Action>())).Callback<Action>(updates.Enqueue);
        using var cancellation = new CancellationTokenSource();
        _mockTaskProgressService.Setup(x => x.IsTaskRunning).Returns(true);
        _mockTaskProgressService.Setup(x => x.CurrentTaskCancellationSource).Returns(cancellation);
        _mockTaskProgressService.Raise(x => x.ProgressUpdated += null, this,
            new TaskProgressDetail { Progress = 0, StatusText = "Cleaner" });
        cancellation.Cancel();
        _mockTaskProgressService.Setup(x => x.IsTaskRunning).Returns(false);
        _mockTaskProgressService.Raise(x => x.ProgressUpdated += null, this,
            new TaskProgressDetail { Progress = 100, IsCompletion = true });
        _mockTaskProgressService.Setup(x => x.CurrentTaskCancellationSource).Returns((CancellationTokenSource?)null);
        while (updates.TryDequeue(out var update)) update();
        _sut.IsLoading.Should().BeFalse();
        _sut.IsTaskRunning.Should().BeFalse();
        _sut.IsTaskFailed.Should().BeFalse();
    }

    [Fact]
    public void OnProgressUpdated_TaskRunning_ProgressNonZero_DoesNotSetTaskFailed()
    {
        _mockTaskProgressService.Setup(s => s.IsTaskRunning).Returns(true);

        _mockTaskProgressService.Raise(
            s => s.ProgressUpdated += null,
            this,
            new TaskProgressDetail { Progress = 50, StatusText = "Half done" });

        _sut.IsTaskFailed.Should().BeFalse();
    }

    [Fact]
    public void OnProgressUpdated_TaskJustStopped_Failed_KeepsLoadingTrue()
    {
        _mockTaskProgressService.Setup(s => s.IsTaskRunning).Returns(true);

        // First, simulate a running task that fails
        _mockTaskProgressService.Raise(
            s => s.ProgressUpdated += null,
            this,
            new TaskProgressDetail { Progress = 0, StatusText = "Error", LogLevel = LogLevel.Error });

        _sut.IsTaskFailed.Should().BeTrue();
        _sut.IsLoading.Should().BeTrue();

        // Now task stops
        _mockTaskProgressService.Setup(s => s.IsTaskRunning).Returns(false);
        _mockTaskProgressService.Raise(
            s => s.ProgressUpdated += null,
            this,
            new TaskProgressDetail { StatusText = "" });

        // Failed task should stay visible
        _sut.IsLoading.Should().BeTrue();
    }

    [Fact]
    public void OnProgressUpdated_TaskJustStopped_Cancelled_HidesImmediately()
    {
        // Simulate a running task that reports failure (Progress=0)
        var cts = new CancellationTokenSource();
        _mockTaskProgressService.Setup(s => s.IsTaskRunning).Returns(true);
        _mockTaskProgressService.Setup(s => s.CurrentTaskCancellationSource).Returns(cts);

        _mockTaskProgressService.Raise(
            s => s.ProgressUpdated += null,
            this,
            new TaskProgressDetail { Progress = 0, StatusText = "Cancelled", LogLevel = LogLevel.Error });

        _sut.IsTaskFailed.Should().BeTrue();
        _sut.IsLoading.Should().BeTrue();

        // User cancels — token is now cancelled
        cts.Cancel();

        // Task stops
        _mockTaskProgressService.Setup(s => s.IsTaskRunning).Returns(false);
        _mockTaskProgressService.Raise(
            s => s.ProgressUpdated += null,
            this,
            new TaskProgressDetail { StatusText = "" });

        // Should hide immediately, not show "click to see details"
        _sut.IsLoading.Should().BeFalse();
        _sut.IsTaskFailed.Should().BeFalse();
    }

    [Fact]
    public void OnProgressUpdated_TaskJustStopped_FailedNotCancelled_ShowsClickToSeeDetails()
    {
        // Simulate a running task with a non-cancelled CTS that reports failure
        var cts = new CancellationTokenSource();
        _mockTaskProgressService.Setup(s => s.IsTaskRunning).Returns(true);
        _mockTaskProgressService.Setup(s => s.CurrentTaskCancellationSource).Returns(cts);

        _mockTaskProgressService.Raise(
            s => s.ProgressUpdated += null,
            this,
            new TaskProgressDetail { Progress = 0, StatusText = "Error occurred", LogLevel = LogLevel.Error });

        _sut.IsTaskFailed.Should().BeTrue();

        // Task stops (NOT cancelled)
        _mockTaskProgressService.Setup(s => s.IsTaskRunning).Returns(false);
        _mockTaskProgressService.Raise(
            s => s.ProgressUpdated += null,
            this,
            new TaskProgressDetail { StatusText = "" });

        // Failed task should stay visible
        _sut.IsLoading.Should().BeTrue();
    }

    [Fact]
    public void OnProgressUpdated_QueueTotalGreaterThan1_ShowsQueue()
    {
        _mockTaskProgressService.Setup(s => s.IsTaskRunning).Returns(true);

        _mockTaskProgressService.Raise(
            s => s.ProgressUpdated += null,
            this,
            new TaskProgressDetail
            {
                StatusText = "Installing",
                QueueTotal = 3,
                QueueCurrent = 1,
                QueueNextItemName = "App B"
            });

        _sut.IsQueueVisible.Should().BeTrue();
        _sut.QueueStatusText.Should().Be("1 / 3");
        _sut.QueueNextItemName.Should().Be("Next: App B");
    }

    [Fact]
    public void OnProgressUpdated_QueueTotalGreaterThan1_EmptyNextItemName_SetsEmptyQueueNextItemName()
    {
        _mockTaskProgressService.Setup(s => s.IsTaskRunning).Returns(true);

        _mockTaskProgressService.Raise(
            s => s.ProgressUpdated += null,
            this,
            new TaskProgressDetail
            {
                StatusText = "Installing",
                QueueTotal = 2,
                QueueCurrent = 2,
                QueueNextItemName = ""
            });

        _sut.QueueNextItemName.Should().BeEmpty();
    }

    [Fact]
    public void OnProgressUpdated_QueueTotal1OrLess_HidesQueue()
    {
        _mockTaskProgressService.Setup(s => s.IsTaskRunning).Returns(true);

        _mockTaskProgressService.Raise(
            s => s.ProgressUpdated += null,
            this,
            new TaskProgressDetail { StatusText = "Installing", QueueTotal = 1 });

        _sut.IsQueueVisible.Should().BeFalse();
        _sut.QueueStatusText.Should().BeEmpty();
        _sut.QueueNextItemName.Should().BeEmpty();
    }

    [Fact]
    public void OnProgressUpdated_MultiScriptMode_UpdatesActiveScriptCount()
    {
        _mockTaskProgressService.Setup(s => s.IsTaskRunning).Returns(true);

        _mockTaskProgressService.Raise(
            s => s.ProgressUpdated += null,
            this,
            new TaskProgressDetail { ScriptSlotCount = 3, ScriptSlotIndex = 0 });

        _sut.ActiveScriptCount.Should().Be(3);
    }

    [Fact]
    public void OnProgressUpdated_MultiScriptMode_RaisesScriptProgressReceived()
    {
        int receivedSlotIndex = -1;
        TaskProgressDetail? receivedDetail = null;
        _sut.ScriptProgressReceived += (index, detail) =>
        {
            receivedSlotIndex = index;
            receivedDetail = detail;
        };

        _mockTaskProgressService.Setup(s => s.IsTaskRunning).Returns(true);
        var progressDetail = new TaskProgressDetail { ScriptSlotCount = 2, ScriptSlotIndex = 1, StatusText = "Script 2" };

        _mockTaskProgressService.Raise(
            s => s.ProgressUpdated += null,
            this,
            progressDetail);

        receivedSlotIndex.Should().Be(1);
        receivedDetail.Should().BeSameAs(progressDetail);
    }

    [Fact]
    public void OnProgressUpdated_MultiScriptComplete_ResetsActiveScriptCount()
    {
        _mockTaskProgressService.Setup(s => s.IsTaskRunning).Returns(true);

        // First, enter multi-script mode
        _mockTaskProgressService.Raise(
            s => s.ProgressUpdated += null,
            this,
            new TaskProgressDetail { ScriptSlotCount = 2, ScriptSlotIndex = 0 });
        _sut.ActiveScriptCount.Should().Be(2);

        // Then, receive completion (ScriptSlotCount = 0, ScriptSlotIndex = -1)
        _mockTaskProgressService.Raise(
            s => s.ProgressUpdated += null,
            this,
            new TaskProgressDetail { ScriptSlotCount = 0, ScriptSlotIndex = -1 });

        _sut.ActiveScriptCount.Should().Be(0);
    }

    [Fact]
    public void CloseFailedTaskCommand_ResetsLoadingAndFailedState()
    {
        // Set up failed state
        _mockTaskProgressService.Setup(s => s.IsTaskRunning).Returns(true);
        _mockTaskProgressService.Raise(
            s => s.ProgressUpdated += null,
            this,
            new TaskProgressDetail { Progress = 0, StatusText = "Error", LogLevel = LogLevel.Error });

        _sut.IsTaskFailed.Should().BeTrue();
        _sut.IsLoading.Should().BeTrue();

        _sut.CloseFailedTaskCommand.Execute(null);

        _sut.IsTaskFailed.Should().BeFalse();
        _sut.IsLoading.Should().BeFalse();
    }

    [Fact]
    public void Dispose_UnsubscribesFromProgressUpdated()
    {
        var sut = new TaskProgressViewModel(
            _mockTaskProgressService.Object,
            _mockDispatcherService.Object,
            _mockDialogService.Object,
            _mockLocalizationService.Object,
            _mockLogService.Object);

        sut.Dispose();

        // After dispose, raising the event should not cause any changes
        _mockTaskProgressService.Setup(s => s.IsTaskRunning).Returns(true);
        _mockTaskProgressService.Raise(
            s => s.ProgressUpdated += null,
            this,
            new TaskProgressDetail { StatusText = "Should not appear" });

        sut.IsLoading.Should().BeFalse();
        sut.AppName.Should().BeEmpty();
    }
}
