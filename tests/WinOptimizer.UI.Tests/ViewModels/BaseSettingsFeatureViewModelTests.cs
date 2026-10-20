using System.Collections.ObjectModel;
using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Events;
using WinOptimizer.Core.Features.Common.Events.Settings;
using WinOptimizer.Core.Features.Common.Events.UI;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Models;
using WinOptimizer.UI.Features.Optimize.ViewModels;
using Xunit;
using ISettingsLoadingService = WinOptimizer.UI.Features.Common.Interfaces.ISettingsLoadingService;

namespace WinOptimizer.UI.Tests.ViewModels;

/// <summary>
/// Concrete subclass of BaseSettingsFeatureViewModel used for testing.
/// </summary>
public class TestableSettingsFeatureViewModel : BaseSettingsFeatureViewModel
{
    public const string TestModuleId = "TestModule";
    public const string TestDisplayNameKey = "Feature_Test_Name";

    public override string ModuleId => TestModuleId;

    protected override string GetDisplayNameKey() => TestDisplayNameKey;

    public TestableSettingsFeatureViewModel(
        ISettingsLoadingService settingsLoadingService,
        ILogService logService,
        ILocalizationService localizationService,
        IDispatcherService dispatcherService,
        IEventBus eventBus,
        IApplicationModeService applicationModeService)
        : base(settingsLoadingService, logService, localizationService, dispatcherService, eventBus, applicationModeService)
    {
    }
}

public class BaseSettingsFeatureViewModelTests
{
    private readonly Mock<ISettingsLoadingService> _mockSettingsLoadingService;
    private readonly Mock<ILogService> _mockLogService;
    private readonly Mock<ILocalizationService> _mockLocalizationService;
    private readonly Mock<IDispatcherService> _mockDispatcherService;
    private readonly Mock<IEventBus> _mockEventBus;
    private readonly Mock<IApplicationModeService> _mockApplicationModeService;
    private readonly Mock<IConfigReviewDiffService> _reviewDiff = new();

    public BaseSettingsFeatureViewModelTests()
    {
        _mockSettingsLoadingService = new Mock<ISettingsLoadingService>();
        _mockLogService = new Mock<ILogService>();
        _mockLocalizationService = new Mock<ILocalizationService>();
        _mockDispatcherService = new Mock<IDispatcherService>();
        _mockEventBus = new Mock<IEventBus>();
        _mockApplicationModeService = new Mock<IApplicationModeService>();

        // Default localization: return the key itself
        _mockLocalizationService
            .Setup(l => l.GetString(It.IsAny<string>()))
            .Returns((string key) => key);

        // Dispatcher executes actions synchronously for testing
        _mockDispatcherService
            .Setup(d => d.RunOnUIThread(It.IsAny<Action>()))
            .Callback<Action>(action => action());

        _mockDispatcherService
            .Setup(d => d.RunOnUIThreadAsync(It.IsAny<Func<Task>>()))
            .Returns<Func<Task>>(asyncAction => asyncAction());

        // Event bus subscribe returns a mock subscription token
        _mockEventBus
            .Setup(e => e.Subscribe(It.IsAny<Action<SettingAppliedEvent>>()))
            .Returns(new Mock<ISubscriptionToken>().Object);
        _mockEventBus
            .Setup(e => e.SubscribeAsync(It.IsAny<Func<FilterStateChangedEvent, Task>>()))
            .Returns(new Mock<ISubscriptionToken>().Object);
        _mockEventBus
            .Setup(e => e.Subscribe(It.IsAny<Action<ReviewModeExitedEvent>>()))
            .Returns(new Mock<ISubscriptionToken>().Object);
    }

    private TestableSettingsFeatureViewModel CreateViewModel()
    {
        return new TestableSettingsFeatureViewModel(
            _mockSettingsLoadingService.Object,
            _mockLogService.Object,
            _mockLocalizationService.Object,
            _mockDispatcherService.Object,
            _mockEventBus.Object,
            _mockApplicationModeService.Object);
    }

