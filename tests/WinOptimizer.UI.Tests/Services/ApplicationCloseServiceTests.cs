using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.Common.Services;
using Xunit;

namespace WinOptimizer.UI.Tests.Services;

public class ApplicationCloseServiceTests
{
    private readonly Mock<ILogService> _mockLogService = new();
    private readonly Mock<ITaskProgressService> _mockTaskProgressService = new();
    private readonly Mock<IDialogService> _mockDialogService = new();

    private bool _shutdownCalled;

    private ApplicationCloseService CreateService()
    {
        var svc = new ApplicationCloseService(
            _mockLogService.Object,
            _mockTaskProgressService.Object,
            _mockDialogService.Object);
        // Tests must not actually terminate the test host — swap in a no-op shutdown.
        svc.ShutdownAction = () => _shutdownCalled = true;
        return svc;
    }

    [Fact]
    public async Task CheckOperationsAndCloseAsync_WhenBeforeShutdownSet_InvokesHook()
    {
        var hookInvoked = false;
        var service = CreateService();
        service.BeforeShutdown = () =>
        {
            hookInvoked = true;
            return Task.CompletedTask;
        };

        _mockTaskProgressService.Setup(t => t.IsTaskRunning).Returns(false);

        await service.CheckOperationsAndCloseAsync();

        hookInvoked.Should().BeTrue();
        _shutdownCalled.Should().BeTrue();
    }

    [Fact]
    public async Task CheckOperationsAndCloseAsync_WhenBeforeShutdownThrows_PreservesTheApplication()
    {
        var service = CreateService();
        service.BeforeShutdown = () => throw new InvalidOperationException("Cleanup failed");

        _mockTaskProgressService.Setup(t => t.IsTaskRunning).Returns(false);

        var result = await service.CheckOperationsAndCloseAsync();
        Assert.False(result.Success);
        Assert.False(_shutdownCalled);

        _mockLogService.Verify(
            l => l.LogError(It.Is<string>(s => s.Contains("Error in CheckOperationsAndCloseAsync")), It.IsAny<Exception>()),
            Times.Once);
    }

    [Fact]
    public async Task CheckOperationsAndCloseAsync_WhenTaskRunning_UserCancels_ReturnsFailedResult()
    {
        var service = CreateService();

        _mockTaskProgressService.Setup(t => t.IsTaskRunning).Returns(true);
        _mockTaskProgressService.Setup(t => t.CurrentStatusText).Returns("Installing apps");

        _mockDialogService
            .Setup(d => d.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()))
            .ReturnsAsync(new ConfirmationResponse { Confirmed = false }); // User clicks Cancel

