using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Extensions;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Infrastructure.Features.Common.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

public class TaskProgressServiceTests
{
    private readonly Mock<ILogService> _mockLog = new();
    private readonly TaskProgressService _sut;

    public TaskProgressServiceTests()
    {
        _sut = new TaskProgressService(_mockLog.Object);
    }

    // ── StartTask ──

    [Fact]
    public void NonCancellableTask_PreservesCapabilityThroughUpdates_AndNextTaskRestoresCancellation()
    {
        var capabilities = new List<bool>();
        _sut.ProgressUpdated += (_, detail) => capabilities.Add(detail.CanCancel);
        _sut.StartTask("Generate XML", true, showInTaskBar: true, canCancel: false);
        _sut.UpdateDetailedProgress(new() { Progress = 50 });
        _sut.CompleteTask();
        capabilities.Should().Equal(false, false, false);
        capabilities.Clear();
        _sut.StartTask("Scan");
        _sut.CompleteTask();
        capabilities.Should().Equal(true, true);
    }

    [Fact]
    public void PageOwnedProgress_CarriesPresentationThroughCompletion_AndNextTaskUsesGlobalBar()
    {
        var updates = new List<(bool Show, bool Running, bool Completion)>();
        _sut.ProgressUpdated += (_, detail) => updates.Add((detail.ShowInTaskBar, _sut.IsTaskRunning, detail.IsCompletion));
        var owner = _sut.StartTask("Drivers", true, showInTaskBar: false);
        _sut.UpdateDetailedProgress(new() { Progress = 25, StatusText = "Checking packages" });
        _sut.CurrentTaskCancellationSource.Should().BeSameAs(owner);
        _sut.CompleteTask();
        updates.Should().Equal((false, true, false), (false, true, false), (false, false, true));
        updates.Clear();
        _sut.StartTask("Install application");
        _sut.CompleteTask();
        updates.Should().Equal((true, true, false), (true, false, true));
    }

    [Fact]
    public void MultiScript_AfterPageOwnedTask_UsesGlobalPresentation()
    {
        _sut.StartTask("Drivers", true, showInTaskBar: false);
        _sut.CompleteTask();
        var visibility = new List<bool>();
        _sut.ProgressUpdated += (_, detail) => visibility.Add(detail.ShowInTaskBar);
        _sut.StartMultiScriptTask(["Script"]);
        _sut.UpdateDetailedProgress(new() { ScriptSlotCount = 1, ScriptSlotIndex = 0, Progress = 50 });
        visibility.Should().OnlyContain(show => show);
        _sut.CompleteTask();
    }

