using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Settings.ViewModels;
using Xunit;
using WinOptimizer.Core.Features.Cleaner.Interfaces;
using WinOptimizer.Core.Features.Cleaner.Models;
using WinOptimizer.Core.Features.Drivers.Models;
using WinOptimizer.Core.Features.Management;
using WinOptimizer.UI.Features.Cleaner.ViewModels;
using WinOptimizer.UI.Features.Drivers.ViewModels;
using WinOptimizer.UI.Features.Common.ViewModels;
using WinOptimizer.UI.ViewModels;
using WinOptimizer.UI.Tests.Helpers;
using WinOptimizer.Core.Features.Common.Constants;

namespace WinOptimizer.UI.Tests.ViewModels;

public class SettingsViewModelTests
{
    private readonly Mock<ILocalizationService> _mockLocalization = new();
    private readonly Mock<IThemeService> _mockThemeService = new();
    private readonly Mock<IUserPreferencesService> _mockPreferencesService = new();
    private readonly Mock<ILogService> _mockLogService = new();
    private readonly Mock<IActivityJournalService> _journal = new();
    private readonly Mock<IProcessExecutor> _mockProcessExecutor = new();
    private readonly Mock<ITimeMachineRetentionService> _retention = new();
    private readonly Mock<IAutorunService> _autoruns = new(MockBehavior.Strict);
    private readonly Mock<IApplicationModeService> _mode = new();
    private bool _startupEnabled;

