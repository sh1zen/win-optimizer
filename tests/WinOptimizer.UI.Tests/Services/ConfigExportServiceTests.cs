using WinOptimizer.Application.Configuration;
using FluentAssertions;
using Microsoft.UI.Xaml;
using Moq;
using WinOptimizer.Core.Features.AdvancedTools.Interfaces;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Services;
using WinOptimizer.UI.Features.SoftwareApps.ViewModels;
using System.Collections.ObjectModel;
using Xunit;

namespace WinOptimizer.UI.Tests.Services;

public class ConfigExportServiceTests
{
    private readonly Mock<ILogService> _mockLogService = new();
    private readonly Mock<IDialogService> _mockDialogService = new();
    private readonly Mock<ILocalizationService> _mockLocalizationService = new();
    private readonly Mock<ICompatibleSettingsRegistry> _mockCompatibleSettingsRegistry = new();
    private readonly Mock<IGlobalSettingsPreloader> _mockSettingsPreloader = new();
    private readonly Mock<ISystemSettingsDiscoveryService> _mockDiscoveryService = new();
    private readonly Mock<IInteractiveUserService> _mockInteractiveUserService = new();
    private readonly Mock<IWindowsAppsItemsProvider> _mockWindowsAppsVM = new();
    private readonly Mock<IExternalAppsItemsProvider> _mockExternalAppsVM = new();
    private readonly Mock<IFileSystemService> _mockFileSystemService = new();
    private readonly Mock<IMainWindowProvider> _mockMainWindowProvider = new();
    private readonly Mock<IDispatcherService> _mockDispatcher = new();
    private readonly Mock<IThemeService> _mockThemeService = new();
    private readonly Mock<IApplicationModeService> _mockApplicationModeService = new();
    private readonly Mock<IAutounattendXmlGeneratorService> _mockAutounattendGenerator = new();
    private readonly Mock<IPowerPlanComboBoxService> _powerPlans = new();

    public ConfigExportServiceTests()
    {
        _mockDispatcher
            .Setup(d => d.RunOnUIThread(It.IsAny<Action>()))
            .Callback<Action>(action => action());

        _mockLocalizationService
            .Setup(l => l.GetString(It.IsAny<string>()))
            .Returns((string key) => key);

        _mockLocalizationService
            .Setup(l => l.GetString(It.IsAny<string>(), It.IsAny<object[]>()))
            .Returns((string key, object[] args) => string.Format(key, args));

        _mockThemeService
            .Setup(t => t.GetEffectiveTheme())
            .Returns(ElementTheme.Dark);
    }

    private ConfigExportService CreateService()
    {
        return new ConfigExportService(
            _mockLogService.Object,
            _mockDialogService.Object,
            _mockLocalizationService.Object,
            _mockCompatibleSettingsRegistry.Object,
            _mockSettingsPreloader.Object,
            _mockDiscoveryService.Object,
            _mockInteractiveUserService.Object,
            new ConfigurationAppSource(_mockWindowsAppsVM.Object, _mockExternalAppsVM.Object),
            _mockFileSystemService.Object,
            new FilePickerService(_mockMainWindowProvider.Object),
            _mockApplicationModeService.Object,
            _mockAutounattendGenerator.Object, _powerPlans.Object);
    }

    // -------------------------------------------------------
    // CreateConfigurationFromSystemAsync
    // -------------------------------------------------------

    [Fact]
    public async Task CreateConfigurationFromSystemAsync_ReturnsVersion2Config()
    {
        _mockCompatibleSettingsRegistry
            .Setup(r => r.GetAllFilteredSettings())
            .Returns(new Dictionary<string, IEnumerable<SettingDefinition>>());

        _mockWindowsAppsVM.Setup(v => v.IsInitialized).Returns(true);
        _mockWindowsAppsVM.Setup(v => v.Items).Returns(new ObservableCollection<AppItemViewModel>());

        _mockExternalAppsVM.Setup(v => v.IsInitialized).Returns(true);
        _mockExternalAppsVM.Setup(v => v.Items).Returns(new ObservableCollection<AppItemViewModel>());

        var service = CreateService();
        var result = await service.CreateConfigurationFromSystemAsync();

        result.Should().NotBeNull();
        result.Version.Should().Be("2.0");
    }