        var result = await service.CheckOperationsAndCloseAsync();

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("User cancelled application close");
        _shutdownCalled.Should().BeFalse();
    }

    [Fact]
    public async Task CheckOperationsAndCloseAsync_WhenTaskRunning_UserConfirms_CancelsTask()
    {
        var service = CreateService();

        _mockTaskProgressService.Setup(t => t.IsTaskRunning).Returns(true);
        _mockTaskProgressService.Setup(t => t.CurrentStatusText).Returns("Applying settings");

        _mockDialogService
            .Setup(d => d.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()))
            .ReturnsAsync(new ConfirmationResponse { Confirmed = true }); // User clicks Yes

        await service.CheckOperationsAndCloseAsync();

        _mockTaskProgressService.Verify(t => t.CancelCurrentTask(), Times.Once);
    }

    [Fact]
    public async Task CheckOperationsAndCloseAsync_WhenTaskRunning_NullStatusText_UsesDefaultMessage()
    {
        var service = CreateService();

        _mockTaskProgressService.Setup(t => t.IsTaskRunning).Returns(true);
        // The loose mock supplies null for the unconfigured status text.

        _mockDialogService
            .Setup(d => d.ShowConfirmationAsync(
                It.Is<ConfirmationRequest>(r => r.Message.Contains("an operation"))))
            .ReturnsAsync(new ConfirmationResponse { Confirmed = false });

        await service.CheckOperationsAndCloseAsync();

        _mockDialogService.Verify(
            d => d.ShowConfirmationAsync(
                It.Is<ConfirmationRequest>(r => r.Message.Contains("an operation"))),
            Times.Once);
    }

    [Fact]
    public async Task CheckOperationsAndCloseAsync_NoRunningTask_DoesNotShowConfirmationDialog()
    {
        var service = CreateService();

        _mockTaskProgressService.Setup(t => t.IsTaskRunning).Returns(false);

        await service.CheckOperationsAndCloseAsync();

        _mockDialogService.Verify(
            d => d.ShowConfirmationAsync(
                It.Is<ConfirmationRequest>(r => r.Title.Contains("Operation in Progress"))),
            Times.Never);
    }

    // Shutdown completes through the production close path for idle and active work.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckOperationsAndCloseAsync_ConfirmedClose_OnlyPromptsForRunningWork(bool isRunning)
    {
        var service = CreateService();
        _mockTaskProgressService.Setup(task => task.IsTaskRunning).Returns(isRunning);
        _mockDialogService.Setup(dialog => dialog.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()))
            .ReturnsAsync(new ConfirmationResponse { Confirmed = true });
        var cleanupCompleted = false;
        service.BeforeShutdown = () => { cleanupCompleted = true; return Task.CompletedTask; };
        var shutdownCount = 0;
        service.ShutdownAction = () =>
        {
            cleanupCompleted.Should().BeTrue();
            shutdownCount++;
        };

        var result = await service.CheckOperationsAndCloseAsync();

        result.Success.Should().BeTrue();
        shutdownCount.Should().Be(1);
        _mockDialogService.Verify(dialog => dialog.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()),
            isRunning ? Times.Once() : Times.Never());
        _mockDialogService.VerifyNoOtherCalls();
        _mockTaskProgressService.Verify(task => task.CancelCurrentTask(), isRunning ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task CheckOperationsAndCloseAsync_WhenConfirmationThrows_LogsAndPreservesTheApplication()
    {
        var service = CreateService();
        _mockTaskProgressService.Setup(task => task.IsTaskRunning).Returns(true);
        _mockDialogService.Setup(dialog => dialog.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()))
            .ThrowsAsync(new InvalidOperationException("Dialog unavailable"));

        var result = await service.CheckOperationsAndCloseAsync();

        result.Success.Should().BeFalse();
        _shutdownCalled.Should().BeFalse();
        _mockLogService.Verify(log => log.LogError(It.Is<string>(message => message.Contains("Error in CheckOperationsAndCloseAsync")), It.IsAny<Exception>()), Times.Once);
        _mockDialogService.Verify(dialog => dialog.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()), Times.Once);
        _mockDialogService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DecliningCloseNeverRunsCleanupOrCancelsTheOperation()
    {
        var service = CreateService();
        var cleanup = false;
        service.BeforeShutdown = () => { cleanup = true; return Task.CompletedTask; };
        _mockTaskProgressService.Setup(tasks => tasks.IsTaskRunning).Returns(true);
        _mockDialogService.Setup(dialog => dialog.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()))
            .ReturnsAsync(new ConfirmationResponse { Confirmed = false });
        Assert.False((await service.CheckOperationsAndCloseAsync()).Success);
        Assert.False(cleanup);
        _mockTaskProgressService.Verify(tasks => tasks.CancelCurrentTask(), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedCloseOrRestartWaitsForTaskCompletionAndPreferenceDrain(bool restart)
    {
        var tasks = new WinOptimizer.Infrastructure.Features.Common.Services.TaskProgressService(_mockLogService.Object);
        var owner = tasks.StartTask("Long work");
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmation = false; var cleanup = false; var installer = false; var exited = false;
        _mockDialogService.Setup(dialog => dialog.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()))
            .Callback(() => confirmation = true).ReturnsAsync(new ConfirmationResponse { Confirmed = true });
        var service = new ApplicationCloseService(_mockLogService.Object, tasks, _mockDialogService.Object)
        {
            BeforeShutdown = () => { Assert.True(confirmation); cleanup = true; return drain.Task; },
            ShutdownAction = () => exited = true, RestartShutdownAction = () => exited = true
        };
        var close = restart ? service.CheckOperationsAndRestartAsync(() => installer = true) : service.CheckOperationsAndCloseAsync();
        Assert.True(cleanup); Assert.True(owner.IsCancellationRequested);
        Assert.False(close.IsCompleted); Assert.False(exited); Assert.False(installer);
        drain.SetResult();
        Assert.False(close.IsCompleted);
        tasks.CompleteTask();
        Assert.True((await close).Success);
        Assert.True(exited); Assert.Equal(restart, installer);
    }

    [Fact]
    public async Task RestartIsCancelledBeforeLaunchingTheInstallerAndCoalescesConcurrentCloseRequests()
    {
        var gate = new TaskCompletionSource<ConfirmationResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockTaskProgressService.Setup(tasks => tasks.IsTaskRunning).Returns(true);
        _mockDialogService.Setup(dialog => dialog.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).Returns(gate.Task);
        var service = CreateService(); var launched = false;
        var restart = service.CheckOperationsAndRestartAsync(() => launched = true);
        var close = service.CheckOperationsAndCloseAsync();
        Assert.Same(restart, close);
        gate.SetResult(new ConfirmationResponse { Confirmed = false });
        Assert.False((await restart).Success); Assert.False(launched); Assert.False(_shutdownCalled);
        _mockDialogService.Verify(dialog => dialog.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()), Times.Once);
    }
}