    public SettingsViewModelTests()
    {
        _mode.SetupGet(service => service.CurrentMode).Returns(WinOptimizerMode.Normal);
        _autoruns.Setup(service => service.ReadApplicationStartupAsync()).ReturnsAsync(() => _startupEnabled);
        _autoruns.Setup(service => service.SetApplicationStartupAsync(It.IsAny<bool>()))
            .Callback((bool enabled) => _startupEnabled = enabled).Returns(Task.CompletedTask);
        _retention.Setup(service => service.RetentionDaysAsync()).ReturnsAsync(90);
        _retention.Setup(service => service.UpdateRetentionAsync(It.IsAny<int>())).Returns(Task.CompletedTask);
        var emptyJournal = new Mock<IActivityJournalSession>();
        emptyJournal.Setup(session => session.ReadNextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JournalPage([], false, 0, []));
        _journal.Setup(service => service.OpenAsync(It.IsAny<JournalQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(emptyJournal.Object);
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
        _mockPreferencesService.Setup(p => p.GetPreferenceAsync(UserPreferenceKeys.BlurWindowBackground, It.IsAny<bool>()))
            .ReturnsAsync((string _, bool defaultValue) => defaultValue);
        _mockPreferencesService.Setup(p => p.SetPreferenceAsync(UserPreferenceKeys.BlurWindowBackground, It.IsAny<bool>())).ReturnsAsync(OperationResult.Succeeded());
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
        var cleaner = new CleanerSettingsViewModel(_mockPreferencesService.Object, backend.Object, _mockLocalization.Object);
        var drivers = new DriverSettingsViewModel(_mockPreferencesService.Object, _mockLocalization.Object);
        var versions = new Mock<IVersionService>();
        var release = new VersionInfo { Version = "v26.11.24" };
        versions.Setup(service => service.GetCurrentVersion()).Returns(release);
        versions.Setup(service => service.CheckForUpdateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(release);
        return new SettingsViewModel(
            _mockLocalization.Object,
            _mockThemeService.Object,
            _mockPreferencesService.Object,
            _mockLogService.Object, cleaner, drivers,
            new LogsAndHistoryViewModel(_journal.Object, _mockLogService.Object, _mockLocalization.Object, Mock.Of<IDialogService>()),
            new ApplicationInfoViewModel(_mockLocalization.Object, versions.Object, _mockLogService.Object,
                _mockProcessExecutor.Object,
                new UpdateCheckViewModel(versions.Object, _mockLocalization.Object, _mockLogService.Object,
                    Mock.Of<IBackgroundUpdateCheckService>(), Mock.Of<IDispatcherService>(), Mock.Of<IApplicationCloseService>())),
            new UpdatePreferencesViewModel(_mockPreferencesService.Object, _mockLocalization.Object),
            new SidebarPreferencesViewModel(_mockPreferencesService.Object, _mockLocalization.Object),
            new AppColorPreferencesViewModel(_mockPreferencesService.Object, _mockLocalization.Object,
                new WinOptimizer.UI.Features.Common.Resources.AppColorResources()),
            new WindowBackgroundPreferencesViewModel(_mockPreferencesService.Object, _mockLocalization.Object), _retention.Object,
            new ApplicationStartupViewModel(_autoruns.Object, _mockLocalization.Object, _mode.Object,
                new ExtensionTestDispatcher(), Mock.Of<ITaskProgressService>()));
    }

    [Fact]
    public async Task GeneralReadsStartupOnEntryAndConfirmsSchedulerChanges()
    {
        using var vm = CreateViewModel();
        await vm.InitializeAsync();
        _autoruns.VerifyNoOtherCalls();
        vm.Query = "ApplicationStartup";
        var category = Assert.Single(Assert.Single(vm.VisibleGroups).Items);
        Assert.Equal(SettingsCategory.General, category.Category);
        vm.SelectedCategory = category;
        var startup = vm.ApplicationStartup;
        Assert.False(startup.IsOn);
        Assert.True(startup.CanChange);
        Assert.Equal("Common_Off", startup.OffLabel);

        await startup.SetEnabledAsync(false);
        await startup.SetEnabledAsync(true);
        Assert.True(startup.IsOn);
        await startup.SetEnabledAsync(false);
        Assert.False(startup.IsOn);

        vm.SelectedCategory = null;
        _startupEnabled = true;
        vm.SelectedCategory = category;
        Assert.True(startup.IsEnabled);
        _autoruns.Verify(service => service.ReadApplicationStartupAsync(), Times.Exactly(4));
        _autoruns.Verify(service => service.SetApplicationStartupAsync(true), Times.Once);
        _autoruns.Verify(service => service.SetApplicationStartupAsync(false), Times.Once);
    }

    [Fact]
    public async Task StartupReadFailureClearsStaleStateAndDisablesChanges()
    {
        _startupEnabled = true;
        using var vm = CreateViewModel();
        vm.SelectedCategory = vm.Categories.Single(item => item.Category == SettingsCategory.General);
        var startup = vm.ApplicationStartup;
        Assert.True(startup.IsOn);
        Assert.True(startup.CanChange);
        _autoruns.Setup(service => service.ReadApplicationStartupAsync())
            .ThrowsAsync(new UnauthorizedAccessException("Scheduler access denied"));

        await startup.RefreshAsync();

        Assert.True(startup.IsError);
        Assert.Equal("Scheduler access denied", startup.Status);
        Assert.Null(startup.IsEnabled);
        Assert.False(startup.IsOn);
        Assert.Equal("Autoruns_Unknown", startup.OffLabel);
        Assert.False(startup.CanChange);
    }

    [Fact]
    public async Task RejectedStartupChangeKeepsTheConfirmedSwitchState()
    {
        _startupEnabled = true;
        _autoruns.Setup(service => service.SetApplicationStartupAsync(false))
            .ThrowsAsync(new UnauthorizedAccessException("Scheduler access denied"));
        using var vm = CreateViewModel();
        vm.SelectedCategory = vm.Categories.Single(item => item.Category == SettingsCategory.General);
        var startup = vm.ApplicationStartup;

        await startup.SetEnabledAsync(false);

        Assert.True(startup.IsOn);
        Assert.True(startup.CanChange);
        Assert.True(startup.IsError);
        Assert.Equal("Scheduler access denied", startup.Status);
        _autoruns.Verify(service => service.ReadApplicationStartupAsync(), Times.Once);
    }

    [Theory]
    [InlineData(WinOptimizerMode.Builder)]
    [InlineData(WinOptimizerMode.ConfigReview)]
    public async Task StartupRemainsReadableInModesThatBlockChanges(WinOptimizerMode mode)
    {
        _mode.SetupGet(service => service.CurrentMode).Returns(mode);
        using var vm = CreateViewModel();
        vm.SelectedCategory = vm.Categories.Single(item => item.Category == SettingsCategory.General);
        var startup = vm.ApplicationStartup;
        Assert.False(startup.IsEnabled);
        Assert.False(startup.CanChange);

        await startup.SetEnabledAsync(true);

        _autoruns.Verify(service => service.SetApplicationStartupAsync(It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task StartupWriteWaitsForSchedulerReadbackBeforeShutdownContinues()
    {
        using var vm = CreateViewModel();
        vm.SelectedCategory = vm.Categories.Single(item => item.Category == SettingsCategory.General);
        var startup = vm.ApplicationStartup;
        var pending = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _autoruns.Setup(service => service.ReadApplicationStartupAsync()).Returns(pending.Task);

        var change = startup.SetEnabledAsync(true);
        var shutdown = startup.WaitForWriteAsync();
        Assert.False(shutdown.IsCompleted);
        Assert.False(startup.CanChange);

        pending.SetResult(true);
        await change;
        await shutdown;
        Assert.True(startup.IsEnabled);
    }

    [Theory]
    [InlineData(SettingsCategory.ResetAll)]
    [InlineData(SettingsCategory.TimeMachine)]
    public async Task ResetRestoresTimeMachineRetentionInStorageAndOnThePage(SettingsCategory category)
    {
        using var vm = CreateViewModel();
        await vm.InitializeAsync();
        Assert.Equal(90, vm.TimeMachineDays);
        vm.SelectedCategory = vm.Categories.Single(item => item.Category == category);

        await vm.ResetCommand.ExecuteAsync(null);

        _retention.Verify(service => service.UpdateRetentionAsync(TimeMachineRetention.DefaultDays), Times.Once);
        Assert.Equal(TimeMachineRetention.DefaultDays, vm.TimeMachineDays);
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
        vm.VisibleGroups.SelectMany(group => group.Items).Should().Equal(vm.Categories)
            .And.ContainSingle(item => item.Category == SettingsCategory.TimeMachine);
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
        vm.SelectedCategory = vm.Categories.Single(item => item.Category == SettingsCategory.General);
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
    public async Task GeneralResetRestoresLanguageAppearanceAndUpdates()
    {
        _mockLocalization.SetupGet(service => service.CurrentLanguage).Returns("fr");
        _mockLocalization.Setup(service => service.SetLanguage("en")).Returns(true);
        _mockThemeService.SetupGet(service => service.CurrentTheme).Returns(WinOptimizerTheme.DarkNative);
        _mockPreferencesService.Setup(p => p.GetPreferenceAsync(UserPreferenceKeys.BlurWindowBackground, It.IsAny<bool>())).ReturnsAsync(true);
        using var vm = CreateViewModel();
        await vm.InitializeAsync();
        vm.Cleaner.MinimumAgeDays = 30;
        vm.Drivers.OnlyWhqlUpdates = false;
        vm.SidebarPreferences.IsExpanded = false;
        vm.UpdatePreferences.CheckApplicationUpdatesAutomatically = false;
        vm.AppColorPreferences.SelectedColor = WinOptimizer.UI.Features.Common.Resources.AppColor.Purple;
        vm.UpdatePreferences.UpdateRemovalScriptsAutomatically = false;
        vm.SelectedCategory = vm.Categories.Single(item => item.Category == SettingsCategory.General);
        vm.ShowGeneral.Should().BeTrue();
        vm.ShowCategoryReset.Should().BeTrue();
        await vm.ResetCommand.ExecuteAsync(null);
        vm.UpdatePreferences.Options.Should().BeEquivalentTo(new UpdateOptions());
        vm.SelectedLanguage.Should().Be("en");
        vm.SelectedTheme.Should().Be(WinOptimizerTheme.System);
        vm.SidebarPreferences.IsExpanded.Should().BeTrue();
        vm.AppColorPreferences.SelectedColor.Should().Be(WinOptimizer.UI.Features.Common.Resources.AppColor.Blue);
        vm.WindowBackgroundPreferences.IsBlurEnabled.Should().BeFalse();
        _mockLocalization.Verify(service => service.SetLanguage("en"), Times.Once);
        _mockPreferencesService.Verify(service => service.SetPreferenceAsync("Language", "en"), Times.Once);
        _mockThemeService.Verify(service => service.SetTheme(WinOptimizerTheme.System), Times.Once);
        _mockPreferencesService.Verify(service => service.SetPreferenceAsync(UserPreferenceKeys.BlurWindowBackground, false), Times.Once);
        vm.Cleaner.Options.MinimumAgeDays.Should().Be(30);
        vm.Drivers.Options.OnlyWhqlUpdates.Should().BeFalse();
        _retention.Verify(service => service.UpdateRetentionAsync(It.IsAny<int>()), Times.Never);
        _autoruns.Verify(service => service.SetApplicationStartupAsync(It.IsAny<bool>()), Times.Never);
        vm.Status.Should().BeEmpty();
    }

    [Fact]
    public async Task GeneralResetReportsSidebarSaveFailureAndStillResetsUpdates()
    {
        using var vm = CreateViewModel();
        await vm.InitializeAsync();
        vm.UpdatePreferences.UpdateRemovalScriptsAutomatically = false;
        _mockPreferencesService.Setup(service => service.SetPreferenceAsync(UserPreferenceKeys.SidebarExpanded, It.IsAny<bool>()))
            .ReturnsAsync(OperationResult.Failed("navigation settings are read-only"));
        vm.SelectedCategory = vm.Categories.Single(item => item.Category == SettingsCategory.General);
        await vm.ResetCommand.ExecuteAsync(null);
        vm.Status.Should().Be("navigation settings are read-only");
        vm.UpdatePreferences.Options.Should().BeEquivalentTo(new UpdateOptions());
        vm.CanReset.Should().BeTrue();
    }

    [Theory]
    [InlineData("Category_General")]
    [InlineData("Settings_Appearance")]
    [InlineData("Settings_Menu_Language")]
    [InlineData("Settings_Theme_Title")]
    [InlineData("Settings_ExpandNavigation")]
    [InlineData("Settings_ExpandNavigationDescription")]
    [InlineData("Settings_AppColor")]
    [InlineData("Settings_AppColorDescription")]
    [InlineData("Settings_AppColor_Purple")]
    [InlineData("Settings_BlurWindowBackground")]
    [InlineData("Settings_BlurWindowBackgroundDescription")]
    [InlineData("Settings_Info")]
    [InlineData("Dialog_Update_CurrentVersion")]
    [InlineData("Menu_CheckForUpdates")]
    [InlineData("Tooltip_Documentation")]
    [InlineData("Menu_SupportWinOptimizer")]
    [InlineData("Tooltip_ReportBug")]
    [InlineData("Settings_AutomaticAppUpdates")]
    [InlineData("Settings_AutomaticScriptUpdates")]
    [InlineData("Autoruns_ApplicationStartupDescription")]
    public void SearchFindsGeneralSettingsAndActions(string query)
    {
        using var vm = CreateViewModel();
        vm.Query = query;
        vm.VisibleGroups.SelectMany(group => group.Items).Should().ContainSingle().Which.Should().BeSameAs(vm.Categories[0]);
    }

    [Fact]
    public async Task FullResetDestinationRestoresEveryCategoryOnSettingsPage()
    {
        _mockPreferencesService.Setup(service => service.GetPreferenceAsync("Cleaner", It.IsAny<CleanerOptions>()))
            .ReturnsAsync(new CleanerOptions { BackupBeforeCleaning = false });
        using var vm = CreateViewModel();
        await vm.InitializeAsync();
        vm.Cleaner.BrowserHistory = true;
        vm.Cleaner.MinimumAgeDays = 30;
        vm.Cleaner.DeepClean = true;
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
