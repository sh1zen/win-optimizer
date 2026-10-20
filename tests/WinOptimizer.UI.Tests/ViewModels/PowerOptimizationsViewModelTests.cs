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
    public void ModuleId_ReturnsPower()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act & Assert
        vm.ModuleId.Should().Be(FeatureIds.Power);
    }

    [Fact]
    public void DisplayName_ReturnsLocalizedPowerName()
    {
        // Arrange
        _mockLocalizationService
            .Setup(l => l.GetString("Feature_Power_Name"))
            .Returns("Power");

        var vm = CreateViewModel();

        // Act & Assert
        vm.DisplayName.Should().Be("Power");
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
