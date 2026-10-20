using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Infrastructure.Features.Common.EventHandlers;
using WinOptimizer.UI.Features.Common.Services;
using WinOptimizer.UI.Features.Common.Interfaces;
using Xunit;

namespace WinOptimizer.UI.Tests.Services;

public class StartupOrchestratorTests
{
    private readonly Mock<ICompatibleSettingsRegistry> _settingsRegistry = new();
    private readonly Mock<IGlobalSettingsPreloader> _settingsPreloader = new();
    private readonly Mock<IUserPreferencesService> _preferencesService = new();
    private readonly Mock<IConfigurationService> _configurationService = new();
    private readonly Mock<ILogService> _logService = new();
    private readonly Mock<IWindowsAppsItemsProvider> _windowsApps = new();

    public StartupOrchestratorTests()
    {
        // Default preferences
        _preferencesService.Setup(p => p.GetPreference(
            UserPreferenceKeys.InitialConfigBackupCompleted, false))
            .Returns(true);
    }

    private StartupOrchestrator CreateSut(TooltipRefreshEventHandler? tooltipHandler = null)
    {
        return new StartupOrchestrator(
            _settingsRegistry.Object,
            _settingsPreloader.Object,
            tooltipHandler!,
            _preferencesService.Object,
            _configurationService.Object,
            _logService.Object,
            _windowsApps.Object);
    }

    private (Progress<string> StatusProgress, Progress<TaskProgressDetail> DetailedProgress,
        List<string> StatusReports) CreateProgressTracking()
    {
        var statusReports = new List<string>();
        var statusProgress = new Progress<string>(s => statusReports.Add(s));
        var detailedProgress = new Progress<TaskProgressDetail>();
        return (statusProgress, detailedProgress, statusReports);
    }

    // --- Phase 1: Settings registry initialization ---

