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
    public async Task CheckOperationsAndCloseAsync_WhenBeforeShutdownThrows_LogsErrorAndContinues()
    {
        var service = CreateService();
        service.BeforeShutdown = () => throw new InvalidOperationException("Cleanup failed");

        _mockTaskProgressService.Setup(t => t.IsTaskRunning).Returns(false);

        await service.CheckOperationsAndCloseAsync();

        _mockLogService.Verify(
            l => l.LogError(It.Is<string>(s => s.Contains("Error running cleanup tasks")), It.IsAny<Exception>()),
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
    public async Task CheckOperationsAndCloseAsync_WhenConfirmationThrows_LogsAndShutsDown()
    {
        var service = CreateService();
        _mockTaskProgressService.Setup(task => task.IsTaskRunning).Returns(true);
        _mockDialogService.Setup(dialog => dialog.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()))
            .ThrowsAsync(new InvalidOperationException("Dialog unavailable"));

        var result = await service.CheckOperationsAndCloseAsync();

        result.Success.Should().BeTrue();
        _shutdownCalled.Should().BeTrue();
        _mockLogService.Verify(log => log.LogError(It.Is<string>(message => message.Contains("Error in CheckOperationsAndCloseAsync")), It.IsAny<Exception>()), Times.Once);
        _mockDialogService.Verify(dialog => dialog.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()), Times.Once);
        _mockDialogService.VerifyNoOtherCalls();
    }
}
