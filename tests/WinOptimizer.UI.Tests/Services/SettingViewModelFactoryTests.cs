using System.Collections.ObjectModel;
using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Events;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Models;
using WinOptimizer.UI.Features.Common.Services;
using WinOptimizer.UI.Features.Optimize.ViewModels;
using Xunit;

namespace WinOptimizer.UI.Tests.Services;

public class SettingViewModelFactoryTests
{
    private readonly Mock<ISettingApplicationService> _mockSettingApplicationService = new();
    private readonly Mock<ILogService> _mockLogService = new();
    private readonly Mock<IDispatcherService> _mockDispatcherService = new();
    private readonly Mock<IDialogService> _mockDialogService = new();
    private readonly Mock<IEventBus> _mockEventBus = new();
    private readonly Mock<IRegeditLauncher> _mockRegeditLauncher = new();
    private readonly Mock<ILocalizationService> _mockLocalizationService = new();
    private readonly Mock<IUserPreferencesService> _mockUserPreferencesService = new();
    private readonly Mock<IComboBoxSetupService> _mockComboBoxSetupService = new();
    private readonly Mock<IComboBoxResolver> _mockComboBoxResolver = new();
    private readonly Mock<ISettingViewModelEnricher> _mockEnricher = new();
    private readonly Mock<IApplicationModeService> _mockApplicationModeService = new();
    private readonly Mock<IConfigReviewDiffService> _reviewDiff = new();

    [Fact]
    public async Task TrialMode_RecreatedViewModelRetainsAuthoredState()
    {
        _mockApplicationModeService.SetupGet(x => x.CurrentMode).Returns(WinOptimizerMode.Builder);
        _mockApplicationModeService.Setup(x => x.GetBuilderEdits()).Returns(new[]
            { new BuilderEdit { SettingId = "draft", InputType = InputType.Toggle, IsSelected = true } });
        var definition = new SettingDefinition { Id = "draft", Name = "Draft", Description = "", InputType = InputType.Toggle };
        using var result = await _sut.CreateAsync(definition, new SettingStateResult { IsEnabled = false }, null);
        result.IsSelected.Should().BeTrue();
        _mockApplicationModeService.Raise(m => m.BuilderModeExiting += null, EventArgs.Empty);
        result.IsSelected.Should().BeFalse();
        _mockSettingApplicationService.VerifyNoOtherCalls();
    }

    private readonly SettingViewModelDependencies _deps;
    private readonly SettingViewModelFactory _sut;

    public SettingViewModelFactoryTests()
    {
        _mockLocalizationService
            .Setup(l => l.GetString(It.IsAny<string>()))
            .Returns((string key) => key);

        _mockDispatcherService
            .Setup(d => d.RunOnUIThread(It.IsAny<Action>()))
            .Callback<Action>(a => a());

        _mockDispatcherService
            .Setup(d => d.RunOnUIThreadAsync(It.IsAny<Func<Task>>()))
            .Callback<Func<Task>>(a => a().GetAwaiter().GetResult())
            .Returns(Task.CompletedTask);

        _deps = new SettingViewModelDependencies(
            _mockSettingApplicationService.Object,
            _mockLogService.Object,
            _mockDispatcherService.Object,
            _mockDialogService.Object,
            _mockEventBus.Object,
            _mockRegeditLauncher.Object,
            _mockApplicationModeService.Object);

        _sut = new SettingViewModelFactory(
            _deps,
            _mockLogService.Object,
            _mockLocalizationService.Object,
            _mockUserPreferencesService.Object,
            _mockComboBoxSetupService.Object,
            _mockComboBoxResolver.Object,
            _mockEnricher.Object,
            _reviewDiff.Object);
    }

    [Fact]
    public async Task CreateAsync_NullIconPack_UsesTheOriginalArtworkDefault()
    {
        var definition = CreateToggleSetting("custom-icon") with { Icon = "Camera", IconPack = null };
        using var result = await _sut.CreateAsync(definition, new SettingStateResult(), null);
        result.IconPack.Should().Be("Custom");
        result.Icon.Should().Be("Camera");
    }

    [Fact]
    public async Task CreateAsync_PassesTheAuthoritativeReviewQueueToTheSetting()
    {
        _mockApplicationModeService.Setup(m => m.CurrentMode).Returns(WinOptimizerMode.ConfigReview);
        var definition = new SettingDefinition { Id = "reviewed", Name = "Reviewed", Description = "", InputType = InputType.Toggle };
        using var result = await _sut.CreateAsync(definition, new SettingStateResult(), null);
        result.UpdateVisibility(string.Empty);
        result.IsVisible.Should().BeFalse();

        _reviewDiff.Setup(d => d.GetDiffForSetting("reviewed"))
            .Returns(new ConfigReviewDiff { SettingId = "reviewed" });
        result.UpdateVisibility(string.Empty);
        result.IsVisible.Should().BeTrue();
    }

    [Fact]
    public async Task CreateAsync_WhenRequiresAdvancedUnlock_SetsIsLocked()
    {
        var setting = CreateToggleSetting("AdvancedSetting") with { RequiresAdvancedUnlock = true };
        var state = new SettingStateResult { Success = true };

        _mockUserPreferencesService
            .Setup(u => u.GetPreferenceAsync("AdvancedPowerSettingsUnlocked", false))
            .ReturnsAsync(false);

        var result = await _sut.CreateAsync(setting, state, null);

        result.IsLocked.Should().BeTrue();
    }