    [Fact]
    public async Task CreateConfigurationFromSystemAsync_WithNoSettings_HasEmptyFeatureSections()
    {
        _mockCompatibleSettingsRegistry
            .Setup(r => r.GetAllFilteredSettings())
            .Returns(new Dictionary<string, IEnumerable<SettingDefinition>>());

        _mockWindowsAppsVM.Setup(v => v.IsInitialized).Returns(true);
        _mockWindowsAppsVM.Setup(v => v.Items).Returns(new ObservableCollection<AppItemViewModel>());

        _mockExternalAppsVM.Setup(v => v.IsInitialized).Returns(true);
        _mockExternalAppsVM.Setup(v => v.Items).Returns(new ObservableCollection<AppItemViewModel>());

        var service = CreateService();
        var result = await service.CreateConfigurationFromSystemAsync();

        result.Optimize.Features.Should().BeEmpty();
        result.Customize.Features.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateConfigurationFromSystemAsync_LoadsWindowsAppsWhenNotInitialized()
    {
        _mockCompatibleSettingsRegistry
            .Setup(r => r.GetAllFilteredSettings())
            .Returns(new Dictionary<string, IEnumerable<SettingDefinition>>());

        _mockWindowsAppsVM.Setup(v => v.IsInitialized).Returns(false);
        _mockWindowsAppsVM.Setup(v => v.Items).Returns(new ObservableCollection<AppItemViewModel>());

        _mockExternalAppsVM.Setup(v => v.IsInitialized).Returns(true);
        _mockExternalAppsVM.Setup(v => v.Items).Returns(new ObservableCollection<AppItemViewModel>());

        var service = CreateService();
        await service.CreateConfigurationFromSystemAsync();

        _mockWindowsAppsVM.Verify(v => v.LoadItemsAsync(), Times.Once);
    }

    [Fact]
    public async Task CreateConfigurationFromSystemAsync_IsBackup_ExportsInstalledStatusNotSelectedStatus()
    {
        _mockCompatibleSettingsRegistry
            .Setup(r => r.GetAllFilteredSettings())
            .Returns(new Dictionary<string, IEnumerable<SettingDefinition>>());

        var installedApp = CreateAppItemViewModel("app1", "App 1", isInstalled: true, isSelected: false);
        var selectedApp = CreateAppItemViewModel("app2", "App 2", isInstalled: false, isSelected: true);

        _mockWindowsAppsVM.Setup(v => v.IsInitialized).Returns(true);
        _mockWindowsAppsVM.Setup(v => v.Items)
            .Returns(new ObservableCollection<AppItemViewModel> { installedApp, selectedApp });

        _mockExternalAppsVM.Setup(v => v.IsInitialized).Returns(true);
        _mockExternalAppsVM.Setup(v => v.Items).Returns(new ObservableCollection<AppItemViewModel>());

        var service = CreateService();
        var result = await service.CreateConfigurationFromSystemAsync(isBackup: true);

        // In backup mode, only installed apps should appear (not selected ones)
        result.WindowsApps.Items.Should().ContainSingle()
            .Which.Id.Should().Be("app1");
    }

    [Fact]
    public async Task CreateConfigurationFromSystemAsync_NotBackup_ExportsSelectedStatus()
    {
        _mockCompatibleSettingsRegistry
            .Setup(r => r.GetAllFilteredSettings())
            .Returns(new Dictionary<string, IEnumerable<SettingDefinition>>());

        var installedApp = CreateAppItemViewModel("app1", "App 1", isInstalled: true, isSelected: false);
        var selectedApp = CreateAppItemViewModel("app2", "App 2", isInstalled: false, isSelected: true);

        _mockWindowsAppsVM.Setup(v => v.IsInitialized).Returns(true);
        _mockWindowsAppsVM.Setup(v => v.Items)
            .Returns(new ObservableCollection<AppItemViewModel> { installedApp, selectedApp });

        _mockExternalAppsVM.Setup(v => v.IsInitialized).Returns(true);
        _mockExternalAppsVM.Setup(v => v.Items).Returns(new ObservableCollection<AppItemViewModel>());

        var service = CreateService();
        var result = await service.CreateConfigurationFromSystemAsync(isBackup: false);

        // In normal mode, only selected apps should appear
        result.WindowsApps.Items.Should().ContainSingle()
            .Which.Id.Should().Be("app2");
    }

    [Fact]
    public async Task CreateConfigurationFromSystemAsync_IsBackup_SkipsExternalApps()
    {
        _mockCompatibleSettingsRegistry
            .Setup(r => r.GetAllFilteredSettings())
            .Returns(new Dictionary<string, IEnumerable<SettingDefinition>>());

        _mockWindowsAppsVM.Setup(v => v.IsInitialized).Returns(true);
        _mockWindowsAppsVM.Setup(v => v.Items).Returns(new ObservableCollection<AppItemViewModel>());

        _mockExternalAppsVM.Setup(v => v.IsInitialized).Returns(true);
        _mockExternalAppsVM.Setup(v => v.Items).Returns(new ObservableCollection<AppItemViewModel>());

        var service = CreateService();
        var result = await service.CreateConfigurationFromSystemAsync(isBackup: true);

        // External apps are not included in backups
        result.ExternalApps.Items.Should().BeEmpty();
    }

    // -------------------------------------------------------
    // ExportConfigurationAsync
    // -------------------------------------------------------

    [Fact]
    public async Task ExportConfigurationAsync_WhenNoMainWindow_ShowsError()
    {
        _mockCompatibleSettingsRegistry.Setup(r => r.IsInitialized).Returns(true);
        _mockCompatibleSettingsRegistry
            .Setup(r => r.GetAllFilteredSettings())
            .Returns(new Dictionary<string, IEnumerable<SettingDefinition>>());

        _mockWindowsAppsVM.Setup(v => v.IsInitialized).Returns(true);
        _mockWindowsAppsVM.Setup(v => v.Items).Returns(new ObservableCollection<AppItemViewModel>());

        _mockExternalAppsVM.Setup(v => v.IsInitialized).Returns(true);
        _mockExternalAppsVM.Setup(v => v.Items).Returns(new ObservableCollection<AppItemViewModel>());

        // Empty items triggers a "no apps selected" confirmation dialog before
        // the window check — allow it to proceed so we reach the null window path.
        _mockDialogService
            .Setup(d => d.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()))
            .ReturnsAsync(new ConfirmationResponse { Confirmed = true });

        _mockMainWindowProvider
            .Setup(p => p.MainWindow)
            .Returns((Microsoft.UI.Xaml.Window?)null);

        var service = CreateService();
        await service.ExportConfigurationAsync();

        _mockDialogService.Verify(
            d => d.ShowErrorAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
    }

    // -------------------------------------------------------
    // CreateUserBackupConfigAsync
    // -------------------------------------------------------

    /// <summary>Checks account-scoped backup paths using synthetic identity and filesystem boundaries.</summary>
    [Fact]
    public async Task CreateUserBackupConfigAsync_WritesBackupInInteractiveAccountDirectory()
    {
        _mockCompatibleSettingsRegistry.Setup(r => r.IsInitialized).Returns(true);
        _mockCompatibleSettingsRegistry
            .Setup(r => r.GetAllFilteredSettings())
            .Returns(new Dictionary<string, IEnumerable<SettingDefinition>>());

        _mockWindowsAppsVM.Setup(v => v.IsInitialized).Returns(true);
        _mockWindowsAppsVM.Setup(v => v.Items).Returns(new ObservableCollection<AppItemViewModel>());

        _mockExternalAppsVM.Setup(v => v.IsInitialized).Returns(true);
        _mockExternalAppsVM.Setup(v => v.Items).Returns(new ObservableCollection<AppItemViewModel>());

        const string interactiveSid = "S-1-5-21-111-222-333-1001";
        _mockInteractiveUserService.SetupGet(s => s.InteractiveUserSid).Returns(interactiveSid);
        var backupDirectory = Path.Combine(AppDataPaths.Root, "Users", interactiveSid, "Backups", "Configurations");

        _mockFileSystemService
            .Setup(fs => fs.CombinePath(It.IsAny<string[]>()))
            .Returns((string[] paths) => Path.Combine(paths));

        string? writtenPath = null;
        _mockFileSystemService
            .Setup(fs => fs.WriteAllTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((path, contents, token) => writtenPath = path)
            .Returns(Task.CompletedTask);

        var service = CreateService();
        await service.CreateUserBackupConfigAsync();

        _mockFileSystemService.Verify(
            fs => fs.CreateDirectory(backupDirectory),
            Times.Once);
        Assert.NotNull(writtenPath);
        Path.GetDirectoryName(writtenPath).Should().Be(backupDirectory);
        Path.GetFileName(writtenPath).Should().MatchRegex(@"^UserBackup_[0-9]{8}_[0-9]{6}\.win-optimizer$");
        _mockFileSystemService.Verify(
            fs => fs.WriteAllTextAsync(writtenPath, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateUserBackupConfigAsync_WhenExceptionOccurs_LogsError()
    {
        _mockCompatibleSettingsRegistry.Setup(r => r.IsInitialized).Returns(true);
        _mockCompatibleSettingsRegistry
            .Setup(r => r.GetAllFilteredSettings())
            .Throws(new Exception("Test error"));

        var service = CreateService();
        await service.CreateUserBackupConfigAsync();

        _mockLogService.Verify(
            l => l.Log(LogLevel.Error, It.Is<string>(s => s.Contains("Test error"))),
            Times.Once);
    }

    // -------------------------------------------------------
    // Helpers
    // -------------------------------------------------------

    [Fact]
    public async Task TrialExport_PreservesCompatibleChoicesNumericPowerAndActions()
    {
        _mockApplicationModeService.SetupGet(x => x.CurrentMode).Returns(WinOptimizerMode.Builder);
        var definitions = new[]
        {
            new SettingDefinition { Id = "toggle", Name = "Toggle", Description = "", InputType = InputType.Toggle },
            new SettingDefinition { Id = "numeric", Name = "Numeric", Description = "", InputType = InputType.NumericRange },
            new SettingDefinition { Id = "acdc", Name = "AC/DC", Description = "", InputType = InputType.Selection },
            new SettingDefinition { Id = "action", Name = "Action", Description = "", InputType = InputType.Action },
            new SettingDefinition { Id = "custom", Name = "Custom", Description = "", InputType = InputType.Selection },
            new SettingDefinition { Id = WinOptimizer.Core.Features.Common.Constants.SettingIds.PowerPlanSelection, Name = "Plan", Description = "", InputType = InputType.Selection }
        };
        _mockCompatibleSettingsRegistry.Setup(x => x.GetAllFilteredSettings()).Returns(
            new Dictionary<string, IEnumerable<SettingDefinition>> { [WinOptimizer.Core.Features.Common.Constants.FeatureIds.Power] = definitions });
        _mockDiscoveryService.Setup(x => x.GetSettingStatesAsync(It.IsAny<IEnumerable<SettingDefinition>>()))
            .ReturnsAsync(new Dictionary<string, SettingStateResult>());
        _mockWindowsAppsVM.SetupGet(x => x.IsInitialized).Returns(true);
        _mockWindowsAppsVM.SetupGet(x => x.Items).Returns(new ObservableCollection<AppItemViewModel>());
        _mockExternalAppsVM.SetupGet(x => x.IsInitialized).Returns(true);
        _mockExternalAppsVM.SetupGet(x => x.Items).Returns(new ObservableCollection<AppItemViewModel>());
        _mockApplicationModeService.Setup(x => x.GetBuilderEdits()).Returns(new BuilderEdit[]
        {
            new() { SettingId = "toggle", InputType = InputType.Toggle, IsSelected = true },
            new() { SettingId = "numeric", InputType = InputType.NumericRange, PowerSettings = new() { ["ACValue"] = 1200, ["DCValue"] = 600 } },
            new() { SettingId = "acdc", InputType = InputType.Selection, PowerSettings = new() { ["ACIndex"] = 1, ["DCIndex"] = 2 } },
            new() { SettingId = "action", InputType = InputType.Action, IsSelected = true },
            new() { SettingId = "custom", InputType = InputType.Selection, CustomStateValues = new() { ["key"] = 42 } },
            new() { SettingId = WinOptimizer.Core.Features.Common.Constants.SettingIds.PowerPlanSelection, InputType = InputType.Selection, SelectedIndex = 2 }
        });
        _powerPlans.Setup(x => x.ResolvePowerPlanByIndexAsync(2)).ReturnsAsync(new PowerPlanResolutionResult
            { Success = true, Guid = "stable-guid", DisplayName = "Authored plan" });
        var config = await CreateService().CreateConfigurationFromUiStateAsync();
        var items = config.Optimize.Features[WinOptimizer.Core.Features.Common.Constants.FeatureIds.Power].Items.ToDictionary(x => x.Id);
        items["toggle"].IsSelected.Should().BeTrue();
        items["numeric"].PowerSettings.Should().BeEquivalentTo(new Dictionary<string, object> { ["ACValue"] = 1200, ["DCValue"] = 600 });
        items["acdc"].PowerSettings.Should().BeEquivalentTo(new Dictionary<string, object> { ["ACIndex"] = 1, ["DCIndex"] = 2 });
        items["acdc"].SelectedIndex.Should().BeNull();
        items["action"].IsSelected.Should().BeTrue();
        items["custom"].CustomStateValues.Should().ContainKey("key").WhoseValue.Should().Be(42);
        items[WinOptimizer.Core.Features.Common.Constants.SettingIds.PowerPlanSelection].PowerPlanGuid.Should().Be("stable-guid");
        _mockCompatibleSettingsRegistry.Verify(x => x.GetAllBypassedSettings(), Times.Never);

        // Exercise the actual Export command path up to its native file-picker boundary.
        _mockCompatibleSettingsRegistry.SetupGet(x => x.IsInitialized).Returns(true);
        _mockDialogService.Setup(x => x.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()))
            .ReturnsAsync(new ConfirmationResponse { Confirmed = true });
        await CreateService().ExportConfigurationAsync();
        _mockApplicationModeService.Verify(x => x.GetBuilderEdits(), Times.Exactly(2));
    }

    [Fact]
    public async Task TrialApply_CopiesOnlyEditedCompatibleSettings()
    {
        _mockApplicationModeService.SetupGet(x => x.CurrentMode).Returns(WinOptimizerMode.Builder);
        _mockCompatibleSettingsRegistry.SetupGet(x => x.IsInitialized).Returns(true);
        var definitions = new[]
        {
            new SettingDefinition { Id = "edited", Name = "Edited", Description = "", InputType = InputType.NumericRange },
            new SettingDefinition { Id = "untouched", Name = "Untouched", Description = "", InputType = InputType.Toggle }
        };
        _mockCompatibleSettingsRegistry.Setup(x => x.GetAllFilteredSettings()).Returns(
            new Dictionary<string, IEnumerable<SettingDefinition>> { [WinOptimizer.Core.Features.Common.Constants.FeatureIds.Power] = definitions });
        _mockDiscoveryService.Setup(x => x.GetSettingStatesAsync(It.IsAny<IEnumerable<SettingDefinition>>()))
            .ReturnsAsync(new Dictionary<string, SettingStateResult>());
        _mockWindowsAppsVM.SetupGet(x => x.IsInitialized).Returns(true);
        _mockWindowsAppsVM.SetupGet(x => x.Items).Returns(new ObservableCollection<AppItemViewModel>());
        _mockExternalAppsVM.SetupGet(x => x.IsInitialized).Returns(true);
        _mockExternalAppsVM.SetupGet(x => x.Items).Returns(new ObservableCollection<AppItemViewModel>());
        var edit = new BuilderEdit { SettingId = "edited", InputType = InputType.NumericRange, PowerSettings = new() { ["ACValue"] = 1200 } };
        _mockApplicationModeService.Setup(x => x.GetBuilderEdits()).Returns(new[] { edit });

        var config = await CreateService().CreateConfigurationFromBuilderEditsAsync();

        var item = config.Optimize.Features.Single().Value.Items.Should().ContainSingle().Subject;
        item.Id.Should().Be("edited");
        item.PowerSettings.Should().NotBeSameAs(edit.PowerSettings);
        edit.PowerSettings["ACValue"] = 10;
        item.PowerSettings!["ACValue"].Should().Be(1200);
        config.WindowsApps.Items.Should().BeEmpty();
        config.ExternalApps.Items.Should().BeEmpty();
        config.Customize.Features.Should().BeEmpty();
        _mockCompatibleSettingsRegistry.Verify(x => x.GetAllBypassedSettings(), Times.Never);
    }

    [Fact]
    public async Task TrialApply_WithoutEditsDoesNotReadWindows()
    {
        _mockApplicationModeService.Setup(x => x.GetBuilderEdits()).Returns(Array.Empty<BuilderEdit>());
        var config = await CreateService().CreateConfigurationFromBuilderEditsAsync();
        config.Optimize.Features.Should().BeEmpty();
        config.Customize.Features.Should().BeEmpty();
        _mockDiscoveryService.VerifyNoOtherCalls();
        _mockSettingsPreloader.VerifyNoOtherCalls();
    }

    private AppItemViewModel CreateAppItemViewModel(
        string id, string name,
        bool isInstalled = false, bool isSelected = false,
        string[]? appxPackageName = null)
    {
        var definition = new ItemDefinition
        {
            Id = id,
            Name = name,
            Description = "Test",
            IsInstalled = isInstalled,
            AppxPackageName = appxPackageName ?? new[] { $"{id}.Package" }
        };

        var vm = new AppItemViewModel(
            definition,
            _mockLocalizationService.Object,
            _mockDispatcher.Object,
            _mockThemeService.Object);

        vm.IsSelected = isSelected;
        return vm;
    }
}
