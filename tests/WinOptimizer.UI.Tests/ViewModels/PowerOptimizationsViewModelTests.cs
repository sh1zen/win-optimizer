using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Events;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Optimize.Interfaces;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Optimize.ViewModels;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

public class PowerOptimizationsViewModelTests
{
    private readonly Mock<ISettingsLoadingService> _mockSettingsLoadingService;
    private readonly Mock<ILogService> _mockLogService;
    private readonly Mock<ILocalizationService> _mockLocalizationService;
    private readonly Mock<IDispatcherService> _mockDispatcherService;
    private readonly Mock<IDialogService> _mockDialogService;
    private readonly Mock<IEventBus> _mockEventBus;
    private readonly Mock<IApplicationModeService> _mockApplicationModeService;
    private readonly Mock<IPowerPlanComboBoxService> _mockPowerPlanComboBoxService;
    private readonly Mock<IPowerService> _mockPowerService;

    public PowerOptimizationsViewModelTests()
    {
        _mockSettingsLoadingService = new Mock<ISettingsLoadingService>();
        _mockLogService = new Mock<ILogService>();
        _mockLocalizationService = new Mock<ILocalizationService>();
        _mockDispatcherService = new Mock<IDispatcherService>();
        _mockDialogService = new Mock<IDialogService>();
        _mockEventBus = new Mock<IEventBus>();
        _mockApplicationModeService = new Mock<IApplicationModeService>();
        _mockPowerPlanComboBoxService = new Mock<IPowerPlanComboBoxService>();
        _mockPowerService = new Mock<IPowerService>();

        // Set up localization to return the key itself by default
        _mockLocalizationService
            .Setup(l => l.GetString(It.IsAny<string>()))
            .Returns((string key) => key);

        // Set up dispatcher to execute actions synchronously for testing
        _mockDispatcherService
            .Setup(d => d.RunOnUIThread(It.IsAny<Action>()))
            .Callback<Action>(action => action());

        _mockDispatcherService
            .Setup(d => d.RunOnUIThreadAsync(It.IsAny<Func<Task>>()))
            .Returns<Func<Task>>(asyncAction => asyncAction());
    }

    private PowerOptimizationsViewModel CreateViewModel()
    {
        return new PowerOptimizationsViewModel(
            _mockSettingsLoadingService.Object,
            _mockLogService.Object,
            _mockLocalizationService.Object,
            _mockDispatcherService.Object,
            _mockDialogService.Object,
            _mockEventBus.Object,
            _mockPowerPlanComboBoxService.Object,
            _mockPowerService.Object,
            _mockApplicationModeService.Object);
    }

    [Fact]
    public async Task TestModeDeletionNeverReachesThePowerService()
    {
        _mockApplicationModeService.Setup(mode => mode.CurrentMode).Returns(WinOptimizer.Core.Features.Common.Enums.WinOptimizerMode.Builder);
        using var vm = CreateViewModel();
        await vm.DeletePowerPlanAsync(new PowerPlanComboBoxOption
        { ExistsOnSystem = true, SystemPlan = new WinOptimizer.Core.Features.Optimize.Models.PowerPlan { Guid = Guid.NewGuid().ToString() } });
        _mockPowerService.VerifyNoOtherCalls();
        _mockDialogService.Verify(dialog => dialog.ShowWarningAsync("TrialMode_Blocked", It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task ConfirmedDeletionHoldsTheModeReservationUntilTheServiceCompletes()
    {
        var lease = new Mock<IDisposable>();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockApplicationModeService.Setup(mode => mode.BeginSystemOperation()).Returns(lease.Object);
        _mockDialogService.Setup(dialog => dialog.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()))
            .ReturnsAsync(new ConfirmationResponse { Confirmed = true });
        var guid = Guid.NewGuid().ToString();
        _mockPowerService.Setup(service => service.DeletePowerPlanAsync(guid)).Returns(completion.Task);
        using var vm = CreateViewModel();
        var deletion = vm.DeletePowerPlanAsync(new PowerPlanComboBoxOption
        { ExistsOnSystem = true, SystemPlan = new WinOptimizer.Core.Features.Optimize.Models.PowerPlan { Guid = guid } });
        Assert.False(deletion.IsCompleted);
        _mockApplicationModeService.Verify(mode => mode.BeginSystemOperation(), Times.Once);
        lease.Verify(reservation => reservation.Dispose(), Times.Never);
        completion.SetResult(true);
        await deletion;
        _mockPowerService.Verify(service => service.DeletePowerPlanAsync(guid), Times.Once);
        lease.Verify(reservation => reservation.Dispose(), Times.Once);
    }

    [Fact]
    public async Task TestModeEnteredDuringConfirmationPreventsDeletion()
    {
        _mockDialogService.Setup(dialog => dialog.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()))
            .Callback(() => _mockApplicationModeService.Setup(mode => mode.BeginSystemOperation())
                .Throws(new InvalidOperationException("TrialMode_Blocked")))
            .ReturnsAsync(new ConfirmationResponse { Confirmed = true });
        using var vm = CreateViewModel();
        await vm.DeletePowerPlanAsync(new PowerPlanComboBoxOption
        { ExistsOnSystem = true, SystemPlan = new WinOptimizer.Core.Features.Optimize.Models.PowerPlan { Guid = Guid.NewGuid().ToString() } });
        _mockPowerService.VerifyNoOtherCalls();
        _mockDialogService.Verify(dialog => dialog.ShowErrorAsync(It.Is<string>(message => message.Contains("TrialMode_Blocked")),
            It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task DeletePowerPlanAsync_WithNullPlan_ReturnsWithoutAction()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.DeletePowerPlanCommand.Should().NotBeNull();

        // Act
        await vm.DeletePowerPlanAsync(null);

        // Assert - no dialog or service calls expected
        _mockDialogService.Verify(
            d => d.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()),
            Times.Never);
    }

    [Fact]
    public async Task DeletePowerPlanAsync_WithActivePlan_ShowsInformationDialog()
    {
        // Arrange
        var vm = CreateViewModel();
        var activePlan = new PowerPlanComboBoxOption
        {
            DisplayName = "Active Plan",
            IsActive = true,
            ExistsOnSystem = true,
        };

        // Act
        await vm.DeletePowerPlanAsync(activePlan);

        // Assert
        _mockDialogService.Verify(
            d => d.ShowInformationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public async Task DeletePowerPlanAsync_WithPlanNotOnSystem_ShowsInformationDialog()
    {
        // Arrange
        var vm = CreateViewModel();
        var offlinePlan = new PowerPlanComboBoxOption
        {
            DisplayName = "Offline Plan",
            IsActive = false,
            ExistsOnSystem = false,
            SystemPlan = null,
        };

        // Act
        await vm.DeletePowerPlanAsync(offlinePlan);

        // Assert
        _mockDialogService.Verify(
            d => d.ShowInformationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
    }
}