    [Fact]
    public async Task CreateAsync_WhenAdvancedUnlocked_SetsIsLockedToFalse()
    {
        var setting = CreateToggleSetting("AdvancedSetting") with { RequiresAdvancedUnlock = true };
        var state = new SettingStateResult { Success = true };

        _mockUserPreferencesService
            .Setup(u => u.GetPreferenceAsync("AdvancedPowerSettingsUnlocked", false))
            .ReturnsAsync(true);

        var result = await _sut.CreateAsync(setting, state, null);

        result.IsLocked.Should().BeFalse();
    }

    [Fact]
    public async Task CreateAsync_NumericRangeSetting_SetsMinMaxValues()
    {
        var setting = CreateNumericRangeSetting("NumericSetting", 0, 100, "ms");
        var state = new SettingStateResult { CurrentValue = 50, Success = true };

        var result = await _sut.CreateAsync(setting, state, null);

        result.MinValue.Should().Be(0);
        result.MaxValue.Should().Be(100);
        result.Units.Should().Be("ms");
    }

    [Fact]
    public async Task CreateAsync_NumericRangeSetting_SetsNumericValue()
    {
        var setting = CreateNumericRangeSetting("NumericSetting", 0, 100, "ms");
        var state = new SettingStateResult { CurrentValue = 42, Success = true };

        var result = await _sut.CreateAsync(setting, state, null);

        result.NumericValue.Should().Be(42);
    }

    [Fact]
    public async Task CreateAsync_SelectionSetting_PopulatesComboBoxOptions()
    {
        var setting = CreateSelectionSetting("SelectionSetting");
        var state = new SettingStateResult { CurrentValue = 1, Success = true };

        var comboBoxResult = new ComboBoxSetupResult
        {
            Options = new ObservableCollection<ComboBoxDisplayOption>
            {
                new ComboBoxDisplayOption("Option A", 0),
                new ComboBoxDisplayOption("Option B", 1)
            },
            SelectedValue = 1,
            Success = true
        };

        _mockComboBoxSetupService
            .Setup(s => s.SetupComboBoxOptionsAsync(setting, It.IsAny<object?>()))
            .ReturnsAsync(comboBoxResult);

        var result = await _sut.CreateAsync(setting, state, null);

        result.ComboBoxOptions.Should().HaveCount(2);
    }

    [Fact]
    public async Task CreateAsync_SelectionSetting_SetsSelectedValue()
    {
        var setting = CreateSelectionSetting("SelectionSetting");
        var state = new SettingStateResult { CurrentValue = 1, Success = true };

        var comboBoxResult = new ComboBoxSetupResult
        {
            Options = new ObservableCollection<ComboBoxDisplayOption>
            {
                new ComboBoxDisplayOption("Option A", 0),
                new ComboBoxDisplayOption("Option B", 1)
            },
            SelectedValue = 1,
            Success = true
        };

        _mockComboBoxSetupService
            .Setup(s => s.SetupComboBoxOptionsAsync(setting, It.IsAny<object?>()))
            .ReturnsAsync(comboBoxResult);

        var result = await _sut.CreateAsync(setting, state, null);

        result.SelectedValue.Should().Be(1);
    }

    [Fact]
    public async Task CreateAsync_SelectionSetting_WhenSetupFails_LogsWarning()
    {
        var setting = CreateSelectionSetting("SelectionSetting");
        var state = new SettingStateResult { CurrentValue = 1, Success = true };

        _mockComboBoxSetupService
            .Setup(s => s.SetupComboBoxOptionsAsync(setting, It.IsAny<object?>()))
            .ThrowsAsync(new Exception("Setup failed"));

        var result = await _sut.CreateAsync(setting, state, null);

        _mockLogService.Verify(l => l.Log(LogLevel.Warning, It.Is<string>(s => s.Contains("SelectionSetting"))), Times.Once);
    }

    [Fact]
    public async Task CreateAsync_CallsApplyReviewDiff()
    {
        var setting = CreateToggleSetting("TestSetting");
        var state = new SettingStateResult { Success = true };

        await _sut.CreateAsync(setting, state, null);

        _mockEnricher.Verify(e => e.ApplyReviewDiff(It.IsAny<SettingItemViewModel>(), state), Times.Once);
    }

    [Fact]
    public async Task CreateAsync_NonSelectionType_SetsSelectedValueFromCurrentState()
    {
        var setting = CreateToggleSetting("TestSetting");
        var state = new SettingStateResult { CurrentValue = "SomeValue", Success = true };

        var result = await _sut.CreateAsync(setting, state, null);

        result.SelectedValue.Should().Be("SomeValue");
    }

    private static SettingDefinition CreateToggleSetting(
        string id, string name = "Test", string description = "Test Description")
    {
        return new SettingDefinition
        {
            Id = id,
            Name = name,
            Description = description,
            InputType = InputType.Toggle,
            GroupName = string.Empty,
            Icon = "TestIcon",
            IconPack = "Custom"
        };
    }

    private static SettingDefinition CreateNumericRangeSetting(
        string id, int min, int max, string units)
    {
        return new SettingDefinition
        {
            Id = id,
            Name = "Numeric",
            Description = "Numeric setting",
            InputType = InputType.NumericRange,
            GroupName = string.Empty,
            NumericRange = new NumericRangeMetadata
            {
                MinValue = min,
                MaxValue = max,
                Units = units
            }
        };
    }

    private static SettingDefinition CreateSelectionSetting(string id)
    {
        return new SettingDefinition
        {
            Id = id,
            Name = "Selection",
            Description = "Selection setting",
            InputType = InputType.Selection,
            GroupName = string.Empty
        };
    }
}
