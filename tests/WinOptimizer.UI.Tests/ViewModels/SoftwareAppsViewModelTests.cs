using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.SoftwareApps.Models;
using WinOptimizer.UI.Features.SoftwareApps.ViewModels;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

public class SoftwareAppsViewModelTests
{
    private readonly Mock<IWindowsAppsService> _windowsAppsService = new();
    private readonly Mock<IAppUninstallWorkflow> _uninstallWorkflow = new();
    private readonly Mock<ILogService> _logService = new();
    private readonly Mock<IDialogService> _dialogService = new();
    private readonly Mock<ILocalizationService> _localizationService = new();
    private readonly Mock<IDispatcherService> _dispatcherService = new();
    private readonly Mock<IThemeService> _themeService = new();

    private readonly Mock<IExternalAppsService> _externalAppsService = new();
    private readonly Mock<ILogService> _extLogService = new();
    private readonly Mock<IDialogService> _extDialogService = new();
    private readonly Mock<ILocalizationService> _extLocalizationService = new();
    private readonly Mock<IDispatcherService> _extDispatcherService = new();

    private readonly Mock<ILocalizationService> _parentLocalizationService = new();
    private readonly Mock<ILogService> _parentLogService = new();
    private readonly Mock<IUserPreferencesService> _userPreferencesService = new();
    private readonly Mock<IConfigReviewModeService> _configReviewModeService = new();
    private readonly Mock<IConfigReviewBadgeService> _configReviewBadgeService = new();
    private readonly Mock<IApplicationModeService> _applicationModeService = new();