    [Fact]
    public void StartTask_ValidName_SetsRunningStateAndReturnsCancellationSource()
    {
        var cts = _sut.StartTask("Apply Settings");

        _sut.IsTaskRunning.Should().BeTrue();
        _sut.CurrentStatusText.Should().Be("Apply Settings");
        _sut.CurrentProgress.Should().Be(0);
        cts.Should().NotBeNull();
        cts.IsCancellationRequested.Should().BeFalse();
        _sut.CurrentTaskCancellationSource.Should().BeSameAs(cts);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void StartTask_NullOrEmptyName_ThrowsArgumentException(string? taskName)
    {
        var act = () => _sut.StartTask(taskName!);

        act.Should().Throw<ArgumentException>()
            .WithParameterName("taskName");
    }

    // ── UpdateProgress ──

    [Fact]
    public void UpdateProgress_WithinRunningTask_UpdatesProgressAndStatus()
    {
        _sut.StartTask("Running");

        _sut.UpdateProgress(50, "Halfway");

        _sut.CurrentProgress.Should().Be(50);
        _sut.CurrentStatusText.Should().Be("Halfway");
    }

    [Fact]
    public void UpdateProgress_WhenNoTaskRunning_DoesNothing()
    {
        // No task started, so the method should silently return
        var act = () => _sut.UpdateProgress(50, "Status");

        act.Should().NotThrow();
        _sut.CurrentProgress.Should().Be(0);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void UpdateProgress_OutOfRange_ThrowsArgumentOutOfRangeException(int progress)
    {
        _sut.StartTask("Running");

        var act = () => _sut.UpdateProgress(progress);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void UpdateProgress_WithoutStatusText_KeepsPreviousStatusText()
    {
        _sut.StartTask("Initial");

        _sut.UpdateProgress(25);

        _sut.CurrentStatusText.Should().Be("Initial");
        _sut.CurrentProgress.Should().Be(25);
    }

    // ── CompleteTask ──

    [Fact]
    public void CompleteTask_SetsProgressTo100AndClearsRunningState()
    {
        _sut.StartTask("Work");
        _sut.UpdateProgress(50, "Working...");

        _sut.CompleteTask();

        _sut.IsTaskRunning.Should().BeFalse();
        _sut.CurrentProgress.Should().Be(100);
        _sut.IsIndeterminate.Should().BeFalse();
        _sut.CurrentTaskCancellationSource.Should().BeNull();
    }

    [Fact]
    public void CompleteTask_RaisesProgressUpdatedWithCompletion()
    {
        _sut.StartTask("Work");
        TaskProgressDetail? received = null;
        _sut.ProgressUpdated += (_, detail) => received = detail;

        _sut.CompleteTask();

        received.Should().NotBeNull();
        received!.Progress.Should().Be(100);
        received.DetailedMessage.Should().Be("Task completed");
    }

    // ── CancelCurrentTask ──

    [Fact]
    public void CancelCurrentTask_WithRunningTask_RequestsCancellation()
    {
        var cts = _sut.StartTask("Cancellable");

        _sut.CancelCurrentTask();

        cts.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void StartMultiScriptTask_NullOrEmpty_ThrowsArgumentException()
    {
        var act = () => _sut.StartMultiScriptTask(Array.Empty<string>());

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void StartMultiScriptTask_FiresProgressUpdatedForEachSlot()
    {
        var received = new List<TaskProgressDetail>();
        _sut.ProgressUpdated += (_, detail) => received.Add(detail);

        _sut.StartMultiScriptTask(new[] { "A", "B" });

        received.Should().HaveCount(2);
        received[0].ScriptSlotIndex.Should().Be(0);
        received[0].ScriptSlotCount.Should().Be(2);
        received[1].ScriptSlotIndex.Should().Be(1);
        received[1].ScriptSlotCount.Should().Be(2);
    }

    /// <summary>Queued slot callbacks cannot revive completed scripts or add output to another operation.</summary>
    [Theory]
    [InlineData("completed")]
    [InlineData("completed-then-single")]
    [InlineData("replaced-single")]
    [InlineData("replaced-multi")]
    [InlineData("idle")]
    public void ScriptProgressCannotOutliveItsTask(string transition)
    {
        var context = new QueuedContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        CancellationTokenSource? owner = null;
        try
        {
            if (transition != "idle") owner = _sut.StartMultiScriptTask(["Old removal", "Other removal"]);
            var reporter = _sut.CreateScriptProgress(0);
            reporter.Report(new() { Progress = 65, StatusText = "Old status", TerminalOutput = "Old output", IsCompletion = true });
            if (transition.StartsWith("completed")) _sut.CompleteMultiScriptTask();
            if (transition is "completed-then-single" or "replaced-single" or "idle") _sut.StartTask("New task");
            else if (transition == "replaced-multi") _sut.StartMultiScriptTask(["New removal"]);
            var running = _sut.IsTaskRunning;
            var current = _sut.CurrentTaskCancellationSource;
            var updates = new List<TaskProgressDetail>();
            _sut.ProgressUpdated += (_, detail) => updates.Add(detail);
            context.Drain();
            Assert.Empty(updates);
            Assert.Empty(_sut.GetTerminalOutputLines());
            Assert.Equal(running, _sut.IsTaskRunning);
            Assert.Same(current, _sut.CurrentTaskCancellationSource);
        }
        finally { _sut.CompleteMultiScriptTask(); owner?.Dispose(); SynchronizationContext.SetSynchronizationContext(previous); }
    }

    [Fact]
    public void ScriptProgressRetainsSlotMetadataAndFinalDiagnosticsForItsCancelledOwner()
    {
        var context = new QueuedContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            _sut.StartMultiScriptTask(["First", "Second"]);
            var reporter = _sut.CreateScriptProgress(1);
            var updates = new List<TaskProgressDetail>();
            _sut.ProgressUpdated += (_, detail) => updates.Add(detail);
            reporter.Report(new() { Progress = 65, TerminalOutput = "Working" });
            context.Drain();
            _sut.CancelCurrentTask();
            reporter.Report(new() { IsCompletion = true, TerminalOutput = "Cancelled safely" });
            context.Drain();
            Assert.Equal(2, updates.Count);
            Assert.All(updates, detail => { Assert.Equal(1, detail.ScriptSlotIndex); Assert.Equal(2, detail.ScriptSlotCount); });
            Assert.True(updates[^1].IsCompletion);
            Assert.Equal(new[] { "[Second] Working", "[Second] Cancelled safely" }, _sut.GetTerminalOutputLines());
        }
        finally { _sut.CompleteMultiScriptTask(); SynchronizationContext.SetSynchronizationContext(previous); }
    }

    // ── CompleteMultiScriptTask ──

    [Fact]
    public void CompleteMultiScriptTask_ResetsStateAndFiresCompletionEvent()
    {
        _sut.StartMultiScriptTask(new[] { "Script1" });
        TaskProgressDetail? received = null;
        _sut.ProgressUpdated += (_, detail) => received = detail;

        _sut.CompleteMultiScriptTask();

        _sut.IsTaskRunning.Should().BeFalse();
        received.Should().NotBeNull();
        received!.ScriptSlotCount.Should().Be(0);
        received.Progress.Should().Be(100);
    }

    // ── UpdateDetailedProgress ──

    [Fact]
    public void UpdateDetailedProgress_WhenNoTaskRunning_DoesNothing()
    {
        var detail = new TaskProgressDetail { Progress = 50, StatusText = "Status" };

        var act = () => _sut.UpdateDetailedProgress(detail);

        act.Should().NotThrow();
        _sut.CurrentProgress.Should().Be(0);
    }

    [Fact]
    public void UpdateDetailedProgress_InvalidProgress_ThrowsOutOfRange()
    {
        _sut.StartTask("Work");

        var act = () => _sut.UpdateDetailedProgress(new TaskProgressDetail { Progress = 150 });

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void UpdateDetailedProgress_WithTerminalOutput_AccumulatesLines()
    {
        _sut.StartTask("Work");

        _sut.UpdateDetailedProgress(new TaskProgressDetail
        {
            Progress = 10,
            TerminalOutput = "Line 1"
        });
        _sut.UpdateDetailedProgress(new TaskProgressDetail
        {
            Progress = 20,
            TerminalOutput = "Line 2"
        });

        var lines = _sut.GetTerminalOutputLines();
        lines.Should().HaveCount(2);
        lines[0].Should().Be("Line 1");
        lines[1].Should().Be("Line 2");
    }

    [Fact]
    public void UpdateDetailedProgress_BlankTerminalOutput_IsFilteredAsNoise()
    {
        _sut.StartTask("Work");

        _sut.UpdateDetailedProgress(new TaskProgressDetail
        {
            Progress = 10,
            TerminalOutput = "   "
        });

        _sut.GetTerminalOutputLines().Should().BeEmpty();
    }

    /// <summary>Both public reporter factories retain the identity captured before dispatch.</summary>
    [Theory]
    [InlineData(false, "replaced")]
    [InlineData(true, "replaced")]
    [InlineData(false, "completed")]
    [InlineData(true, "completed")]
    [InlineData(false, "idle")]
    [InlineData(true, "idle")]
    public void QueuedProgressDoesNotAttachToAnotherTask(bool powerShell, string transition)
    {
        var context = new QueuedContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            if (transition != "idle") _sut.StartTask("First");
            var reporter = powerShell ? _sut.CreatePowerShellProgress() : _sut.CreateDetailedProgress();
            reporter.Report(new() { Progress = 37, StatusText = "Old status", TerminalOutput = "Old output" });
            if (transition == "completed") _sut.CompleteTask();
            var replacement = _sut.StartTask("Replacement");
            var updates = new List<TaskProgressDetail>();
            _sut.ProgressUpdated += (_, detail) => updates.Add(detail);
            context.Drain();
            Assert.Empty(updates);
            Assert.Equal("Replacement", _sut.CurrentStatusText);
            Assert.Equal(0, _sut.CurrentProgress);
            Assert.Same(replacement, _sut.CurrentTaskCancellationSource);
            Assert.DoesNotContain("Old output", _sut.GetTerminalOutputLines());
        }
        finally { _sut.CompleteTask(); SynchronizationContext.SetSynchronizationContext(previous); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReporterUpdatesItsOwnerIncludingFinalOutputAfterCancellation(bool powerShell)
    {
        var context = new QueuedContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            _sut.StartTask("Work");
            var reporter = powerShell ? _sut.CreatePowerShellProgress() : _sut.CreateDetailedProgress();
            reporter.Report(new() { Progress = 37, StatusText = "Measured", TerminalOutput = "Details" });
            context.Drain();
            Assert.Equal(37, _sut.CurrentProgress);
            Assert.Equal("Measured", _sut.CurrentStatusText);
            _sut.CancelCurrentTask();
            reporter.Report(new() { StatusText = "Cancelled", TerminalOutput = "Final diagnostic" });
            context.Drain();
            Assert.Equal("Cancelled", _sut.CurrentStatusText);
            Assert.Contains("Final diagnostic", _sut.GetTerminalOutputLines());
        }
        finally { _sut.CompleteTask(); SynchronizationContext.SetSynchronizationContext(previous); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnedCompletionOnlyCompletesTheMatchingTask(bool replaced)
    {
        var owner = _sut.StartTask("Owner");
        var current = replaced ? _sut.StartTask("Replacement") : owner;
        _sut.CompleteOwnedTask(owner);
        Assert.Equal(replaced, _sut.IsTaskRunning);
        if (replaced)
        {
            Assert.Same(current, _sut.CurrentTaskCancellationSource);
            Assert.False(current.IsCancellationRequested);
            _sut.CompleteTask();
        }
        else Assert.Null(_sut.CurrentTaskCancellationSource);
    }

    /// <summary>Delivers Progress callbacks deterministically without timing or a real UI dispatcher.</summary>
    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();
        public override void Post(SendOrPostCallback callback, object? state) => _queue.Enqueue((callback, state));
        public void Drain() { while (_queue.TryDequeue(out var work)) work.Callback(work.State); }
    }

    // ── Queue sticky state ──

    [Fact]
    public void UpdateDetailedProgress_WithQueueInfo_PersistsStickily()
    {
        _sut.StartTask("Queue Task");
        var receivedDetails = new List<TaskProgressDetail>();
        _sut.ProgressUpdated += (_, detail) => receivedDetails.Add(detail);

        // First update with queue info
        _sut.UpdateDetailedProgress(new TaskProgressDetail
        {
            Progress = 10,
            StatusText = "Item 1",
            QueueTotal = 5,
            QueueCurrent = 1,
            QueueNextItemName = "Item 2"
        });

        // Second update without queue info should still carry it
        _sut.UpdateDetailedProgress(new TaskProgressDetail
        {
            Progress = 20,
            StatusText = "Still Item 1"
        });

        receivedDetails.Should().HaveCountGreaterOrEqualTo(2);
        var lastDetail = receivedDetails[^1];
        lastDetail.QueueTotal.Should().Be(5);
        lastDetail.QueueCurrent.Should().Be(1);
    }
}
