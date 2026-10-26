using WinOptimizer.Application.Configuration;
using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using Xunit;

namespace WinOptimizer.Application.Tests;

public class ConfigApplicationExecutionServiceTests
{
    [Theory]
    [InlineData("Taskbar")]
    [InlineData("StartMenu")]
    [InlineData("WindowsTheme")]
    public async Task EmptyConfigurationStillExecutesTheRequestedAction(string feature)
    {
        _mockConfigLoadService.Setup(service => service.DetectIncompatibleSettings(It.IsAny<UnifiedConfigurationFile>())).Returns([]);
        _mockBridgeService.Setup(bridge => bridge.ApplyConfigurationSectionAsync(It.IsAny<ConfigSection>(), It.IsAny<string>(),
            It.IsAny<Func<string, object?, SettingDefinition, Task<(bool confirmed, bool checkboxResult)>>?>())).ReturnsAsync(false);
        var options = new ImportOptions { ApplyCleanTaskbar = feature == "Taskbar", ApplyCleanStartMenu = feature == "StartMenu", ApplyThemeWallpaper = feature == "WindowsTheme" };

        await CreateService().ExecuteConfigImportAsync(new UnifiedConfigurationFile(), options);

        _mockBridgeService.Verify(bridge => bridge.ApplyConfigurationSectionAsync(It.Is<ConfigSection>(section => section.Items.Count > 0),
            "Customize." + feature, It.IsAny<Func<string, object?, SettingDefinition, Task<(bool confirmed, bool checkboxResult)>>?>()), Times.Once);
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
        _mockWindowsUIManagementService.Verify(ui => ui.KillProcess(It.IsAny<string>()), Times.Never);
        _mockProcessExecutor.VerifyNoOtherCalls();
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
    public async Task ExecuteConfigImportAsync_WithWindowsApps_SelectsAppsFromConfig()
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

        var options = new ImportOptions
        {
            ProcessWindowsAppsRemoval = false
        };

        _mockConfigLoadService
            .Setup(s => s.DetectIncompatibleSettings(It.IsAny<UnifiedConfigurationFile>()))
            .Returns(new List<string>());

        // The config has only WindowsApps but no optimize/customize, so only that section
        var service = CreateService();
        await service.ExecuteConfigImportAsync(config, options);

        _mockConfigAppSelectionService.Verify(
            s => s.SelectWindowsAppsFromConfigAsync(It.IsAny<ConfigSection>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteConfigImportAsync_WithRemoval_ConfirmsWithUser()
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

        _mockConfigAppSelectionService.Verify(
            s => s.ConfirmWindowsAppsRemovalAsync(),
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

        _mockConfigAppSelectionService.Verify(
            s => s.ClearWindowsAppsSelectionAsync(),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteConfigImportAsync_ShowsAndHidesOverlay()
    {
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
                            new ConfigurationItem { Id = "setting1", Name = "Setting 1" }
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

        // Explorer restart
        var dialogEvents = new List<string>();
        var deferred = new Mock<IAsyncDisposable>();
        deferred.Setup(scope => scope.DisposeAsync()).Callback(() => dialogEvents.Add("reviews")).Returns(ValueTask.CompletedTask);
        _uninstallReview.Setup(review => review.Defer()).Callback(() => dialogEvents.Add("defer")).Returns(deferred.Object);
        _mockOverlayService.Setup(overlay => overlay.HideOverlay()).Callback(() => dialogEvents.Add("hide"));
        _mockWindowsUIManagementService
            .Setup(w => w.IsProcessRunning("explorer"))
            .Returns(true);

        var service = CreateService();
        await service.ExecuteConfigImportAsync(config, options);

        _mockOverlayService.Verify(o => o.ShowOverlay(It.IsAny<string>(), It.IsAny<string?>()), Times.Once);
        _mockOverlayService.Verify(o => o.HideOverlay(), Times.Once);
        Assert.Equal(new[] { "defer", "hide", "reviews" }, dialogEvents);
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
    // ApplyConfigurationWithOptionsAsync (public overload)
    // -------------------------------------------------------

    [Fact]
    public async Task ApplyConfigurationWithOptionsAsync_WithEmptyConfig_HandlesGracefully()
    {
        var config = new UnifiedConfigurationFile();
        var selectedSections = new List<string>();
        var options = new ImportOptions();

        _mockWindowsUIManagementService
            .Setup(w => w.IsProcessRunning("explorer"))
            .Returns(true);

        var service = CreateService();

        // Should not throw
        await service.ApplyConfigurationWithOptionsAsync(config, selectedSections, options);

        // Explorer restart still runs
        _mockOverlayService.Verify(
            o => o.UpdateStatus(It.IsAny<string>(), It.IsAny<string?>()),
            Times.AtLeastOnce);
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

        _mockPolicyCleanupService.Verify(p => p.CleanupPolicyKeys(), Times.Once);
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

        _mockPolicyCleanupService.Verify(p => p.CleanupPolicyKeys(), Times.Never);
    }
}