    public SoftwareAppsViewModelTests()
    {
        // Set up dispatcher mocks to execute actions synchronously
        _dispatcherService.Setup(d => d.RunOnUIThread(It.IsAny<Action>()))
            .Callback<Action>(a => a());
        _dispatcherService.Setup(d => d.RunOnUIThreadAsync(It.IsAny<Func<Task>>()))
            .Callback<Func<Task>>(f => f().GetAwaiter().GetResult())
            .Returns(Task.CompletedTask);
        _dispatcherService.Setup(d => d.RunOnUIThreadWithContextAsync(It.IsAny<Func<Task>>()))
            .Callback<Func<Task>>(f => f().GetAwaiter().GetResult())
            .Returns(Task.CompletedTask);

        _extDispatcherService.Setup(d => d.RunOnUIThread(It.IsAny<Action>()))
            .Callback<Action>(a => a());
        _extDispatcherService.Setup(d => d.RunOnUIThreadAsync(It.IsAny<Func<Task>>()))
            .Callback<Func<Task>>(f => f().GetAwaiter().GetResult())
            .Returns(Task.CompletedTask);
        _extDispatcherService.Setup(d => d.RunOnUIThreadWithContextAsync(It.IsAny<Func<Task>>()))
            .Callback<Func<Task>>(f => f().GetAwaiter().GetResult())
            .Returns(Task.CompletedTask);

        _parentLocalizationService.Setup(l => l.GetString(It.IsAny<string>()))
            .Returns<string>(k => k);
        _localizationService.Setup(l => l.GetString(It.IsAny<string>()))
            .Returns<string>(k => k);
        _extLocalizationService.Setup(l => l.GetString(It.IsAny<string>()))
            .Returns<string>(k => k);

        _userPreferencesService.Setup(u => u.GetPreference(It.IsAny<string>(), It.IsAny<string>()))
            .Returns("Table");
        _userPreferencesService.Setup(u => u.SetPreferenceAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(OperationResult.Succeeded());
    }

    private WindowsAppsViewModel CreateWindowsAppsVm() => new(
        _windowsAppsService.Object,
        _uninstallWorkflow.Object,
        _logService.Object,
        _dialogService.Object,
        _localizationService.Object,
        _dispatcherService.Object,
        _themeService.Object,
        iconResolver: Mock.Of<IAppIconResolver>());

    private ExternalAppsViewModel CreateExternalAppsVm() => new(
        _externalAppsService.Object,
        _uninstallWorkflow.Object,
        _extLogService.Object,
        _extDialogService.Object,
        _extLocalizationService.Object,
        _extDispatcherService.Object,
        _themeService.Object,
        iconResolver: Mock.Of<IAppIconResolver>());

    private SoftwareAppsViewModel CreateSut()
    {
        var winVm = CreateWindowsAppsVm();
        var extVm = CreateExternalAppsVm();
        return new SoftwareAppsViewModel(
            winVm,
            extVm,
            _parentLocalizationService.Object,
            _parentLogService.Object,
            _userPreferencesService.Object,
            _configReviewModeService.Object,
            _configReviewBadgeService.Object,
            _applicationModeService.Object,
            Mock.Of<WinOptimizer.Core.Features.SoftwareApps.Interfaces.IUninstallResidueReview>(), CreateLeftoverVm());
    }

    private LeftoverAppsViewModel CreateLeftoverVm() => new(Mock.Of<ILeftoverService>(), _parentLocalizationService.Object,
        _dialogService.Object, _applicationModeService.Object, Mock.Of<ITaskProgressService>(), _dispatcherService.Object, _logService.Object);

    [Fact]
    public void CurrentRemoveAction_WhenWindowsTabSelected_RoutesToWindowsRemoveAction()
    {
        var sut = CreateSut();
        sut.IsWindowsAppsTabSelected = true;

        sut.CurrentRemoveAction = true;

        sut.IsWindowsAppsRemoveAction.Should().BeTrue();
    }

    [Fact]
    public void CurrentRemoveAction_WhenExternalTabSelected_RoutesToExternalRemoveAction()
    {
        var sut = CreateSut();
        sut.IsExternalAppsTabSelected = true;

        sut.CurrentRemoveAction = true;

        sut.IsExternalAppsRemoveAction.Should().BeTrue();
    }

    [Fact]
    public void IsSoftwareAppsReviewed_WhenNotInReviewMode_ReturnsFalse()
    {
        var sut = CreateSut();
        sut.IsInReviewMode = false;

        sut.IsSoftwareAppsReviewed.Should().BeFalse();
    }

    [Fact]
    public void IsSoftwareAppsReviewed_WhenInReviewMode_NoConfigItems_ReturnsTrue()
    {
        var sut = CreateSut();
        sut.IsInReviewMode = true;

        _configReviewBadgeService.Setup(s => s.IsFeatureInConfig(It.IsAny<string>()))
            .Returns(false);

        sut.IsSoftwareAppsReviewed.Should().BeTrue();
    }

    [Fact]
    public void IsSoftwareAppsReviewed_WhenInReviewMode_WindowsAppsInConfigNoAction_ReturnsFalse()
    {
        _configReviewBadgeService.Setup(s => s.IsFeatureInConfig("WindowsApps")).Returns(true);
        _configReviewBadgeService.Setup(s => s.IsFeatureInConfig("ExternalApps")).Returns(false);

        var sut = CreateSut();
        sut.IsInReviewMode = true;
        sut.WindowsAppsSelectedCount = 2;

        sut.IsSoftwareAppsReviewed.Should().BeFalse();
    }

    [Fact]
    public void IsSoftwareAppsReviewed_WhenInReviewMode_AllActionsChosen_ReturnsTrue()
    {
        _configReviewBadgeService.Setup(s => s.IsFeatureInConfig("WindowsApps")).Returns(true);
        _configReviewBadgeService.Setup(s => s.IsFeatureInConfig("ExternalApps")).Returns(true);

        var sut = CreateSut();
        sut.IsInReviewMode = true;
        sut.WindowsAppsSelectedCount = 2;
        sut.ExternalAppsSelectedCount = 1;
        sut.IsWindowsAppsRemoveAction = true;
        sut.IsExternalAppsRemoveAction = true;

        sut.IsSoftwareAppsReviewed.Should().BeTrue();
    }

    [Fact]
    public void SelectedWindowsApps_EnableRemovalOnly()
    {
        var sut = CreateSut();
        sut.WindowsAppsViewModel.Items.Add(new AppItemViewModel(
            new ItemDefinition { Id = "app1", Name = "App 1", Description = "", IsInstalled = true },
            _localizationService.Object, _dispatcherService.Object, _themeService.Object)
        {
            IsSelected = true
        });
        sut.SelectExternalAppsTabCommand.Execute(null);

        sut.SelectWindowsAppsTabCommand.Execute(null);

        sut.RemoveSelectedItemsCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void SelectedExternalApps_EnableRemovalOnly()
    {
        var sut = CreateSut();
        sut.ExternalAppsViewModel.Items.Add(new AppItemViewModel(
            new ItemDefinition { Id = "ext1", Name = "External App", Description = "" },
            _extLocalizationService.Object, _extDispatcherService.Object, _themeService.Object)
        {
            IsSelected = true
        });

        sut.SelectExternalAppsTabCommand.Execute(null);

        sut.RemoveSelectedItemsCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void CanRemoveItems_InReviewMode_IsFalse()
    {
        var sut = CreateSut();
        sut.IsInReviewMode = true;

        sut.IsWindowsAppsTabSelected = true;

        sut.CanRemoveItems.Should().BeFalse();
    }

    /// <summary>Restores the first frame's layout without starting discovery or repeating preference reads.</summary>
    [Theory]
    [InlineData("Card", SoftwareAppsViewMode.Card)]
    [InlineData("Table", SoftwareAppsViewMode.Table)]
    [InlineData("Compact", SoftwareAppsViewMode.Compact)]
    public void InitializePresentationRestoresLayoutWithoutLoadingInventory(string saved, SoftwareAppsViewMode expected)
    {
        _userPreferencesService.Setup(service => service.GetPreference("SoftwareAppsViewMode", "Card")).Returns(saved);
        _userPreferencesService.Setup(service => service.GetPreference("SoftwareAppsSortMode", "GroupAsc")).Returns("NameAsc");
        using var sut = CreateSut();

        sut.InitializePresentation();
        sut.InitializePresentation();

        sut.ViewMode.Should().Be(expected);
        sut.SortMode.Should().Be(AppSortMode.NameAsc);
        sut.WindowsAppsViewModel.SortMode.Should().Be(AppSortMode.NameAsc);
        sut.ExternalAppsViewModel.SortMode.Should().Be(AppSortMode.NameAsc);
        _userPreferencesService.Verify(service => service.GetPreference("SoftwareAppsViewMode", "Card"), Times.Once);
        _userPreferencesService.Verify(service => service.GetPreference("SoftwareAppsSortMode", "GroupAsc"), Times.Once);
        _windowsAppsService.Verify(service => service.GetAppsAsync(), Times.Never);
        _externalAppsService.Verify(service => service.GetAppsAsync(), Times.Never);
    }

    [Fact]
    public async Task InitializeAsync_SubscribesOnlyOnce()
    {
        _userPreferencesService.Setup(u => u.GetPreference("SoftwareAppsViewMode", "Card"))
            .Returns("Table");
        _windowsAppsService.Setup(s => s.GetAppsAsync())
            .ReturnsAsync(Enumerable.Empty<ItemDefinition>());
        _externalAppsService.Setup(s => s.GetAppsAsync())
            .ReturnsAsync(Enumerable.Empty<ItemDefinition>());
        _windowsAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(new Dictionary<string, bool>());
        _externalAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(new Dictionary<string, bool>());

        var sut = CreateSut();

        await sut.InitializeAsync();
        await sut.InitializeAsync(); // second call should not re-subscribe

        _userPreferencesService.Verify(u => u.GetPreference("SoftwareAppsViewMode", "Card"), Times.Once);
    }

    [Fact]
    public void ActionChoice_SyncsSoftwareAppsReviewedState()
    {
        _configReviewBadgeService.Setup(s => s.IsFeatureInConfig(It.IsAny<string>())).Returns(false);

        var sut = CreateSut();
        sut.IsInReviewMode = true;
        sut.IsWindowsAppsRemoveAction = true;

        _configReviewBadgeService.VerifySet(s => s.IsSoftwareAppsReviewed = It.IsAny<bool>(), Times.AtLeastOnce);
        _configReviewBadgeService.Verify(s => s.NotifyBadgeStateChanged(), Times.AtLeastOnce);
    }

    [Fact]
    public async Task DefaultViewMode_IsCard_WhenNoPreferenceSaved()
    {
        _userPreferencesService.Setup(u => u.GetPreference("SoftwareAppsViewMode", "Card"))
            .Returns("Card");
        _windowsAppsService.Setup(s => s.GetAppsAsync())
            .ReturnsAsync(Enumerable.Empty<ItemDefinition>());
        _externalAppsService.Setup(s => s.GetAppsAsync())
            .ReturnsAsync(Enumerable.Empty<ItemDefinition>());
        _windowsAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(new Dictionary<string, bool>());
        _externalAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(new Dictionary<string, bool>());

        var sut = CreateSut();
        await sut.InitializeAsync();

        sut.ViewMode.Should().Be(SoftwareAppsViewMode.Card);
        sut.IsCardView.Should().BeTrue();
        sut.IsTableView.Should().BeFalse();
        sut.IsCompactView.Should().BeFalse();
    }

    [Fact]
    public async Task ViewMode_LoadsTable_WhenPreferenceIsTable()
    {
        _userPreferencesService.Setup(u => u.GetPreference("SoftwareAppsViewMode", "Card"))
            .Returns("Table");
        _windowsAppsService.Setup(s => s.GetAppsAsync())
            .ReturnsAsync(Enumerable.Empty<ItemDefinition>());
        _externalAppsService.Setup(s => s.GetAppsAsync())
            .ReturnsAsync(Enumerable.Empty<ItemDefinition>());
        _windowsAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(new Dictionary<string, bool>());
        _externalAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(new Dictionary<string, bool>());

        var sut = CreateSut();
        await sut.InitializeAsync();

        sut.ViewMode.Should().Be(SoftwareAppsViewMode.Table);
        sut.IsTableView.Should().BeTrue();
        sut.IsCardView.Should().BeFalse();
        sut.IsCompactView.Should().BeFalse();
    }

    [Fact]
    public async Task ViewMode_LoadsCompact_WhenPreferenceIsCompact()
    {
        _userPreferencesService.Setup(u => u.GetPreference("SoftwareAppsViewMode", "Card"))
            .Returns("Compact");
        _windowsAppsService.Setup(s => s.GetAppsAsync())
            .ReturnsAsync(Enumerable.Empty<ItemDefinition>());
        _externalAppsService.Setup(s => s.GetAppsAsync())
            .ReturnsAsync(Enumerable.Empty<ItemDefinition>());
        _windowsAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(new Dictionary<string, bool>());
        _externalAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(new Dictionary<string, bool>());

        var sut = CreateSut();
        await sut.InitializeAsync();

        sut.ViewMode.Should().Be(SoftwareAppsViewMode.Compact);
        sut.IsCompactView.Should().BeTrue();
    }

    [Fact]
    public async Task ViewMode_FallsBackToCard_WhenPreferenceIsUnknown()
    {
        _userPreferencesService.Setup(u => u.GetPreference("SoftwareAppsViewMode", "Card"))
            .Returns("Garbage");
        _windowsAppsService.Setup(s => s.GetAppsAsync())
            .ReturnsAsync(Enumerable.Empty<ItemDefinition>());
        _externalAppsService.Setup(s => s.GetAppsAsync())
            .ReturnsAsync(Enumerable.Empty<ItemDefinition>());
        _windowsAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(new Dictionary<string, bool>());
        _externalAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(new Dictionary<string, bool>());

        var sut = CreateSut();
        await sut.InitializeAsync();

        sut.ViewMode.Should().Be(SoftwareAppsViewMode.Card);
    }

    [Fact]
    public void SettingViewMode_PersistsPreference()
    {
        var sut = CreateSut();

        sut.ViewMode = SoftwareAppsViewMode.Compact;

        _userPreferencesService.Verify(
            u => u.SetPreferenceAsync("SoftwareAppsViewMode", "Compact"),
            Times.Once);
    }
}