    /// <summary>
    /// Creates a mock SettingItemViewModel with the given properties.
    /// Uses the real SettingItemViewModel constructor.
    /// </summary>
    private SettingItemViewModel CreateSettingItem(
        string settingId,
        string name,
        string description = "Description",
        string groupName = "Group1",
        InputType inputType = InputType.Toggle,
        bool isSelected = false,
        int numericValue = 0,
        object? selectedValue = null,
        NumericRangeMetadata? numericRange = null,
        ComboBoxMetadata? comboBox = null,
        IReadOnlyList<PowerCfgSetting>? powerCfgSettings = null)
    {
        var config = new SettingItemViewModelConfig
        {
            SettingDefinition = new SettingDefinition
            {
                Id = settingId,
                Name = name,
                Description = description,
                InputType = inputType,
                NumericRange = numericRange,
                ComboBox = comboBox,
                PowerCfgSettings = powerCfgSettings,
            },
            SettingId = settingId,
            Name = name,
            Description = description,
            GroupName = groupName,
            InputType = inputType,
            IsSelected = isSelected,
            Icon = "TestIcon",
            IconPack = "Custom",
        };

        var mockSettingAppService = new Mock<ISettingApplicationService>();
        var mockDialogService = new Mock<IDialogService>();

        var vm = new SettingItemViewModel(
            config,
            mockSettingAppService.Object,
            _mockLogService.Object,
            _mockDispatcherService.Object,
            mockDialogService.Object,
            _mockLocalizationService.Object,
            _mockEventBus.Object,
            applicationModeService: _mockApplicationModeService.Object, configReviewDiffService: _reviewDiff.Object);

        // Set post-construction values for non-Toggle types
        if (inputType == InputType.NumericRange)
            vm.NumericValue = numericValue;
        if (inputType == InputType.Selection && selectedValue != null)
            vm.SelectedValue = selectedValue;

        return vm;
    }

    private ObservableCollection<SettingItemViewModel> CreateSettingsCollection(params (string id, string name, string group)[] items)
    {
        var collection = new ObservableCollection<SettingItemViewModel>();
        foreach (var (id, name, group) in items)
        {
            collection.Add(CreateSettingItem(id, name, groupName: group));
        }
        return collection;
    }

    [Fact]
    public async Task LoadSettingsAsync_SetsIsLoadingTrueDuringLoad()
    {
        // Arrange
        var vm = CreateViewModel();
        bool wasLoadingDuringLoad = false;

        _mockSettingsLoadingService
            .Setup(s => s.LoadConfiguredSettingsAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<ISettingsFeatureViewModel>()))
            .ReturnsAsync(() =>
            {
                wasLoadingDuringLoad = vm.IsLoading;
                return new ObservableCollection<SettingItemViewModel>();
            });

        // Act
        await vm.LoadSettingsAsync();

        // Assert
        wasLoadingDuringLoad.Should().BeTrue();
        vm.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task LoadSettingsAsync_ConcurrentLoadGuard_DoesNotLoadTwice()
    {
        // Arrange
        var vm = CreateViewModel();
        var loadCount = 0;

        _mockSettingsLoadingService
            .Setup(s => s.LoadConfiguredSettingsAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<ISettingsFeatureViewModel>()))
            .ReturnsAsync(() =>
            {
                loadCount++;
                return new ObservableCollection<SettingItemViewModel>();
            });

        // Act - load twice
        await vm.LoadSettingsAsync();
        await vm.LoadSettingsAsync();