    [Fact]
    public async Task RunStartupSequenceAsync_WaitsForSettingsWithoutStartingInventoryOrBackup()
    {
        var appLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registryLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _windowsApps.Setup(a => a.LoadItemsAsync()).Returns(appLoad.Task);
        _settingsRegistry.Setup(r => r.InitializeAsync()).Returns(registryLoad.Task);
        var sut = CreateSut();
        var (status, details, _) = CreateProgressTracking();
        var pending = sut.RunStartupSequenceAsync(status, details);
        try
        {
            _windowsApps.VerifyNoOtherCalls();
            _configurationService.VerifyNoOtherCalls();
            _settingsRegistry.Verify(r => r.InitializeAsync(), Times.Once);
            pending.IsCompleted.Should().BeFalse();
        }
        finally
        {
            appLoad.TrySetResult();
            registryLoad.TrySetResult();
        }
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        _windowsApps.VerifyNoOtherCalls();
        _configurationService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RunDeferredInitializationAsync_WindowsAppsFailure_CompletesAndReportsFailure()
    {
        _windowsApps.Setup(a => a.LoadItemsAsync()).ThrowsAsync(new InvalidOperationException("inventory failed"));
        var (status, details, _) = CreateProgressTracking();
        await CreateSut().RunDeferredInitializationAsync();
        _logService.Verify(l => l.LogWarning("Windows Apps initialization failed: inventory failed"), Times.Once);
    }

    [Fact]
    public async Task RunDeferredInitializationAsync_SharesInventoryAndWaitsBeforeCreatingBackup()
    {
        var appLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _windowsApps.Setup(a => a.LoadItemsAsync()).Returns(appLoad.Task);
        var (status, details, _) = CreateProgressTracking();
        _preferencesService.Setup(p => p.GetPreference(UserPreferenceKeys.InitialConfigBackupCompleted, false)).Returns(false);
        var sut = CreateSut();
        var pending = sut.RunDeferredInitializationAsync();
        try
        {
            sut.RunDeferredInitializationAsync().Should().BeSameAs(pending);
            _configurationService.VerifyNoOtherCalls();
            pending.IsCompleted.Should().BeFalse();
        }
        finally { appLoad.TrySetResult(); }
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        _windowsApps.Verify(a => a.LoadItemsAsync(), Times.Once);
        _configurationService.Verify(c => c.CreateUserBackupConfigAsync(), Times.Once);
    }

    [Fact]
    public async Task RunStartupSequenceAsync_InitializesSettingsRegistry()
    {
        var sut = CreateSut();
        var (statusProgress, detailedProgress, _) = CreateProgressTracking();

        await sut.RunStartupSequenceAsync(statusProgress, detailedProgress);

        _settingsRegistry.Verify(r => r.InitializeAsync(), Times.Once);
    }

    [Fact]
    public async Task RunStartupSequenceAsync_PreloadsSettings()
    {
        var sut = CreateSut();
        var (statusProgress, detailedProgress, _) = CreateProgressTracking();

        await sut.RunStartupSequenceAsync(statusProgress, detailedProgress);

        _settingsPreloader.Verify(p => p.PreloadAllSettingsAsync(), Times.Once);
    }

    [Fact]
    public async Task RunStartupSequenceAsync_SettingsFailureStillReturnsAndReportsFailure()
    {
        _settingsRegistry.Setup(r => r.InitializeAsync())
            .ThrowsAsync(new InvalidOperationException("Settings init failed"));

        var sut = CreateSut();
        var (statusProgress, detailedProgress, _) = CreateProgressTracking();

        var result = await sut.RunStartupSequenceAsync(statusProgress, detailedProgress);

        result.Should().NotBeNull();
        _logService.Verify(l => l.LogWarning(It.Is<string>(s =>
            s.Contains("Failed to initialize settings registry"))), Times.Once);
    }

    // --- Phase 2: User backup config ---

    [Fact]
    public async Task RunDeferredInitializationAsync_WhenBackupAlreadyCompleted_SkipsBackupPhase()
    {
        _preferencesService.Setup(p => p.GetPreference(
            UserPreferenceKeys.InitialConfigBackupCompleted, false))
            .Returns(true);

        var sut = CreateSut();
        var (statusProgress, detailedProgress, _) = CreateProgressTracking();

        await sut.RunDeferredInitializationAsync();

        _configurationService.Verify(c => c.CreateUserBackupConfigAsync(), Times.Never);
    }

    [Fact]
    public async Task RunDeferredInitializationAsync_WhenBackupNotCompleted_CreatesBackup()
    {
        _preferencesService.Setup(p => p.GetPreference(
            UserPreferenceKeys.InitialConfigBackupCompleted, false))
            .Returns(false);
        _configurationService.Setup(c => c.CreateUserBackupConfigAsync())
            .Returns(Task.CompletedTask);

        var sut = CreateSut();
        var (statusProgress, detailedProgress, _) = CreateProgressTracking();

        await sut.RunDeferredInitializationAsync();

        _configurationService.Verify(c => c.CreateUserBackupConfigAsync(), Times.Once);
    }

    [Fact]
    public async Task RunDeferredInitializationAsync_WhenBackupSucceeds_SetsPreferenceToTrue()
    {
        _preferencesService.Setup(p => p.GetPreference(
            UserPreferenceKeys.InitialConfigBackupCompleted, false))
            .Returns(false);
        _configurationService.Setup(c => c.CreateUserBackupConfigAsync())
            .Returns(Task.CompletedTask);

        var sut = CreateSut();
        var (statusProgress, detailedProgress, _) = CreateProgressTracking();

        await sut.RunDeferredInitializationAsync();

        _preferencesService.Verify(p => p.SetPreferenceAsync(
            UserPreferenceKeys.InitialConfigBackupCompleted, true), Times.Once);
    }

    [Fact]
    public async Task RunDeferredInitializationAsync_BackupFailureStillReturnsAndReportsFailure()
    {
        _preferencesService.Setup(p => p.GetPreference(
            UserPreferenceKeys.InitialConfigBackupCompleted, false))
            .Returns(false);
        _configurationService.Setup(c => c.CreateUserBackupConfigAsync())
            .ThrowsAsync(new InvalidOperationException("Backup failed"));

        var sut = CreateSut();
        var (statusProgress, detailedProgress, _) = CreateProgressTracking();

        var result = await sut.RunDeferredInitializationAsync();

        result.Should().NotBeNull();
        _logService.Verify(l => l.LogWarning(It.Is<string>(s =>
            s.Contains("User backup config failed"))), Times.Once);
    }

    // --- IsFirstLaunch ---

    [Fact]
    public async Task RunDeferredInitializationAsync_WhenBackupAlreadyCompleted_IsFirstLaunchIsFalse()
    {
        _preferencesService.Setup(p => p.GetPreference(
            UserPreferenceKeys.InitialConfigBackupCompleted, false))
            .Returns(true);

        var sut = CreateSut();
        var (statusProgress, detailedProgress, _) = CreateProgressTracking();

        var result = await sut.RunDeferredInitializationAsync();

        result.IsFirstLaunch.Should().BeFalse();
    }

    [Fact]
    public async Task RunDeferredInitializationAsync_WhenBackupNotCompletedAndSucceeds_IsFirstLaunchIsTrue()
    {
        _preferencesService.Setup(p => p.GetPreference(
            UserPreferenceKeys.InitialConfigBackupCompleted, false))
            .Returns(false);
        _configurationService.Setup(c => c.CreateUserBackupConfigAsync())
            .Returns(Task.CompletedTask);

        var sut = CreateSut();
        var (statusProgress, detailedProgress, _) = CreateProgressTracking();

        var result = await sut.RunDeferredInitializationAsync();

        result.IsFirstLaunch.Should().BeTrue();
    }

    [Fact]
    public async Task RunStartupSequenceAsync_ReportsOnlyRequiredInitializationBeforeReadiness()
    {
        var reports = new List<string>();
        var status = new Mock<IProgress<string>>();
        status.Setup(progress => progress.Report(It.IsAny<string>())).Callback<string>(reports.Add);
        await CreateSut().RunStartupSequenceAsync(status.Object, Mock.Of<IProgress<TaskProgressDetail>>());
        reports.Should().Equal("Loading_InitializingSettings", "Loading_PreparingApp");
    }

    // --- Return value ---

    [Fact]
    public async Task RunStartupSequenceAsync_ReturnsStartupResult()
    {
        var sut = CreateSut();
        var (statusProgress, detailedProgress, _) = CreateProgressTracking();

        var result = await sut.RunStartupSequenceAsync(statusProgress, detailedProgress);

        result.Should().NotBeNull();
        result.Should().BeOfType<StartupResult>();
    }

    // --- Full sequence execution ---

    [Fact]
    public async Task RunStartupSequenceAsync_ExecutesAllPhasesInOrder()
    {
        _preferencesService.Setup(p => p.GetPreference(
            UserPreferenceKeys.InitialConfigBackupCompleted, false))
            .Returns(false);

        var callOrder = new List<string>();
        _settingsRegistry.Setup(r => r.InitializeAsync())
            .Callback(() => callOrder.Add("Phase1_Registry"))
            .Returns(Task.CompletedTask);
        _settingsPreloader.Setup(p => p.PreloadAllSettingsAsync())
            .Callback(() => callOrder.Add("Phase1_Preload"))
            .Returns(Task.CompletedTask);
        _configurationService.Setup(c => c.CreateUserBackupConfigAsync())
            .Callback(() => callOrder.Add("Phase2_Backup"))
            .Returns(Task.CompletedTask);

        var sut = CreateSut();
        var (statusProgress, detailedProgress, _) = CreateProgressTracking();

        await sut.RunStartupSequenceAsync(statusProgress, detailedProgress);
        callOrder.Should().Equal("Phase1_Registry", "Phase1_Preload");
        await sut.RunDeferredInitializationAsync();
        callOrder.Should().Equal(
            "Phase1_Registry",
            "Phase1_Preload",
            "Phase2_Backup");
    }

    // --- Resilience: All phases fail gracefully ---

    [Fact]
    public async Task RunStartupSequenceAsync_WhenAllPhasesFail_StillReturnsResult()
    {
        _settingsRegistry.Setup(r => r.InitializeAsync())
            .ThrowsAsync(new Exception("Phase 1 fail"));
        _preferencesService.Setup(p => p.GetPreference(
            UserPreferenceKeys.InitialConfigBackupCompleted, false))
            .Returns(false);
        _configurationService.Setup(c => c.CreateUserBackupConfigAsync())
            .ThrowsAsync(new Exception("Phase 2 fail"));

        var sut = CreateSut();
        var (statusProgress, detailedProgress, _) = CreateProgressTracking();

        var result = await sut.RunStartupSequenceAsync(statusProgress, detailedProgress);
        var deferred = await sut.RunDeferredInitializationAsync();

        result.Should().NotBeNull();
        deferred.Should().NotBeNull();
    }
}
