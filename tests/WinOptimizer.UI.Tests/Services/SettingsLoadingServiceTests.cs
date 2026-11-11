using System.Collections.ObjectModel;
using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Events;
using WinOptimizer.Core.Features.Common.Events.UI;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Models;
using WinOptimizer.UI.Features.Common.Services;
using WinOptimizer.UI.Features.Optimize.ViewModels;
using Xunit;

namespace WinOptimizer.UI.Tests.Services;

public class SettingsLoadingServiceTests
{
    private readonly Mock<ISystemSettingsDiscoveryService> _mockDiscoveryService = new();
    private readonly Mock<IEventBus> _mockEventBus = new();
    private readonly Mock<ILogService> _mockLogService = new();
    private readonly Mock<IInitializationService> _mockInitializationService = new();
    private readonly Mock<IComboBoxResolver> _mockComboBoxResolver = new();
    private readonly Mock<ISettingPreparationPipeline> _mockPreparationPipeline = new();
    private readonly Mock<ISettingViewModelFactory> _mockViewModelFactory = new();

    private readonly SettingsLoadingService _sut;

    public SettingsLoadingServiceTests()
    {
        _sut = new SettingsLoadingService(
            _mockDiscoveryService.Object,
            _mockEventBus.Object,
            _mockLogService.Object,
            _mockInitializationService.Object,
            _mockComboBoxResolver.Object,
            _mockPreparationPipeline.Object,
            _mockViewModelFactory.Object);
    }

    [Fact]
    public async Task LoadConfiguredSettingsAsync_SkipsSettingsWithFailedState()
    {
        var settings = new List<SettingDefinition>
        {
            new() { Id = "GoodSetting", Name = "Good", Description = "Good desc", InputType = InputType.Toggle },
            new() { Id = "BadSetting", Name = "Bad", Description = "Bad desc", InputType = InputType.Toggle }
        };

        _mockPreparationPipeline
            .Setup(p => p.PrepareSettings("TestFeature"))
            .Returns(settings);

        _mockDiscoveryService
            .Setup(d => d.GetSettingStatesAsync(It.IsAny<IReadOnlyList<SettingDefinition>>()))
            .ReturnsAsync(new Dictionary<string, SettingStateResult>
            {
                { "GoodSetting", new SettingStateResult { Success = true, IsEnabled = true } },
                { "BadSetting", new SettingStateResult { Success = false, ErrorMessage = "Not found" } }
            });

        var mockVm = CreateMockSettingItemViewModel("GoodSetting");
        _mockViewModelFactory
            .Setup(f => f.CreateAsync(
                It.Is<SettingDefinition>(s => s.Id == "GoodSetting"),
                It.IsAny<SettingStateResult>(),
                It.IsAny<ISettingsFeatureViewModel?>()))
            .ReturnsAsync(mockVm);

        var result = await _sut.LoadConfiguredSettingsAsync(
            "TestFeature", "Loading...", null);

        result.Should().ContainSingle().Which.SettingId.Should().Be("GoodSetting");
        _mockViewModelFactory.Verify(f => f.CreateAsync(
            It.Is<SettingDefinition>(s => s.Id == "BadSetting"), It.IsAny<SettingStateResult>(),
            It.IsAny<ISettingsFeatureViewModel?>()), Times.Never);
    }

    [Fact]
    public async Task LoadConfiguredSettingsAsync_PublishesTooltipUpdatedEvents()
    {
        var tooltipData = new SettingTooltipData { SettingId = "Setting1", DisplayValue = "test" };
        var settings = new List<SettingDefinition>
        {
            new() { Id = "Setting1", Name = "Setting 1", Description = "Desc 1", InputType = InputType.Toggle }
        };

        _mockPreparationPipeline
            .Setup(p => p.PrepareSettings("TestFeature"))
            .Returns(settings);

        _mockDiscoveryService
            .Setup(d => d.GetSettingStatesAsync(It.IsAny<IReadOnlyList<SettingDefinition>>()))
            .ReturnsAsync(new Dictionary<string, SettingStateResult>
            {
                { "Setting1", new SettingStateResult { Success = true, TooltipData = tooltipData } }
            });

        var mockVm = CreateMockSettingItemViewModel("Setting1");
        _mockViewModelFactory
            .Setup(f => f.CreateAsync(
                It.IsAny<SettingDefinition>(),
                It.IsAny<SettingStateResult>(),
                It.IsAny<ISettingsFeatureViewModel?>()))
            .ReturnsAsync(mockVm);

        await _sut.LoadConfiguredSettingsAsync(
            "TestFeature", "Loading...", null);

        _mockEventBus.Verify(
            e => e.Publish(It.Is<TooltipUpdatedEvent>(evt => evt.SettingId == "Setting1")),
            Times.Once);
    }