        // Assert - should only load once due to _settingsLoaded guard
        loadCount.Should().Be(1);
    }

    [Fact]
    public async Task LoadSettingsAsync_UsesFeatureIdentityAndSubscribesToEvents()
    {
        // Arrange
        var vm = CreateViewModel();

        _mockSettingsLoadingService
            .Setup(s => s.LoadConfiguredSettingsAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<ISettingsFeatureViewModel>()))
            .ReturnsAsync(new ObservableCollection<SettingItemViewModel>());

        // Act
        await vm.LoadSettingsAsync();

        _mockEventBus.Verify(
            e => e.Subscribe(It.IsAny<Action<SettingAppliedEvent>>()),
            Times.Once);
        _mockEventBus.Verify(
            e => e.SubscribeAsync(It.IsAny<Func<FilterStateChangedEvent, Task>>()),
            Times.Once);
        _mockEventBus.Verify(
            e => e.Subscribe(It.IsAny<Action<ReviewModeExitedEvent>>()),
            Times.Once);
        _mockSettingsLoadingService.Verify(
            s => s.LoadConfiguredSettingsAsync(
                TestableSettingsFeatureViewModel.TestModuleId,
                It.IsAny<string>(),
                vm),
            Times.Once);
    }

    [Fact]
    public async Task LoadSettingsAsync_OnError_SetsIsLoadingFalse()
    {
        // Arrange
        var vm = CreateViewModel();

        _mockSettingsLoadingService
            .Setup(s => s.LoadConfiguredSettingsAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<ISettingsFeatureViewModel>()))
            .ThrowsAsync(new InvalidOperationException("Test error"));

        // Act
        Func<Task> action = () => vm.LoadSettingsAsync();

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>();
        vm.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task LoadSettingsAsync_OnError_ResetsSettingsLoadedFlag_AllowsRetry()
    {
        // Arrange
        var vm = CreateViewModel();
        var callCount = 0;

        _mockSettingsLoadingService
            .Setup(s => s.LoadConfiguredSettingsAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<ISettingsFeatureViewModel>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                    throw new InvalidOperationException("First call fails");
                return new ObservableCollection<SettingItemViewModel>();
            });

        // Act - first call should fail
        Func<Task> firstCall = () => vm.LoadSettingsAsync();
        await firstCall.Should().ThrowAsync<InvalidOperationException>();

        // Second call should succeed because the flag was reset
        await vm.LoadSettingsAsync();

        // Assert
        callCount.Should().Be(2);
    }

    [Fact]
    public async Task RefreshSettingsAsync_ClearsAndReloadsSettings()
    {
        // Arrange
        var vm = CreateViewModel();
        var firstSettings = CreateSettingsCollection(("s1", "First", "G1"));
        var secondSettings = CreateSettingsCollection(("s2", "Second", "G2"));
        var callCount = 0;

        _mockSettingsLoadingService
            .Setup(s => s.LoadConfiguredSettingsAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<ISettingsFeatureViewModel>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount == 1 ? firstSettings : secondSettings;
            });

        await vm.LoadSettingsAsync();
        vm.Settings.Should().HaveCount(1);

        // Act
        await vm.RefreshSettingsAsync();

        // Assert
        vm.Settings.Should().HaveCount(1);
        vm.Settings[0].Name.Should().Be("Second");
    }

    [Fact]
    public async Task RefreshSettingsAsync_PublishesSettingsRefreshedEvent()
    {
        // Overview consumers must be notified when the settings collection is replaced.
        var vm = CreateViewModel();
        _mockSettingsLoadingService
            .Setup(s => s.LoadConfiguredSettingsAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<ISettingsFeatureViewModel>()))
            .ReturnsAsync(() => CreateSettingsCollection(("s1", "First", "G1")));

        await vm.LoadSettingsAsync();

        // Act
        await vm.RefreshSettingsAsync();

        // Assert — RefreshSettingsAsync publishes exactly once (LoadSettingsAsync itself does not).
        _mockEventBus.Verify(e => e.Publish(It.IsAny<SettingsRefreshedEvent>()), Times.Once);
    }

    [Theory]
    [InlineData(BuilderTarget.Config)]
    [InlineData(BuilderTarget.Autounattend)]
    public async Task ModeChanged_EnteringTestModeRetainsCardsValuesAndCounts(BuilderTarget target)
    {
        using var vm = CreateViewModel();
        var toggle = CreateSettingItem("toggle", "Toggle", isSelected: true);
        var selection = CreateSettingItem("selection", "Selection", inputType: InputType.Selection, selectedValue: 2);
        var numeric = CreateSettingItem("numeric", "Numeric", inputType: InputType.NumericRange, numericValue: 37);
        numeric.AcNumericValue = 41;
        numeric.DcNumericValue = 19;
        selection.AcValue = 1;
        selection.DcValue = 2;
        _mockSettingsLoadingService.Setup(s => s.LoadConfiguredSettingsAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ISettingsFeatureViewModel>()))
            .ReturnsAsync(() => new ObservableCollection<SettingItemViewModel> { toggle, selection, numeric });
        await vm.LoadSettingsAsync();
        var settings = vm.Settings;
        var groups = vm.GroupedSettings;
        _mockApplicationModeService.SetupGet(m => m.CurrentMode).Returns(WinOptimizerMode.Builder);
        _mockApplicationModeService.SetupGet(m => m.CurrentBuilderTarget).Returns(target);

        _mockApplicationModeService.Raise(m => m.ModeChanged += null, EventArgs.Empty);

        vm.Settings.Should().BeSameAs(settings);
        vm.GroupedSettings.Should().BeSameAs(groups);
        vm.Settings.Should().Equal(toggle, selection, numeric);
        vm.SettingsCount.Should().Be(3);
        toggle.IsSelected.Should().BeTrue();
        selection.SelectedValue.Should().Be(2);
        selection.AcValue.Should().Be(1);
        selection.DcValue.Should().Be(2);
        numeric.NumericValue.Should().Be(37);
        numeric.AcNumericValue.Should().Be(41);
        numeric.DcNumericValue.Should().Be(19);
        _mockSettingsLoadingService.Verify(s => s.LoadConfiguredSettingsAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ISettingsFeatureViewModel>()), Times.Once);
        _mockEventBus.Verify(e => e.Publish(It.IsAny<SettingsRefreshedEvent>()), Times.Never);

        // Switching export targets also retains any edits made during this session.
        toggle.IsSelected = false;
        _mockApplicationModeService.SetupGet(m => m.CurrentBuilderTarget)
            .Returns(target == BuilderTarget.Config ? BuilderTarget.Autounattend : BuilderTarget.Config);
        _mockApplicationModeService.Raise(m => m.ModeChanged += null, EventArgs.Empty);
        vm.Settings.Should().BeSameAs(settings);
        toggle.IsSelected.Should().BeFalse();
        _mockSettingsLoadingService.Verify(s => s.LoadConfiguredSettingsAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ISettingsFeatureViewModel>()), Times.Once);
    }

    [Fact]
    public async Task DiscardTestMode_RestoresOriginalValuesWithoutReloadingCards()
    {
        using var vm = CreateViewModel();
        var draft = CreateSettingsCollection(("s1", "Setting", "Group"));
        _mockSettingsLoadingService.Setup(s => s.LoadConfiguredSettingsAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ISettingsFeatureViewModel>()))
            .ReturnsAsync(draft);
        await vm.LoadSettingsAsync();
        _mockApplicationModeService.SetupGet(m => m.CurrentMode).Returns(WinOptimizerMode.Builder);
        _mockApplicationModeService.Raise(m => m.ModeChanged += null, EventArgs.Empty);
        draft[0].IsSelected = true;
        _mockApplicationModeService.Raise(m => m.BuilderModeExiting += null, EventArgs.Empty);
        _mockApplicationModeService.SetupGet(m => m.CurrentMode).Returns(WinOptimizerMode.Normal);
        _mockApplicationModeService.Raise(m => m.ModeChanged += null, EventArgs.Empty);

        vm.Settings.Should().BeSameAs(draft);
        vm.Settings[0].IsSelected.Should().BeFalse();
        _mockSettingsLoadingService.Verify(s => s.LoadConfiguredSettingsAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ISettingsFeatureViewModel>()), Times.Once);
        _mockEventBus.Verify(e => e.Publish(It.IsAny<SettingsRefreshedEvent>()), Times.Once);
    }

    [Fact]
    public async Task RefreshSettingStatesAsync_WhenSettingsNotLoaded_DoesNothing()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act - settings have never been loaded, so this should be a no-op
        await vm.RefreshSettingStatesAsync();

        // Assert
        _mockSettingsLoadingService.Verify(
            s => s.RefreshSettingStatesAsync(It.IsAny<IEnumerable<SettingItemViewModel>>()),
            Times.Never);
    }

    [Fact]
    public async Task RefreshSettingStatesAsync_WhenSettingsLoaded_RefreshesStates()
    {
        // Arrange
        var vm = CreateViewModel();
        var settings = CreateSettingsCollection(("s1", "Setting 1", "Group"));

        _mockSettingsLoadingService
            .Setup(s => s.LoadConfiguredSettingsAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<ISettingsFeatureViewModel>()))
            .ReturnsAsync(settings);

        _mockSettingsLoadingService
            .Setup(s => s.RefreshSettingStatesAsync(It.IsAny<IEnumerable<SettingItemViewModel>>()))
            .ReturnsAsync(new Dictionary<string, SettingStateResult>());

        // Load first
        await vm.LoadSettingsAsync();

        // Act
        await vm.RefreshSettingStatesAsync();

        // Assert
        _mockSettingsLoadingService.Verify(
            s => s.RefreshSettingStatesAsync(It.IsAny<IEnumerable<SettingItemViewModel>>()),
            Times.Once);
    }

    [Fact]
    public async Task RefreshSettingStatesAsync_InBuilderMode_DoesNotReadSystemState()
    {
        // Arrange - Builder mode authors un-applied state into the VMs; a navigation
        // refresh must not clobber it with live system values.
        var vm = CreateViewModel();
        var settings = CreateSettingsCollection(("s1", "Setting 1", "Group"));

        _mockSettingsLoadingService
            .Setup(s => s.LoadConfiguredSettingsAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<ISettingsFeatureViewModel>()))
            .ReturnsAsync(settings);

        await vm.LoadSettingsAsync();

        _mockApplicationModeService
            .Setup(m => m.CurrentMode)
            .Returns(WinOptimizerMode.Builder);

        // Act
        await vm.RefreshSettingStatesAsync();

        // Assert
        _mockSettingsLoadingService.Verify(
            s => s.RefreshSettingStatesAsync(It.IsAny<IEnumerable<SettingItemViewModel>>()),
            Times.Never);
    }

    [Fact]
    public async Task RefreshSettingStatesAsync_UpdatesSettingStatesFromResults()
    {
        // Arrange
        var vm = CreateViewModel();
        var settings = CreateSettingsCollection(("s1", "Setting 1", "Group"));

        _mockSettingsLoadingService
            .Setup(s => s.LoadConfiguredSettingsAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<ISettingsFeatureViewModel>()))
            .ReturnsAsync(settings);

        var stateResults = new Dictionary<string, SettingStateResult>
        {
            ["s1"] = new SettingStateResult { Success = true, IsEnabled = true }
        };

        _mockSettingsLoadingService
            .Setup(s => s.RefreshSettingStatesAsync(It.IsAny<IEnumerable<SettingItemViewModel>>()))
            .ReturnsAsync(stateResults);

        await vm.LoadSettingsAsync();

        // Act
        await vm.RefreshSettingStatesAsync();

        // Assert - the setting should have been updated via UpdateStateFromSystemState
        // The actual state update is done via the dispatcher mock, which runs synchronously
        _mockDispatcherService.Verify(
            d => d.RunOnUIThread(It.IsAny<Action>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task RefreshSettingStatesAsync_PublishesTooltipUpdatedEvents()
    {
        // Arrange
        var vm = CreateViewModel();
        var settings = CreateSettingsCollection(("s1", "Setting 1", "Group"));

        _mockSettingsLoadingService
            .Setup(s => s.LoadConfiguredSettingsAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<ISettingsFeatureViewModel>()))
            .ReturnsAsync(settings);

        var tooltipData = new SettingTooltipData { SettingId = "s1", DisplayValue = "test" };
        var stateResults = new Dictionary<string, SettingStateResult>
        {
            ["s1"] = new SettingStateResult
            {
                Success = true,
                IsEnabled = true,
                TooltipData = tooltipData
            }
        };

        _mockSettingsLoadingService
            .Setup(s => s.RefreshSettingStatesAsync(It.IsAny<IEnumerable<SettingItemViewModel>>()))
            .ReturnsAsync(stateResults);

        await vm.LoadSettingsAsync();
        _mockEventBus.Invocations.Clear();

        // Act
        await vm.RefreshSettingStatesAsync();

        // Assert - TooltipUpdatedEvent should be published directly from state results
        _mockEventBus.Verify(
            e => e.Publish(It.Is<TooltipUpdatedEvent>(evt => evt.SettingId == "s1")),
            Times.Once);
    }

    [Fact]
    public async Task SearchMatchingTheWholeFeature_PreservesTheFixedReviewQueue()
    {
        _mockApplicationModeService.Setup(m => m.CurrentMode).Returns(WinOptimizerMode.ConfigReview);
        _reviewDiff.Setup(d => d.GetDiffForSetting("queued"))
            .Returns(new ConfigReviewDiff { SettingId = "queued" });
        using var queued = CreateSettingItem("queued", "Queued setting");
        using var unrelated = CreateSettingItem("unrelated", "Other setting");
        var vm = CreateViewModel();
        vm.Settings.Add(queued);
        vm.Settings.Add(unrelated);
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockDispatcherService.Setup(d => d.RunOnUIThread(It.IsAny<Action>()))
            .Callback<Action>(action => { action(); applied.TrySetResult(); });

        vm.ApplySearchFilter(vm.DisplayName);
        await applied.Task.WaitAsync(TimeSpan.FromSeconds(5));

        queued.IsVisible.Should().BeTrue();
        unrelated.IsVisible.Should().BeFalse();

        _reviewDiff.Setup(d => d.GetDiffForSetting("queued")).Returns((ConfigReviewDiff?)null);
        applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.ApplySearchFilter(vm.DisplayName);
        await applied.Task.WaitAsync(TimeSpan.FromSeconds(5));
        queued.IsVisible.Should().BeFalse();

        _mockApplicationModeService.Setup(m => m.CurrentMode).Returns(WinOptimizerMode.Normal);
        applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.ApplySearchFilter(vm.DisplayName);
        await applied.Task.WaitAsync(TimeSpan.FromSeconds(5));
        queued.IsVisible.Should().BeTrue();
        unrelated.IsVisible.Should().BeTrue();
    }

    [Theory]
    [InlineData("test", "test")]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void ApplySearchFilter_UpdatesOrClearsSearchText(string? search, string expected)
    {
        using var vm = CreateViewModel();
        vm.ApplySearchFilter("initial");

        vm.ApplySearchFilter(search!);

        vm.SearchText.Should().Be(expected);
    }

    [Fact]
    public async Task LoadSettingsAsync_GroupsSettingsByGroupName()
    {
        // Arrange
        var vm = CreateViewModel();
        var settings = CreateSettingsCollection(
            ("s1", "Setting 1", "Alpha"),
            ("s2", "Setting 2", "Alpha"),
            ("s3", "Setting 3", "Beta"));

        _mockSettingsLoadingService
            .Setup(s => s.LoadConfiguredSettingsAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<ISettingsFeatureViewModel>()))
            .ReturnsAsync(settings);

        // Act
        await vm.LoadSettingsAsync();

        // Assert
        vm.GroupedSettings.Should().HaveCount(2);
        vm.GroupedSettings[0].Key.Should().Be("Alpha");
        vm.GroupedSettings[0].Should().HaveCount(2);
        vm.GroupedSettings[1].Key.Should().Be("Beta");
        vm.GroupedSettings[1].Should().HaveCount(1);
    }

    [Fact]
    public async Task LoadSettingsAsync_SettingsWithEmptyGroupName_FallsBackToOtherGroup()
    {
        // Arrange
        var vm = CreateViewModel();

        // Mock the "Other" localization to return a bracketed value, triggering the fallback
        _mockLocalizationService
            .Setup(l => l.GetString("SettingGroup_Other"))
            .Returns("[SettingGroup_Other]");

        var settings = CreateSettingsCollection(
            ("s1", "Setting 1", ""));

        _mockSettingsLoadingService
            .Setup(s => s.LoadConfiguredSettingsAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<ISettingsFeatureViewModel>()))
            .ReturnsAsync(settings);

        // Act
        await vm.LoadSettingsAsync();

        // Assert - should fall back to "Other" when localization returns bracketed key
        vm.GroupedSettings.Should().HaveCount(1);
        vm.GroupedSettings[0].Key.Should().Be("Other");
    }

    [Fact]
    public async Task Dispose_UnsubscribesFromEvents()
    {
        // Arrange
        var mockSettingToken = new Mock<ISubscriptionToken>();
        var mockFilterToken = new Mock<ISubscriptionToken>();
        var mockReviewToken = new Mock<ISubscriptionToken>();

        _mockEventBus
            .Setup(e => e.Subscribe(It.IsAny<Action<SettingAppliedEvent>>()))
            .Returns(mockSettingToken.Object);
        _mockEventBus
            .Setup(e => e.SubscribeAsync(It.IsAny<Func<FilterStateChangedEvent, Task>>()))
            .Returns(mockFilterToken.Object);
        _mockEventBus
            .Setup(e => e.Subscribe(It.IsAny<Action<ReviewModeExitedEvent>>()))
            .Returns(mockReviewToken.Object);

        var vm = CreateViewModel();

        _mockSettingsLoadingService
            .Setup(s => s.LoadConfiguredSettingsAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<ISettingsFeatureViewModel>()))
            .ReturnsAsync(new ObservableCollection<SettingItemViewModel>());

        // Trigger event subscriptions by loading
        await vm.LoadSettingsAsync();

        // Act
        vm.Dispose();

        // Assert - subscription tokens should be disposed
        mockSettingToken.Verify(t => t.Dispose(), Times.Once);
        mockFilterToken.Verify(t => t.Dispose(), Times.Once);
        mockReviewToken.Verify(t => t.Dispose(), Times.Once);
    }

    [Fact]
    public async Task Dispose_ClearsSettingsCollection()
    {
        // Arrange
        var vm = CreateViewModel();
        var settings = CreateSettingsCollection(("s1", "Setting 1", "Group"));

        _mockSettingsLoadingService
            .Setup(s => s.LoadConfiguredSettingsAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<ISettingsFeatureViewModel>()))
            .ReturnsAsync(settings);

        await vm.LoadSettingsAsync();
        vm.Settings.Should().NotBeEmpty();

        // Act
        vm.Dispose();

        // Assert
        vm.Settings.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadSettingsAsync_WithMixedInputTypes_AllSettingsPopulatedCorrectly()
    {
        var vm = CreateViewModel();
        var settings = new ObservableCollection<SettingItemViewModel>
        {
            CreateSettingItem("toggle1", "Toggle", inputType: InputType.Toggle, isSelected: true),
            CreateSettingItem("sel1", "Selection", inputType: InputType.Selection, selectedValue: 2),
            CreateSettingItem("num1", "Numeric", inputType: InputType.NumericRange, numericValue: 45,
                numericRange: new NumericRangeMetadata { MinValue = 0, MaxValue = 100 }),
            CreateSettingItem("action1", "Action", inputType: InputType.Action)
        };

        _mockSettingsLoadingService
            .Setup(s => s.LoadConfiguredSettingsAsync(
                It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<ISettingsFeatureViewModel>()))
            .ReturnsAsync(settings);

        await vm.LoadSettingsAsync();

        vm.Settings.Should().HaveCount(4);
        vm.Settings[0].IsSelected.Should().BeTrue();
        vm.Settings[1].SelectedValue.Should().Be(2);
        vm.Settings[2].NumericValue.Should().Be(45);
        vm.Settings[3].InputType.Should().Be(InputType.Action);
        vm.HasVisibleSettings.Should().BeTrue();
    }

    [Fact]
    public async Task RefreshSettingStatesAsync_FailedResult_DoesNotChangeExistingValues()
    {
        var vm = CreateViewModel();
        var settings = new ObservableCollection<SettingItemViewModel>
        {
            CreateSettingItem("t1", "Toggle", isSelected: true)
        };

        _mockSettingsLoadingService
            .Setup(s => s.LoadConfiguredSettingsAsync(
                It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<ISettingsFeatureViewModel>()))
            .ReturnsAsync(settings);

        var refreshStates = new Dictionary<string, SettingStateResult>
        {
            ["t1"] = new SettingStateResult { Success = false, IsEnabled = false }
        };
        _mockSettingsLoadingService
            .Setup(s => s.RefreshSettingStatesAsync(It.IsAny<IEnumerable<SettingItemViewModel>>()))
            .ReturnsAsync(refreshStates);

        await vm.LoadSettingsAsync();

        await vm.RefreshSettingStatesAsync();

        vm.Settings[0].IsSelected.Should().BeTrue(); // unchanged due to failed result
    }

    [Fact]
    public async Task RefreshSettingStatesAsync_MissingSettingInResults_DoesNotThrow()
    {
        var vm = CreateViewModel();
        var settings = new ObservableCollection<SettingItemViewModel>
        {
            CreateSettingItem("t1", "Toggle", isSelected: true),
            CreateSettingItem("t2", "Toggle 2", isSelected: false)
        };

        _mockSettingsLoadingService
            .Setup(s => s.LoadConfiguredSettingsAsync(
                It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<ISettingsFeatureViewModel>()))
            .ReturnsAsync(settings);

        // Only return state for t1, not t2
        var refreshStates = new Dictionary<string, SettingStateResult>
        {
            ["t1"] = new SettingStateResult { Success = true, IsEnabled = false }
        };
        _mockSettingsLoadingService
            .Setup(s => s.RefreshSettingStatesAsync(It.IsAny<IEnumerable<SettingItemViewModel>>()))
            .ReturnsAsync(refreshStates);

        await vm.LoadSettingsAsync();

        var action = () => vm.RefreshSettingStatesAsync();

        await action.Should().NotThrowAsync();
        vm.Settings[0].IsSelected.Should().BeFalse(); // updated
        vm.Settings[1].IsSelected.Should().BeFalse(); // unchanged (no state returned)
    }

    [Fact]
    public async Task FullLifecycle_LoadThenRefreshWithSameValues_PreservesAllTypes()
    {
        var vm = CreateViewModel();
        var numericRange = new NumericRangeMetadata { MinValue = 0, MaxValue = 60 };
        var settings = new ObservableCollection<SettingItemViewModel>
        {
            CreateSettingItem("t1", "Toggle", isSelected: true),
            CreateSettingItem("sel1", "Selection", inputType: InputType.Selection, selectedValue: 1),
            CreateSettingItem("num1", "Numeric", inputType: InputType.NumericRange,
                numericValue: 42, numericRange: numericRange)
        };

        _mockSettingsLoadingService
            .Setup(s => s.LoadConfiguredSettingsAsync(
                It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<ISettingsFeatureViewModel>()))
            .ReturnsAsync(settings);

        // Refresh returns the same values
        var refreshStates = new Dictionary<string, SettingStateResult>
        {
            ["t1"] = new SettingStateResult { Success = true, IsEnabled = true },
            ["sel1"] = new SettingStateResult { Success = true, CurrentValue = 1 },
            ["num1"] = new SettingStateResult { Success = true, CurrentValue = 42 }
        };
        _mockSettingsLoadingService
            .Setup(s => s.RefreshSettingStatesAsync(It.IsAny<IEnumerable<SettingItemViewModel>>()))
            .ReturnsAsync(refreshStates);

        await vm.LoadSettingsAsync();
        await vm.RefreshSettingStatesAsync();

        vm.Settings[0].IsSelected.Should().BeTrue();
        vm.Settings[1].SelectedValue.Should().Be(1);
        vm.Settings[2].NumericValue.Should().Be(42);
    }

    [Fact]
    public async Task FullLifecycle_LoadThenRefreshWithChangedValues_UpdatesAllTypes()
    {
        var vm = CreateViewModel();
        var numericRange = new NumericRangeMetadata { MinValue = 0, MaxValue = 100 };
        var settings = new ObservableCollection<SettingItemViewModel>
        {
            CreateSettingItem("t1", "Toggle", isSelected: false),
            CreateSettingItem("sel1", "Selection", inputType: InputType.Selection, selectedValue: 0),
            CreateSettingItem("num1", "Numeric", inputType: InputType.NumericRange,
                numericValue: 10, numericRange: numericRange)
        };

        _mockSettingsLoadingService
            .Setup(s => s.LoadConfiguredSettingsAsync(
                It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<ISettingsFeatureViewModel>()))
            .ReturnsAsync(settings);

        // Refresh returns different values
        var refreshStates = new Dictionary<string, SettingStateResult>
        {
            ["t1"] = new SettingStateResult { Success = true, IsEnabled = true },
            ["sel1"] = new SettingStateResult { Success = true, CurrentValue = 3 },
            ["num1"] = new SettingStateResult { Success = true, CurrentValue = 75 }
        };
        _mockSettingsLoadingService
            .Setup(s => s.RefreshSettingStatesAsync(It.IsAny<IEnumerable<SettingItemViewModel>>()))
            .ReturnsAsync(refreshStates);

        await vm.LoadSettingsAsync();
        await vm.RefreshSettingStatesAsync();

        vm.Settings[0].IsSelected.Should().BeTrue();
        vm.Settings[1].SelectedValue.Should().Be(3);
        vm.Settings[2].NumericValue.Should().Be(75);
    }

    [Fact]
    public async Task RefreshSettingsAsync_DisposesOldSettings_LoadsNewOnes()
    {
        var vm = CreateViewModel();
        var firstSettings = new ObservableCollection<SettingItemViewModel>
        {
            CreateSettingItem("old1", "Old Setting 1"),
            CreateSettingItem("old2", "Old Setting 2")
        };
        var secondSettings = new ObservableCollection<SettingItemViewModel>
        {
            CreateSettingItem("new1", "New Setting 1"),
            CreateSettingItem("new2", "New Setting 2"),
            CreateSettingItem("new3", "New Setting 3")
        };
        var callCount = 0;

        _mockSettingsLoadingService
            .Setup(s => s.LoadConfiguredSettingsAsync(
                It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<ISettingsFeatureViewModel>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount == 1 ? firstSettings : secondSettings;
            });

        await vm.LoadSettingsAsync();
        vm.Settings.Should().HaveCount(2);
        vm.Settings[0].Name.Should().Be("Old Setting 1");

        await vm.RefreshSettingsAsync();

        vm.Settings.Should().HaveCount(3);
        vm.Settings[0].Name.Should().Be("New Setting 1");
    }

    [Fact]
    public async Task LoadSettingsAsync_GroupedSettingsPreservesInsertionOrder()
    {
        // Arrange
        var vm = CreateViewModel();
        var settings = CreateSettingsCollection(
            ("s1", "S1", "Zebra"),
            ("s2", "S2", "Alpha"),
            ("s3", "S3", "Middle"));

        _mockSettingsLoadingService
            .Setup(s => s.LoadConfiguredSettingsAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<ISettingsFeatureViewModel>()))
            .ReturnsAsync(settings);

        // Act
        await vm.LoadSettingsAsync();

        // Assert - groups should appear in the order settings were encountered, not alphabetically
        vm.GroupedSettings[0].Key.Should().Be("Zebra");
        vm.GroupedSettings[1].Key.Should().Be("Alpha");
        vm.GroupedSettings[2].Key.Should().Be("Middle");
    }
}
