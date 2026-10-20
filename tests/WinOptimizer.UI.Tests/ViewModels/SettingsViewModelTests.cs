using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Settings.ViewModels;
using Xunit;
using WinOptimizer.Core.Features.Cleaner.Interfaces;
using WinOptimizer.Core.Features.Cleaner.Models;
using WinOptimizer.Core.Features.Drivers.Models;
using WinOptimizer.UI.Features.Cleaner.ViewModels;
using WinOptimizer.UI.Features.Drivers.ViewModels;
using WinOptimizer.UI.Features.Common.ViewModels;
using WinOptimizer.UI.ViewModels;
using WinOptimizer.Core.Features.Common.Constants;

namespace WinOptimizer.UI.Tests.ViewModels;

public class SettingsViewModelTests
{
    private readonly Mock<ILocalizationService> _mockLocalization = new();
    private readonly Mock<IThemeService> _mockThemeService = new();
    private readonly Mock<IUserPreferencesService> _mockPreferencesService = new();
    private readonly Mock<ILogService> _mockLogService = new();
    private readonly Mock<IChangeHistoryService> _mockChangeHistory = new();
    private readonly Mock<IProcessExecutor> _mockProcessExecutor = new();

    public SettingsViewModelTests()
    {
        _mockLogService.Setup(service => service.GetFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<LogFileInfo>());
        _mockChangeHistory.Setup(service => service.GetFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<LogFileInfo>());
        _mockLocalization
            .Setup(l => l.GetString(It.IsAny<string>()))
            .Returns((string key) => key);
        _mockLocalization
            .Setup(l => l.CurrentLanguage)
            .Returns("en");
        _mockLocalization
            .Setup(l => l.GetAvailableLanguages())
            .Returns(new List<LanguageOption>
            {
                new("en", "English"),
                new("af", "Afrikaans"),
                new("fr", "Français (French)"),
            });
        _mockThemeService
            .Setup(t => t.CurrentTheme)
            .Returns(WinOptimizerTheme.System);
        _mockPreferencesService.Setup(p => p.GetPreferenceAsync("Cleaner", It.IsAny<CleanerOptions>())).ReturnsAsync(new CleanerOptions());
        _mockPreferencesService.Setup(p => p.GetPreferenceAsync("Drivers", It.IsAny<DriverScanOptions>())).ReturnsAsync(new DriverScanOptions());
        _mockPreferencesService.Setup(p => p.SetPreferenceAsync("Cleaner", It.IsAny<CleanerOptions>())).ReturnsAsync(OperationResult.Succeeded());
        _mockPreferencesService.Setup(p => p.SetPreferenceAsync("Drivers", It.IsAny<DriverScanOptions>())).ReturnsAsync(OperationResult.Succeeded());
        _mockPreferencesService.Setup(p => p.GetPreferenceAsync(UserPreferenceKeys.Updates, It.IsAny<UpdateOptions>())).ReturnsAsync(new UpdateOptions());
        _mockPreferencesService.Setup(p => p.SetPreferenceAsync(UserPreferenceKeys.Updates, It.IsAny<UpdateOptions>())).ReturnsAsync(OperationResult.Succeeded());
        _mockPreferencesService.Setup(p => p.GetPreferenceAsync(UserPreferenceKeys.SidebarExpanded, true)).ReturnsAsync(true);
        _mockPreferencesService.Setup(p => p.GetPreferenceAsync(UserPreferenceKeys.AppColor, "Blue")).ReturnsAsync("Blue");
        _mockPreferencesService.Setup(p => p.SetPreferenceAsync(UserPreferenceKeys.SidebarExpanded, It.IsAny<bool>())).ReturnsAsync(OperationResult.Succeeded());
        _mockPreferencesService.Setup(p => p.SetPreferenceAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(OperationResult.Succeeded());
    }

    internal SettingsViewModel CreateViewModel()
    {
        var backend = new Mock<ICleanerBackend>();
        backend.Setup(b => b.GetApplicationsAsync(It.IsAny<CleanerOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CleanerApplicationInventory([new("Example.App_publisher", "Example installed app")], []));
        backend.Setup(b => b.GetRegistryAccountsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { new CleanerRegistryAccount("S-1-5-21-1-2-3-1001", "Current account", true) });
        backend.Setup(b => b.GetDrivesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { "C:\\" });
        var cleaner = new CleanerSettingsViewModel(_mockPreferencesService.Object, backend.Object, _mockLocalization.Object,
            Mock.Of<ITaskProgressService>(), Mock.Of<IExplorerWindowManager>(), Mock.Of<ICleanerBackupService>(), Mock.Of<IDialogService>(), Mock.Of<IApplicationModeService>());
        var drivers = new DriverSettingsViewModel(_mockPreferencesService.Object, _mockLocalization.Object);
        return new SettingsViewModel(
            _mockLocalization.Object,
            _mockThemeService.Object,
            _mockPreferencesService.Object,
            _mockLogService.Object, cleaner, drivers,
            new LogsAndHistoryViewModel(Mock.Of<IActivityJournalService>(), _mockLogService.Object, _mockLocalization.Object, Mock.Of<IDialogService>()),
            new ApplicationInfoViewModel(_mockLocalization.Object, Mock.Of<IVersionService>(), _mockLogService.Object,
                Mock.Of<IDialogService>(), _mockProcessExecutor.Object,
                new UpdateCheckViewModel(Mock.Of<IVersionService>(), _mockLocalization.Object, _mockLogService.Object,
                    Mock.Of<IBackgroundUpdateCheckService>(), Mock.Of<IDispatcherService>())),
            new UpdatePreferencesViewModel(_mockPreferencesService.Object, _mockLocalization.Object),
            new SidebarPreferencesViewModel(_mockPreferencesService.Object, _mockLocalization.Object),
            new AppColorPreferencesViewModel(_mockPreferencesService.Object, _mockLocalization.Object,
                new WinOptimizer.UI.Features.Common.Resources.AppColorResources()));
    }

    [Fact]
    public void SelectedLanguage_SetToNewValue_CallsLocalizationSetLanguage()
    {
        _mockLocalization
            .Setup(l => l.CurrentLanguage)
            .Returns("en");
        _mockLocalization
            .Setup(l => l.SetLanguage("fr"))
            .Returns(true);

        var vm = CreateViewModel();

        vm.SelectedLanguage = "fr";

        _mockLocalization.Verify(l => l.SetLanguage("fr"), Times.Once);
    }

    [Fact]
    public void SelectedLanguage_WhenSetLanguageSucceeds_SavesPreference()
    {
        _mockLocalization
            .Setup(l => l.CurrentLanguage)
            .Returns("en");
        _mockLocalization
            .Setup(l => l.SetLanguage("de"))
            .Returns(true);
        _mockPreferencesService
            .Setup(p => p.SetPreferenceAsync("Language", "de"))
            .ReturnsAsync(OperationResult.Succeeded());

        var vm = CreateViewModel();

        vm.SelectedLanguage = "de";

        _mockPreferencesService.Verify(
            p => p.SetPreferenceAsync("Language", "de"),
            Times.Once);
    }

    [Fact]
    public void SelectedLanguage_WhenSetLanguageFails_DoesNotSavePreference()
    {
        _mockLocalization
            .Setup(l => l.CurrentLanguage)
            .Returns("en");
        _mockLocalization
            .Setup(l => l.SetLanguage("zz"))
            .Returns(false);

        var vm = CreateViewModel();

        vm.SelectedLanguage = "zz";

        _mockPreferencesService.Verify(
            p => p.SetPreferenceAsync(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public void SelectedTheme_SetToNewValue_CallsThemeServiceSetTheme()
    {
        _mockThemeService
            .Setup(t => t.CurrentTheme)
            .Returns(WinOptimizerTheme.System);

        var vm = CreateViewModel();

        vm.SelectedTheme = WinOptimizerTheme.DarkNative;

        _mockThemeService.Verify(t => t.SetTheme(WinOptimizerTheme.DarkNative), Times.Once);
    }

    [Fact]
    public async Task SearchMatchesIndividualSettingsAndInstalledApps_AndClearingRestoresGroups()
    {
        using var vm = CreateViewModel();
        await vm.InitializeAsync();
        vm.Query = "BrowserHistory";
        vm.VisibleGroups.SelectMany(group => group.Items).Should().ContainSingle().Which.Category.Should().Be(SettingsCategory.Privacy);
        vm.Query = "Example installed";
        var found = vm.VisibleGroups.SelectMany(group => group.Items).Should().ContainSingle().Which;
        found.Category.Should().Be(SettingsCategory.Junk);
        found.DisplayDescription.Should().Contain("Example installed app");
        vm.Query = "unknown setting 954821";
        vm.NoMatches.Should().BeTrue();
        vm.Query = "";
        vm.VisibleGroups.Should().HaveCount(4);
        vm.VisibleGroups.SelectMany(group => group.Items).Should().HaveCount(11);
    }

    [Fact]
    public void NarrowLayoutShowsCategoryDetails_AndClearingSelectionRestoresList()
    {
        using var vm = CreateViewModel();
        vm.IsWideLayout = false;
        vm.ShowCategories.Should().BeTrue();
        vm.SelectedCategory = vm.Categories.Single(item => item.Category == SettingsCategory.Registry);
        vm.ShowCategories.Should().BeFalse();
        vm.ShowDetails.Should().BeTrue();
        vm.SelectedCleanerSection.Should().Be(CleanerSettingsSection.Registry);
        vm.SelectedCategory = null;
        vm.ShowCategories.Should().BeTrue();
        vm.ShowDetails.Should().BeFalse();
    }

    /// <summary>Recent-document controls are discoverable in both of their owning settings categories.</summary>
    [Fact]
    public void RecentDocumentSearchFindsPrivacyAndShortcutSettings()
    {
        using var vm = CreateViewModel();
        vm.Query = "Cleaner_RecentFiles";
        vm.VisibleGroups.SelectMany(group => group.Items).Select(item => item.Category)
            .Should().BeEquivalentTo(new[] { SettingsCategory.Privacy, SettingsCategory.Shortcuts });
    }

    [Theory]
    [InlineData("Settings_LogsAndHistory")]
    [InlineData("Menu_WinOptimizerLogs")]
    [InlineData("Menu_ChangeHistory")]
    public async Task LogsAndHistoryIsSearchableAndDoesNotResetPreferences(string query)
    {
        using var vm = CreateViewModel();
        vm.Query = query;
        var category = vm.VisibleGroups.SelectMany(group => group.Items).Should().ContainSingle().Which;
        category.Category.Should().Be(SettingsCategory.LogsAndHistory);
        category.GroupKey.Should().Be("Settings_GroupApplication");
        vm.IsWideLayout = false;
        vm.SelectedCategory = category;
        vm.ShowLogsAndHistory.Should().BeTrue();
        vm.ShowDetails.Should().BeTrue();
        vm.ShowCategories.Should().BeFalse();
        vm.ShowCleaner.Should().BeFalse();
        vm.ShowCleanerAccounts.Should().BeFalse();
        vm.ShowReset.Should().BeFalse();
        vm.CanReset.Should().BeFalse();
        await vm.ResetCommand.ExecuteAsync(null);
        _mockPreferencesService.Verify(service => service.SetPreferenceAsync(It.IsAny<string>(), It.IsAny<CleanerOptions>()), Times.Never);
        _mockPreferencesService.Verify(service => service.SetPreferenceAsync(It.IsAny<string>(), It.IsAny<DriverScanOptions>()), Times.Never);
        _mockPreferencesService.Verify(service => service.SetPreferenceAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _mockThemeService.Verify(service => service.SetTheme(It.IsAny<WinOptimizerTheme>()), Times.Never);
        vm.SelectedCategory = null;
        vm.ShowLogsAndHistory.Should().BeFalse();
        vm.ShowCategories.Should().BeTrue();
        vm.SelectedCategory = vm.Categories.Single(item => item.Category == SettingsCategory.Appearance);
        vm.ShowReset.Should().BeTrue();
        vm.CanReset.Should().BeTrue();
    }

    [Fact]
    public async Task AccountSearchFindsAllProfileScopedCategories_AndFooterUsesTheirOwnSelections()
    {
        using var vm = CreateViewModel();
        await vm.InitializeAsync();
        vm.Query = "Current account";
        var matches = vm.VisibleGroups.SelectMany(group => group.Items).ToArray();
        matches.Select(category => category.Category).Should().BeEquivalentTo(new[] { SettingsCategory.Registry, SettingsCategory.Privacy, SettingsCategory.Junk, SettingsCategory.Shortcuts });
        foreach (var category in matches)
        {
            vm.SelectedCategory = category;
            vm.ShowCleanerAccounts.Should().BeTrue();
            vm.CleanerAccountChoices.Should().BeSameAs(vm.Cleaner.AccountChoices(vm.SelectedCleanerSection));
        }
        vm.SelectedCategory = vm.Categories.Single(category => category.Category == SettingsCategory.Disk);
        vm.ShowCleanerAccounts.Should().BeFalse();
        vm.SelectedCategory = vm.Categories.Single(category => category.Category == SettingsCategory.Backups);
        vm.ShowCleanerAccounts.Should().BeFalse();
    }

    [Fact]
    public async Task CategoryResetOnlyRestoresOpenedPreferences()
    {
        using var vm = CreateViewModel();
        await vm.InitializeAsync();
        vm.Cleaner.BrowserHistory = true;
        vm.Cleaner.MinimumAgeDays = 30;
        vm.Cleaner.DeepClean = true;
        vm.Drivers.OnlyWhqlUpdates = false;
        vm.SelectedCategory = vm.Categories.Single(item => item.Category == SettingsCategory.Privacy);
        await vm.ResetCommand.ExecuteAsync(null);
        vm.Cleaner.Options.BrowserHistory.Should().BeFalse();
        vm.Cleaner.Options.MinimumAgeDays.Should().Be(30);
        vm.Cleaner.Options.DeepClean.Should().BeTrue();
        vm.Drivers.Options.OnlyWhqlUpdates.Should().BeFalse();
        _mockThemeService.Verify(service => service.SetTheme(It.IsAny<WinOptimizerTheme>()), Times.Never);
        vm.Status.Should().BeEmpty();
    }

    [Fact]
    public async Task InfoResetOnlyRestoresUpdatePreferences()
    {
        using var vm = CreateViewModel();
        await vm.InitializeAsync();
        vm.Cleaner.MinimumAgeDays = 30;
        vm.Drivers.OnlyWhqlUpdates = false;
        vm.SidebarPreferences.IsExpanded = false;
        vm.UpdatePreferences.CheckApplicationUpdatesAutomatically = false;
        vm.AppColorPreferences.SelectedColor = WinOptimizer.UI.Features.Common.Resources.AppColor.Purple;
        vm.UpdatePreferences.UpdateRemovalScriptsAutomatically = false;
        vm.SelectedCategory = vm.Categories.Single(item => item.Category == SettingsCategory.Info);
        vm.ShowInfo.Should().BeTrue();
        vm.ShowReset.Should().BeTrue();
        await vm.ResetCommand.ExecuteAsync(null);
        vm.UpdatePreferences.Options.Should().BeEquivalentTo(new UpdateOptions());
        vm.Cleaner.Options.MinimumAgeDays.Should().Be(30);
        vm.Drivers.Options.OnlyWhqlUpdates.Should().BeFalse();
        vm.SidebarPreferences.IsExpanded.Should().BeFalse();
        vm.AppColorPreferences.SelectedColor.Should().Be(WinOptimizer.UI.Features.Common.Resources.AppColor.Purple);
        _mockThemeService.Verify(service => service.SetTheme(It.IsAny<WinOptimizerTheme>()), Times.Never);
    }

    [Fact]
    public async Task AppearanceResetRestoresSidebarWithoutResettingOtherCategories()
    {
        using var vm = CreateViewModel();
        await vm.InitializeAsync();
        vm.SidebarPreferences.IsExpanded = false;
        vm.Drivers.OnlyWhqlUpdates = false;
        vm.AppColorPreferences.SelectedColor = WinOptimizer.UI.Features.Common.Resources.AppColor.Green;
        vm.UpdatePreferences.CheckApplicationUpdatesAutomatically = false;
        vm.SelectedCategory = vm.Categories.Single(item => item.Category == SettingsCategory.Appearance);
        await vm.ResetCommand.ExecuteAsync(null);
        vm.SidebarPreferences.IsExpanded.Should().BeTrue();
        vm.AppColorPreferences.SelectedColor.Should().Be(WinOptimizer.UI.Features.Common.Resources.AppColor.Blue);
        vm.Drivers.OnlyWhqlUpdates.Should().BeFalse();
        vm.UpdatePreferences.CheckApplicationUpdatesAutomatically.Should().BeFalse();
        vm.Status.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Settings_ExpandNavigation")]
    [InlineData("Settings_ExpandNavigationDescription")]
    [InlineData("Settings_AppColor")]
    [InlineData("Settings_AppColorDescription")]
    [InlineData("Settings_AppColor_Purple")]
    public void SidebarSearchFindsAppearancePreferences(string query)
    {
        using var vm = CreateViewModel();
        vm.Query = query;
        vm.VisibleGroups.SelectMany(group => group.Items).Should().ContainSingle().Which.Category.Should().Be(SettingsCategory.Appearance);
    }

    [Fact]
    public async Task AppearanceResetReportsSidebarSaveFailureAndReenablesReset()
    {
        using var vm = CreateViewModel();
        await vm.InitializeAsync();
        _mockPreferencesService.Setup(service => service.SetPreferenceAsync(UserPreferenceKeys.SidebarExpanded, It.IsAny<bool>()))
            .ReturnsAsync(OperationResult.Failed("navigation settings are read-only"));
        vm.SelectedCategory = vm.Categories.Single(item => item.Category == SettingsCategory.Appearance);
        await vm.ResetCommand.ExecuteAsync(null);
        vm.Status.Should().Be("navigation settings are read-only");
        vm.CanReset.Should().BeTrue();
    }

    [Theory]
    [InlineData("Settings_Info")]
    [InlineData("Menu_CheckForUpdates")]
    [InlineData("Tooltip_Documentation")]
    [InlineData("Menu_SupportWinOptimizer")]
    [InlineData("Tooltip_ReportBug")]
    [InlineData("Settings_AutomaticAppUpdates")]
    [InlineData("Settings_AutomaticScriptUpdates")]
    public void InfoSearchFindsItsActionsAndUpdatePreferences(string query)
    {
        using var vm = CreateViewModel();
        vm.Query = query;
        vm.VisibleGroups.SelectMany(group => group.Items).Should().ContainSingle().Which.Category.Should().Be(SettingsCategory.Info);
    }

    [Fact]
    public async Task FullResetDestinationRestoresEveryCategoryOnSettingsPage()
    {
        using var vm = CreateViewModel();
        await vm.InitializeAsync();
        vm.Cleaner.BrowserHistory = true;
        vm.Cleaner.MinimumAgeDays = 30;
        vm.Cleaner.DeepClean = true;
        vm.Cleaner.BackupBeforeCleaning = false;
        vm.Drivers.OnlyWhqlUpdates = false;
        vm.SidebarPreferences.IsExpanded = false;
        vm.UpdatePreferences.CheckApplicationUpdatesAutomatically = false;
        vm.UpdatePreferences.UpdateRemovalScriptsAutomatically = false;
        vm.SelectedTheme = WinOptimizerTheme.DarkNative;
        vm.SelectedCategory = vm.Categories.Single(item => item.Category == SettingsCategory.ResetAll);
        vm.AppColorPreferences.SelectedColor = WinOptimizer.UI.Features.Common.Resources.AppColor.Pink;
        vm.ShowResetAll.Should().BeTrue();
        vm.ResetLabel.Should().Be("Settings_ResetAll");
        await vm.ResetCommand.ExecuteAsync(null);
        vm.Cleaner.Options.Should().BeEquivalentTo(new CleanerOptions());
        vm.Drivers.Options.OnlyWhqlUpdates.Should().BeTrue();
        vm.SidebarPreferences.IsExpanded.Should().BeTrue();
        vm.UpdatePreferences.Options.Should().BeEquivalentTo(new UpdateOptions());
        vm.SelectedLanguage.Should().Be("en");
        vm.AppColorPreferences.SelectedColor.Should().Be(WinOptimizer.UI.Features.Common.Resources.AppColor.Blue);
        vm.SelectedTheme.Should().Be(WinOptimizerTheme.System);
        _mockPreferencesService.Verify(service => service.SetPreferenceAsync("Language", "en"), Times.Once);
        _mockThemeService.Verify(service => service.SetTheme(WinOptimizerTheme.System), Times.Once);
        vm.Status.Should().BeEmpty();
    }

    [Fact]
    public async Task ResetReportsPersistenceFailure_AndReenablesReset()
    {
        using var vm = CreateViewModel();
        await vm.InitializeAsync();
        _mockPreferencesService.Setup(service => service.SetPreferenceAsync("Drivers", It.IsAny<DriverScanOptions>()))
            .ReturnsAsync(OperationResult.Failed("disk full"));
        vm.SelectedCategory = vm.Categories.Single(item => item.Category == SettingsCategory.Drivers);
        await vm.ResetCommand.ExecuteAsync(null);
        vm.Status.Should().Be("disk full");
        vm.CanReset.Should().BeTrue();
    }

    [Fact]
    public void Dispose_UnsubscribesFromLanguageChanged()
    {
        var vm = CreateViewModel();
        vm.Dispose();

        var changedProperties = new List<string>();
        vm.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName!);

        _mockLocalization.Raise(l => l.LanguageChanged += null, EventArgs.Empty);

        changedProperties.Should().BeEmpty();
    }
}