    [Fact]
    public async Task LoadConfiguredSettingsAsync_WhenExceptionThrown_CompletesInitializationAndRethrows()
    {

        _mockPreparationPipeline
            .Setup(p => p.PrepareSettings("TestFeature"))
            .Throws(new Exception("Pipeline error"));

        var act = () => _sut.LoadConfiguredSettingsAsync(
            "TestFeature", "Loading...", null);

        await act.Should().ThrowAsync<Exception>().WithMessage("Pipeline error");
        _mockInitializationService.Verify(i => i.CompleteFeatureInitialization("TestFeature"), Times.Once);
    }

    [Fact]
    public async Task RefreshSettingStatesAsync_WithMixedInputTypes_ReturnsStatesForAll()
    {
        var toggleDef = new SettingDefinition
        {
            Id = "Toggle1", Name = "Toggle", Description = "Toggle desc", InputType = InputType.Toggle
        };
        var selectionDef = new SettingDefinition
        {
            Id = "Select1", Name = "Select", Description = "Select desc", InputType = InputType.Selection
        };
        var numericDef = new SettingDefinition
        {
            Id = "Numeric1", Name = "Numeric", Description = "Numeric desc", InputType = InputType.NumericRange
        };

        var vms = new[]
        {
            CreateMockSettingItemViewModel("Toggle1", toggleDef),
            CreateMockSettingItemViewModel("Select1", selectionDef),
            CreateMockSettingItemViewModel("Numeric1", numericDef)
        };

        _mockDiscoveryService
            .Setup(d => d.GetSettingStatesAsync(It.IsAny<IReadOnlyList<SettingDefinition>>()))
            .ReturnsAsync(new Dictionary<string, SettingStateResult>
            {
                { "Toggle1", new SettingStateResult { Success = true, IsEnabled = true } },
                { "Select1", new SettingStateResult { Success = true, CurrentValue = 1 } },
                { "Numeric1", new SettingStateResult { Success = true, CurrentValue = 300 } }
            });

        var result = await _sut.RefreshSettingStatesAsync(vms);

        result.Should().HaveCount(3);
        result["Toggle1"].IsEnabled.Should().BeTrue();
        result["Select1"].CurrentValue.Should().Be(1);
        result["Numeric1"].CurrentValue.Should().Be(300);
    }

    private static SettingItemViewModel CreateMockSettingItemViewModel(string settingId)
    {
        return CreateMockSettingItemViewModel(settingId, new SettingDefinition
        {
            Id = settingId,
            Name = settingId,
            Description = "Test",
            InputType = InputType.Toggle
        });
    }

    private static SettingItemViewModel CreateMockSettingItemViewModel(string settingId, SettingDefinition settingDefinition)
    {
        var config = new SettingItemViewModelConfig
        {
            SettingDefinition = settingDefinition,
            SettingId = settingId,
            Name = settingDefinition.Name,
            Description = settingDefinition.Description,
            GroupName = string.Empty,
            Icon = string.Empty,
            IconPack = "Custom",
            InputType = settingDefinition.InputType,
            IsSelected = false,
            OnText = "On",
            OffText = "Off",
            ActionButtonText = "Apply"
        };

        var mockSettingApp = new Mock<ISettingApplicationService>();
        var mockLog = new Mock<ILogService>();
        var mockDispatcher = new Mock<IDispatcherService>();
        mockDispatcher.Setup(d => d.RunOnUIThread(It.IsAny<Action>())).Callback<Action>(a => a());
        var mockDialog = new Mock<IDialogService>();
        var mockLocalization = new Mock<ILocalizationService>();
        mockLocalization.Setup(l => l.GetString(It.IsAny<string>())).Returns((string k) => k);
        var mockEventBus = new Mock<IEventBus>();
        var mockUserPrefs = new Mock<IUserPreferencesService>();
        var mockRegeditLauncher = new Mock<IRegeditLauncher>();

        return new SettingItemViewModel(
            config,
            mockSettingApp.Object,
            mockLog.Object,
            mockDispatcher.Object,
            mockDialog.Object,
            mockLocalization.Object,
            mockEventBus.Object,
            mockUserPrefs.Object,
            mockRegeditLauncher.Object);
    }
}
