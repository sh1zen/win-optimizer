using WinOptimizer.Application.Configuration;
using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using Xunit;

namespace WinOptimizer.Application.Tests;

public class ConfigApplicationExecutionServiceTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(3, true)]
    [InlineData(11, false)]
    public async Task ExplorerChecksImmediatelyAndRetainsAllDelayedRetries(int probes, bool found)
    {
        var timers = System.Threading.Channels.Channel.CreateUnbounded<Action>();
        var clock = new Mock<TimeProvider>();
        clock.Setup(provider => provider.CreateTimer(It.IsAny<TimerCallback>(), It.IsAny<object?>(),
                TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan))
            .Returns((TimerCallback callback, object? state, TimeSpan _, TimeSpan _) =>
            {
                timers.Writer.TryWrite(() => callback(state));
                return Mock.Of<ITimer>();
            });
        var calls = 0;
        var verification = ConfigApplicationExecutionService.VerifyManualExplorerStartAsync(() => ++calls == probes && found, clock.Object);
        Assert.Equal(1, calls);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        for (var i = 1; i < probes; i++) (await timers.Reader.ReadAsync(deadline.Token))();

        Assert.Equal(found, await verification.WaitAsync(deadline.Token));
        Assert.Equal(probes, calls);
        Assert.False(timers.Reader.TryRead(out _));
    }
    [Theory]
    [InlineData("Taskbar")]
    [InlineData("StartMenu")]
    public async Task EmptyConfigurationStillExecutesTheRequestedAction(string feature)
    {
        _mockConfigLoadService.Setup(service => service.DetectIncompatibleSettings(It.IsAny<UnifiedConfigurationFile>())).Returns([]);
        _mockBridgeService.Setup(bridge => bridge.ApplyConfigurationSectionAsync(It.IsAny<ConfigSection>(), It.IsAny<string>(),
            It.IsAny<Func<string, object?, SettingDefinition, Task<(bool confirmed, bool checkboxResult)>>?>())).ReturnsAsync(false);
        var options = new ImportOptions { ApplyCleanTaskbar = feature == "Taskbar", ApplyCleanStartMenu = feature == "StartMenu" };

        await CreateService().ExecuteConfigImportAsync(new UnifiedConfigurationFile(), options);

        _mockBridgeService.Verify(bridge => bridge.ApplyConfigurationSectionAsync(It.Is<ConfigSection>(section => section.Items.Count > 0),
            "Customize." + feature, It.IsAny<Func<string, object?, SettingDefinition, Task<(bool confirmed, bool checkboxResult)>>?>()), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("unrelated-theme-setting")]
    [InlineData(SettingIds.ThemeModeWindows)]
    public async Task WallpaperRequestUsesCurrentThemeUnlessAThemeModeIsImported(string? settingId)
    {
        var config = new UnifiedConfigurationFile();
        if (settingId is not null)
            config.Customize.Features = new Dictionary<string, ConfigSection>
            {
                [FeatureIds.WindowsTheme] = new() { Items = [new() { Id = settingId, Name = settingId }] }
            };
        _mockConfigLoadService.Setup(service => service.DetectIncompatibleSettings(config)).Returns([]);
        _mockBridgeService.Setup(bridge => bridge.ApplyConfigurationSectionAsync(It.IsAny<ConfigSection>(), It.IsAny<string>(),
            It.IsAny<Func<string, object?, SettingDefinition, Task<(bool confirmed, bool checkboxResult)>>?>())).ReturnsAsync(true);
        _mockWindowsUIManagementService.Setup(service => service.IsProcessRunning("explorer")).Returns(true);

        await CreateService().ExecuteConfigImportAsync(config, new ImportOptions { ApplyThemeWallpaper = true });

        _mockBridgeService.Verify(bridge => bridge.ApplyDefaultWallpaperForCurrentThemeAsync(),
            settingId == SettingIds.ThemeModeWindows ? Times.Never() : Times.Once());
        _mockBridgeService.Verify(bridge => bridge.ApplyConfigurationSectionAsync(
            It.Is<ConfigSection>(s => s.Items.Count == 1 && s.Items[0].Id == settingId), "Customize.WindowsTheme",
            It.IsAny<Func<string, object?, SettingDefinition, Task<(bool confirmed, bool checkboxResult)>>?>()),
            settingId is null ? Times.Never() : Times.Once());
    }

    [Theory]
    [InlineData("policy")]
    [InlineData("explorer-launch")]
    [InlineData("explorer-stop")]
    public async Task ImportDoesNotAnnounceSuccessAfterPolicyOrExplorerFailure(string operation)
    {
        var config = new UnifiedConfigurationFile();
        if (operation == "policy")
        {
            config.WindowsApps.Items = [new() { Id = "fixture", Name = "Fixture" }];
            _mockPolicyCleanupService.Setup(service => service.CleanupPolicyValues()).Throws(new IOException("Managed policy write denied"));
        }
        else
        {
            config.Optimize.Features = new Dictionary<string, ConfigSection>
            {
                ["Privacy"] = new() { Items = [new() { Id = "fixture", Name = "Fixture" }] }
            };
            _mockBridgeService.Setup(bridge => bridge.ApplyConfigurationSectionAsync(It.IsAny<ConfigSection>(), It.IsAny<string>(),
                It.IsAny<Func<string, object?, SettingDefinition, Task<(bool confirmed, bool checkboxResult)>>?>())).ReturnsAsync(true);
            _mockProcessExecutor.Setup(process => process.ShellExecuteAsync("explorer.exe", null, false, default)).ReturnsAsync((int?)null);
            if (operation == "explorer-stop")
            {
                _mockWindowsUIManagementService.Setup(service => service.IsProcessRunning("explorer")).Returns(true);
                _mockWindowsUIManagementService.Setup(service => service.KillProcess("explorer", true))
                    .Throws(new System.ComponentModel.Win32Exception(5));
            }
        }
        _mockConfigLoadService.Setup(service => service.DetectIncompatibleSettings(config)).Returns([]);

        await CreateService().ExecuteConfigImportAsync(config, new ImportOptions { IsWindowsDefaults = operation != "explorer-launch" });

        var expected = operation == "policy" ? "Managed policy write denied" : "Explorer could not be restarted";
        _mockDialogService.Verify(dialog => dialog.ShowMessage(It.Is<string>(message => message.Contains(expected)), "Error"), Times.Once);
        _mockDialogService.Verify(dialog => dialog.ShowInformationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _mockProcessExecutor.Verify(process => process.ShellExecuteAsync("explorer.exe", null, false, default),
            operation == "explorer-launch" ? Times.Once() : Times.Never());
        _mockPolicyCleanupService.Verify(service => service.CleanupPolicyValues(),
            operation == "explorer-launch" ? Times.Never() : Times.Once());
    }

    [Fact]
    public async Task FailedSettingsImportShowsErrorAndDoesNotAnnounceSuccessOrRestartExplorer()
    {
        var config = new UnifiedConfigurationFile();
        config.Optimize.Features = new Dictionary<string, ConfigSection> { ["Privacy"] = new() { Items = [new() { Id = "fixture", Name = "Fixture" }] } };
        _mockConfigLoadService.Setup(service => service.DetectIncompatibleSettings(config)).Returns([]);
        _mockBridgeService.Setup(bridge => bridge.ApplyConfigurationSectionAsync(It.IsAny<ConfigSection>(), It.IsAny<string>(),
            It.IsAny<Func<string, object?, SettingDefinition, Task<(bool confirmed, bool checkboxResult)>>?>())).ReturnsAsync(false);

        await CreateService().ExecuteConfigImportAsync(config, new ImportOptions());

        _mockDialogService.Verify(dialog => dialog.ShowMessage(It.Is<string>(message => message.Contains("failed")), "Error"), Times.Once);
        _mockDialogService.Verify(dialog => dialog.ShowInformationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _mockWindowsUIManagementService.Verify(ui => ui.KillProcess(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        _mockProcessExecutor.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowsAppRemovalUsesTheImportOptionsAtTheExecutionBoundary(bool saveScripts)
    {
        var marker = new IOException("Synthetic removal failed before Explorer handling.");
        _mockVmCoordinator.Setup(coordinator => coordinator.RemoveWindowsAppsAsync(true, saveScripts)).ThrowsAsync(marker);
        var options = new ImportOptions { ProcessWindowsAppsRemoval = true, SaveRemovalScripts = saveScripts };

        var error = await Assert.ThrowsAsync<IOException>(() =>
            CreateService().ApplyConfigurationWithOptionsAsync(new UnifiedConfigurationFile(), ["WindowsApps"], options));

        Assert.Same(marker, error);
        _mockVmCoordinator.Verify(coordinator => coordinator.RemoveWindowsAppsAsync(true, saveScripts), Times.Once);
        _mockProcessExecutor.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Taskbar", "taskbar-clean", false)]
    [InlineData("Taskbar", "taskbar-clean", true)]
    [InlineData("StartMenu", "start-menu-clean-11", false)]
    [InlineData("StartMenu", "start-menu-clean-11", true)]
    public async Task CleanupRequiresConsentAndRunsOnceWhenAlsoListedInTheFile(string feature, string action, bool approved)
    {
        var config = new UnifiedConfigurationFile();
        config.Customize.Features = new Dictionary<string, ConfigSection>
        {
            [feature] = new() { Items = [new() { Id = action, Name = action, IsSelected = true }, new() { Id = "regular", Name = "Regular setting" }] }
        };
        _mockConfigLoadService.Setup(service => service.DetectIncompatibleSettings(config)).Returns([]);
        _mockWindowsVersionService.Setup(service => service.IsWindows11()).Returns(true);
        List<string> applied = [];
        _mockBridgeService.Setup(bridge => bridge.ApplyConfigurationSectionAsync(It.IsAny<ConfigSection>(), "Customize." + feature,
                It.IsAny<Func<string, object?, SettingDefinition, Task<(bool, bool)>>>())).ReturnsAsync(
            (ConfigSection section, string _, Func<string, object?, SettingDefinition, Task<(bool, bool)>> _) =>
            {
                applied.AddRange(section.Items.Select(item => item.Id));
                return section.Items.All(item => item.Id != "regular");
            });

        await CreateService().ExecuteConfigImportAsync(config, new ImportOptions
        {
            ApplyCleanTaskbar = approved && feature == "Taskbar",
            ApplyCleanStartMenu = approved && feature == "StartMenu"
        });

        Assert.Equal(approved ? [action, "regular"] : new[] { "regular" }, applied);
        Assert.Contains(config.Customize.Features[feature].Items, item => item.Id == action);
        _mockWindowsUIManagementService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SelectOnlyImportsTheSelectionWithoutRestartingExplorer()
    {
        var config = new UnifiedConfigurationFile
        {
            WindowsApps = new() { Items = [new() { Id = "fixture", Name = "Fixture", IsSelected = true }] }
        };
        _mockConfigLoadService.Setup(service => service.DetectIncompatibleSettings(config)).Returns([]);

        await CreateService().ExecuteConfigImportAsync(config, new ImportOptions());

        _mockConfigAppSelectionService.Verify(selection => selection.SelectWindowsAppsFromConfigAsync(config.WindowsApps), Times.Once);
        _mockVmCoordinator.VerifyNoOtherCalls();
        _mockWindowsUIManagementService.VerifyNoOtherCalls();
        _mockProcessExecutor.VerifyNoOtherCalls();
        _mockDialogService.Verify(dialog => dialog.ShowInformationAsync("Config_Import_Success_Message", It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Theory]
    [InlineData(UninstallVerificationState.StillInstalled)]
    [InlineData(UninstallVerificationState.Unknown)]
    [InlineData(UninstallVerificationState.Deferred)]
    public async Task UnverifiedWindowsAppRemovalReportsItsOutcomeInsteadOfImportSuccess(UninstallVerificationState state)
    {
        var config = new UnifiedConfigurationFile
        {
            WindowsApps = new() { Items = [new() { Id = "fixture", Name = "Fixture", IsSelected = true }] }
        };
        _mockConfigLoadService.Setup(service => service.DetectIncompatibleSettings(config)).Returns([]);
        _mockConfigAppSelectionService.Setup(selection => selection.ConfirmWindowsAppsRemovalAsync()).ReturnsAsync((true, false));
        _mockVmCoordinator.Setup(coordinator => coordinator.RemoveWindowsAppsAsync(true, false)).ReturnsAsync(
            OperationResult<AppUninstallResult>.Succeeded(new(
                [new("first", "First app", UninstallVerificationState.Removed, "Verified"),
                 new("fixture", "Fixture", state, "Removal unconfirmed")], [])));

        await CreateService().ExecuteConfigImportAsync(config, new ImportOptions { ProcessWindowsAppsRemoval = true });

        _mockDialogService.Verify(dialog => dialog.ShowMessage(It.Is<string>(message => message.Contains("Fixture: Removal unconfirmed")), "Error"), Times.Once);
        _mockDialogService.Verify(dialog => dialog.ShowInformationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _mockWindowsUIManagementService.VerifyNoOtherCalls();
    }
    private readonly Mock<ILogService> _mockLogService = new();
    private readonly Mock<IDialogService> _mockDialogService = new();
    private readonly Mock<ILocalizationService> _mockLocalizationService = new();
    private readonly Mock<IWindowsVersionService> _mockWindowsVersionService = new();
    private readonly Mock<IConfigurationApplicationBridgeService> _mockBridgeService = new();
    private readonly Mock<IWindowsUIManagementService> _mockWindowsUIManagementService = new();
    private readonly Mock<IProcessExecutor> _mockProcessExecutor = new();
    private readonly Mock<IConfigImportOverlayService> _mockOverlayService = new();
    private readonly Mock<IConfigImportState> _mockConfigImportState = new();
    private readonly Mock<IConfigAppSelectionService> _mockConfigAppSelectionService = new();
    private readonly Mock<IConfigLoadService> _mockConfigLoadService = new();
    private readonly Mock<IReviewModeViewModelCoordinator> _mockVmCoordinator = new();
    private readonly Mock<IPolicyCleanupService> _mockPolicyCleanupService = new();
    private readonly Mock<IChangeHistoryService> _mockChangeHistoryService = new();
    private readonly Mock<IApplicationModeService> _mode = new();
    private readonly Mock<WinOptimizer.Core.Features.SoftwareApps.Interfaces.IUninstallResidueReview> _uninstallReview = new();

    public ConfigApplicationExecutionServiceTests()
    {
        _mockLocalizationService
            .Setup(l => l.GetString(It.IsAny<string>()))
            .Returns((string key) => key);

        _mockLocalizationService
            .Setup(l => l.GetString(It.IsAny<string>(), It.IsAny<object[]>()))
            .Returns((string key, object[] args) => string.Format(key, args));

        _mockChangeHistoryService
            .Setup(h => h.BeginBatch(It.IsAny<string>()))
            .Returns(Mock.Of<IDisposable>());
    }

    private ConfigApplicationExecutionService CreateService()
    {
        return new ConfigApplicationExecutionService(
            _mockLogService.Object,
            _mockDialogService.Object,
            _mockLocalizationService.Object,
            _mockWindowsVersionService.Object,
            _mockBridgeService.Object,
            _mockWindowsUIManagementService.Object,
            _mockProcessExecutor.Object,
            _mockOverlayService.Object,
            _mockConfigImportState.Object,
            _mockConfigAppSelectionService.Object,
            _mockConfigLoadService.Object,
            _mockVmCoordinator.Object,
            _mockPolicyCleanupService.Object,
            _mockChangeHistoryService.Object, _mode.Object,
            _uninstallReview.Object);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalAppsImport_RemovesOnlyWhenExplicitlyChosen_AfterOverlayAndSuccessDialog(bool remove)
    {
        var config = new UnifiedConfigurationFile
        {
            ExternalApps = new ConfigSection { Items = [new() { Id = "ext1", Name = "External app" }] }
        };
        _mockConfigLoadService.Setup(service => service.DetectIncompatibleSettings(config)).Returns([]);
        List<string> events = [];
        _mockOverlayService.Setup(overlay => overlay.HideOverlay()).Callback(() => events.Add("hide"));
        _mockDialogService.Setup(dialog => dialog.ShowInformationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback(() => events.Add("success")).Returns(Task.CompletedTask);
        _mockConfigAppSelectionService.Setup(service => service.ProcessExternalAppsRemovalAsync(config.ExternalApps))
            .Callback(() => events.Add("remove")).Returns(Task.CompletedTask);

        await CreateService().ExecuteConfigImportAsync(config, new ImportOptions { ProcessExternalAppsRemoval = remove });

        _mockConfigAppSelectionService.Verify(service => service.SelectExternalAppsFromConfigAsync(config.ExternalApps), Times.Once);
        _mockConfigAppSelectionService.Verify(service => service.ProcessExternalAppsRemovalAsync(config.ExternalApps), remove ? Times.Once() : Times.Never());
        Assert.Equal(remove ? new[] { "hide", "success", "remove" } : new[] { "hide", "success" }, events);
    }

    // -------------------------------------------------------
    // ExecuteConfigImportAsync
    // -------------------------------------------------------

    [Fact]
    public async Task TrialMode_DoesNotStartConfigurationApplication()
    {
        _mode.SetupGet(x => x.CurrentMode).Returns(WinOptimizerMode.Builder);
        await CreateService().ExecuteConfigImportAsync(new UnifiedConfigurationFile(), new ImportOptions());
        _mockBridgeService.VerifyNoOtherCalls();
        _mockPolicyCleanupService.VerifyNoOtherCalls();
        _mockOverlayService.VerifyNoOtherCalls();
        _mockDialogService.Verify(x => x.ShowInformationAsync("TrialMode_Blocked", "TrialMode_Label"), Times.Once);
    }

    [Fact]
    public async Task ExecuteConfigImportAsync_WhenNoSections_ShowsNoChangesMessage()
    {
        var config = new UnifiedConfigurationFile();
        var options = new ImportOptions();

        _mockConfigLoadService
            .Setup(s => s.DetectIncompatibleSettings(It.IsAny<UnifiedConfigurationFile>()))
            .Returns(new List<string>());

        var service = CreateService();
        await service.ExecuteConfigImportAsync(config, options);

        _mockDialogService.Verify(d => d.ShowMessage(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteConfigImportAsync_FiltersIncompatibleSettings()
    {
        var config = new UnifiedConfigurationFile();
        var filteredConfig = new UnifiedConfigurationFile();
        var options = new ImportOptions();

        _mockConfigLoadService
            .Setup(s => s.DetectIncompatibleSettings(It.IsAny<UnifiedConfigurationFile>()))
            .Returns(new List<string> { "Incompatible Setting" });

        _mockConfigLoadService
            .Setup(s => s.FilterConfigForCurrentSystem(It.IsAny<UnifiedConfigurationFile>()))
            .Returns(filteredConfig);

        var service = CreateService();
        await service.ExecuteConfigImportAsync(config, options);

        _mockConfigLoadService.Verify(
            s => s.FilterConfigForCurrentSystem(config),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteConfigImportAsync_WhenRemovalCancelled_ClearsWindowsAppsSelection()
    {
        var config = new UnifiedConfigurationFile
        {
            WindowsApps = new ConfigSection
            {
                Items = new List<ConfigurationItem>
                {
                    new ConfigurationItem { Id = "app1", Name = "App 1" }
                }
            }
        };

        var options = new ImportOptions { ProcessWindowsAppsRemoval = true };

        _mockConfigLoadService
            .Setup(s => s.DetectIncompatibleSettings(It.IsAny<UnifiedConfigurationFile>()))
            .Returns(new List<string>());

        _mockConfigAppSelectionService
            .Setup(s => s.ConfirmWindowsAppsRemovalAsync())
            .ReturnsAsync((false, true));

        var service = CreateService();
        await service.ExecuteConfigImportAsync(config, options);

        _mockConfigAppSelectionService.Verify(s => s.ConfirmWindowsAppsRemovalAsync(), Times.Once);
        _mockConfigAppSelectionService.Verify(
            s => s.ClearWindowsAppsSelectionAsync(),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteConfigImportAsync_SetsAndClearsConfigImportState()
    {
        var isActiveValues = new List<bool>();

        _mockConfigImportState
            .SetupSet(s => s.IsActive = It.IsAny<bool>())
            .Callback<bool>(v => isActiveValues.Add(v));

        var config = new UnifiedConfigurationFile
        {
            Optimize = new FeatureGroupSection
            {
                Features = new Dictionary<string, ConfigSection>
                {
                    ["Privacy"] = new ConfigSection
                    {
                        Items = new List<ConfigurationItem>
                        {
                            new ConfigurationItem { Id = "s1", Name = "S1" }
                        }
                    }
                }
            }
        };

        var options = new ImportOptions();

        _mockConfigLoadService
            .Setup(s => s.DetectIncompatibleSettings(It.IsAny<UnifiedConfigurationFile>()))
            .Returns(new List<string>());

        _mockBridgeService
            .Setup(b => b.ApplyConfigurationSectionAsync(
                It.IsAny<ConfigSection>(),
                It.IsAny<string>(),
                It.IsAny<Func<string, object?, SettingDefinition, Task<(bool confirmed, bool checkboxResult)>>>()))
            .ReturnsAsync(true);

        _mockWindowsUIManagementService
            .Setup(w => w.IsProcessRunning("explorer"))
            .Returns(true);

        var service = CreateService();
        await service.ExecuteConfigImportAsync(config, options);

        // Should have been set to true then false
        isActiveValues.Should().Contain(true);
        isActiveValues.Should().Contain(false);
        isActiveValues.Last().Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteConfigImportAsync_OnException_HidesOverlayAndShowsError()
    {
        var config = new UnifiedConfigurationFile();
        var options = new ImportOptions();

        _mockConfigLoadService
            .Setup(s => s.DetectIncompatibleSettings(It.IsAny<UnifiedConfigurationFile>()))
            .Throws(new Exception("Test error"));

        var service = CreateService();
        await service.ExecuteConfigImportAsync(config, options);

        _mockOverlayService.Verify(o => o.HideOverlay(), Times.Once);
        _mockDialogService.Verify(
            d => d.ShowMessage(It.Is<string>(s => s.Contains("Test error")), It.IsAny<string>()),
            Times.Once);
    }

    // -------------------------------------------------------
    // Policy Cleanup on Windows Defaults Import
    // -------------------------------------------------------

    [Fact]
    public async Task ExecuteConfigImportAsync_WindowsDefaults_CallsPolicyCleanup()
    {
        var config = new UnifiedConfigurationFile
        {
            Optimize = new FeatureGroupSection
            {
                IsIncluded = true,
                Features = new Dictionary<string, ConfigSection>
                {
                    ["Privacy"] = new ConfigSection
                    {
                        IsIncluded = true,
                        Items = new List<ConfigurationItem>
                        {
                            new ConfigurationItem { Id = "test-setting", Name = "Test", IsSelected = true }
                        }
                    }
                }
            }
        };

        var options = new ImportOptions { IsWindowsDefaults = true };

        _mockConfigLoadService
            .Setup(s => s.DetectIncompatibleSettings(It.IsAny<UnifiedConfigurationFile>()))
            .Returns(new List<string>());

        _mockBridgeService
            .Setup(b => b.ApplyConfigurationSectionAsync(
                It.IsAny<ConfigSection>(),
                It.IsAny<string>(),
                It.IsAny<Func<string, object?, SettingDefinition, Task<(bool, bool)>>>()))
            .ReturnsAsync(true);

        _mockWindowsUIManagementService
            .Setup(w => w.IsProcessRunning("explorer"))
            .Returns(true);

        _mockDialogService
            .Setup(d => d.ShowInformationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        var service = CreateService();
        await service.ExecuteConfigImportAsync(config, options);

        _mockPolicyCleanupService.Verify(p => p.CleanupPolicyValues(), Times.Once);
    }

    [Fact]
    public async Task ExecuteConfigImportAsync_NonWindowsDefaults_DoesNotCallPolicyCleanup()
    {
        var config = new UnifiedConfigurationFile
        {
            Optimize = new FeatureGroupSection
            {
                IsIncluded = true,
                Features = new Dictionary<string, ConfigSection>
                {
                    ["Privacy"] = new ConfigSection
                    {
                        IsIncluded = true,
                        Items = new List<ConfigurationItem>
                        {
                            new ConfigurationItem { Id = "test-setting", Name = "Test", IsSelected = true }
                        }
                    }
                }
            }
        };

        var options = new ImportOptions { IsWindowsDefaults = false };

        _mockConfigLoadService
            .Setup(s => s.DetectIncompatibleSettings(It.IsAny<UnifiedConfigurationFile>()))
            .Returns(new List<string>());

        _mockBridgeService
            .Setup(b => b.ApplyConfigurationSectionAsync(
                It.IsAny<ConfigSection>(),
                It.IsAny<string>(),
                It.IsAny<Func<string, object?, SettingDefinition, Task<(bool, bool)>>>()))
            .ReturnsAsync(true);

        _mockWindowsUIManagementService
            .Setup(w => w.IsProcessRunning("explorer"))
            .Returns(true);

        _mockDialogService
            .Setup(d => d.ShowInformationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        var service = CreateService();
        await service.ExecuteConfigImportAsync(config, options);

        _mockPolicyCleanupService.Verify(p => p.CleanupPolicyValues(), Times.Never);
    }
}
