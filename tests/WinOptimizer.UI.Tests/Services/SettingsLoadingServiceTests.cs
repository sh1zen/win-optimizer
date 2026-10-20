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
    public async Task LoadConfiguredSettingsAsync_ReturnsViewModelsForAllSettings()
    {
        var settings = new List<SettingDefinition>
        {
            new() { Id = "Setting1", Name = "Setting 1", Description = "Desc 1", InputType = InputType.Toggle },
            new() { Id = "Setting2", Name = "Setting 2", Description = "Desc 2", InputType = InputType.Toggle }
        };

        _mockPreparationPipeline
            .Setup(p => p.PrepareSettings("TestFeature"))
            .Returns(settings);

        _mockDiscoveryService
            .Setup(d => d.GetSettingStatesAsync(It.IsAny<IReadOnlyList<SettingDefinition>>()))
            .ReturnsAsync(new Dictionary<string, SettingStateResult>
            {
                { "Setting1", new SettingStateResult { Success = true, IsEnabled = true } },
                { "Setting2", new SettingStateResult { Success = true, IsEnabled = false } }
            });

        var mockVm1 = CreateMockSettingItemViewModel("Setting1");
        var mockVm2 = CreateMockSettingItemViewModel("Setting2");
        mockVm1.TechnicalDetailSections = new[]
        {
            new TechnicalDetailSection(DetailRowType.Registry, "Registry", false,
                new[] { new TechnicalDetailRow { RegistryPath = @"HKCU\Software\Example" } })
        };
        mockVm1.HasBadgeData = true;

        _mockViewModelFactory
            .SetupSequence(f => f.CreateAsync(
                It.IsAny<SettingDefinition>(),
                It.IsAny<SettingStateResult>(),
                It.IsAny<ISettingsFeatureViewModel?>()))
            .ReturnsAsync(mockVm1)
            .ReturnsAsync(mockVm2);

        var result = await _sut.LoadConfiguredSettingsAsync(
            "TestFeature", "Loading...", null);

        result.Should().HaveCount(2);
        result[0].HasTechnicalDetails.Should().BeTrue();
        result[0].ShowInfoBadge.Should().BeTrue();
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
    public async Task LoadConfiguredSettingsAsync_StartsAndCompletesFeatureInitialization()
    {

        _mockPreparationPipeline
            .Setup(p => p.PrepareSettings("TestFeature"))
            .Returns(new List<SettingDefinition>());

        _mockDiscoveryService
            .Setup(d => d.GetSettingStatesAsync(It.IsAny<IReadOnlyList<SettingDefinition>>()))
            .ReturnsAsync(new Dictionary<string, SettingStateResult>());

        await _sut.LoadConfiguredSettingsAsync(
            "TestFeature", "Loading...", null);

        _mockInitializationService.Verify(i => i.StartFeatureInitialization("TestFeature"), Times.Once);
        _mockInitializationService.Verify(i => i.CompleteFeatureInitialization("TestFeature"), Times.Once);
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
    public async Task LoadConfiguredSettingsAsync_ResolvesComboBoxForSelectionTypeSettings()
    {
        var selectionSetting = new SettingDefinition
        {
            Id = "SelectSetting",
            Name = "Select",
            Description = "Select desc",
            InputType = InputType.Selection
        };

        _mockPreparationPipeline
            .Setup(p => p.PrepareSettings("TestFeature"))
            .Returns(new List<SettingDefinition> { selectionSetting });

        var rawValues = new Dictionary<string, object?> { { "PowerCfgValue", 1 } };
        _mockDiscoveryService
            .Setup(d => d.GetSettingStatesAsync(It.IsAny<IReadOnlyList<SettingDefinition>>()))
            .ReturnsAsync(new Dictionary<string, SettingStateResult>
            {
                { "SelectSetting", new SettingStateResult
                    { Success = true, CurrentValue = 1, RawValues = rawValues } }
            });

        _mockComboBoxResolver
            .Setup(r => r.ResolveCurrentValueAsync(
                selectionSetting,
                It.IsAny<Dictionary<string, object?>>()))
            .ReturnsAsync(2);

        var mockVm = CreateMockSettingItemViewModel("SelectSetting");
        _mockViewModelFactory
            .Setup(f => f.CreateAsync(
                It.IsAny<SettingDefinition>(),
                It.IsAny<SettingStateResult>(),
                It.IsAny<ISettingsFeatureViewModel?>()))
            .ReturnsAsync(mockVm);

        await _sut.LoadConfiguredSettingsAsync(
            "TestFeature", "Loading...", null);

        _mockComboBoxResolver.Verify(r => r.ResolveCurrentValueAsync(
            selectionSetting, It.IsAny<Dictionary<string, object?>>()), Times.Once);
    }

    [Fact]
    public async Task RefreshSettingStatesAsync_SelectionSettings_ResolvesComboBoxValues()
    {
        var selectionDef = new SettingDefinition
        {
            Id = "SelectSetting",
            Name = "Select Setting",
            Description = "Selection test",
            InputType = InputType.Selection
        };

        var vm = CreateMockSettingItemViewModel("SelectSetting", selectionDef);
        var rawValues = new Dictionary<string, object?> { { "PowerCfgValue", 1 } };

        _mockDiscoveryService
            .Setup(d => d.GetSettingStatesAsync(It.IsAny<IReadOnlyList<SettingDefinition>>()))
            .ReturnsAsync(new Dictionary<string, SettingStateResult>
            {
                { "SelectSetting", new SettingStateResult { Success = true, CurrentValue = 1, RawValues = rawValues } }
            });

        _mockComboBoxResolver
            .Setup(r => r.ResolveCurrentValueAsync(
                selectionDef,
                It.IsAny<Dictionary<string, object?>>()))
            .ReturnsAsync(2);

        var result = await _sut.RefreshSettingStatesAsync(new[] { vm });

        result["SelectSetting"].CurrentValue.Should().Be(2);
        _mockComboBoxResolver.Verify(r => r.ResolveCurrentValueAsync(
            selectionDef, It.IsAny<Dictionary<string, object?>>()), Times.Once);
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
