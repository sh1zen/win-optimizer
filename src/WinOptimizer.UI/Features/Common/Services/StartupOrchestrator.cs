using System;
using System.Threading.Tasks;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Extensions;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Common.Services;
using WinOptimizer.Infrastructure.Features.Common.EventHandlers;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Utilities;

namespace WinOptimizer.UI.Features.Common.Services;

/// <summary>
/// Initializes the shell, then explicitly loads inventory and the initial backup after presentation.
/// Extracted from MainWindow.xaml.cs for testability.
/// </summary>
public class StartupOrchestrator : IStartupOrchestrator
{
    private readonly ICompatibleSettingsRegistry _settingsRegistry;
    private readonly IGlobalSettingsPreloader _settingsPreloader;
    private readonly TooltipRefreshEventHandler _tooltipEventHandler;
    private readonly IUserPreferencesService _preferencesService;
    private readonly IConfigurationService _configurationService;
    private readonly ILogService _logService;
    private readonly IWindowsAppsItemsProvider _windowsApps;

    public StartupOrchestrator(
        ICompatibleSettingsRegistry settingsRegistry,
        IGlobalSettingsPreloader settingsPreloader,
        TooltipRefreshEventHandler tooltipEventHandler,
        IUserPreferencesService preferencesService,
        IConfigurationService configurationService,
        ILogService logService,
        IWindowsAppsItemsProvider windowsApps)
    {
        _settingsRegistry = settingsRegistry;
        _settingsPreloader = settingsPreloader;
        _tooltipEventHandler = tooltipEventHandler;
        _preferencesService = preferencesService;
        _configurationService = configurationService;
        _logService = logService;
        _windowsApps = windowsApps;
    }

    /// <inheritdoc />
    public async Task<StartupResult> RunStartupSequenceAsync(
        IProgress<string> statusProgress,
        IProgress<TaskProgressDetail> detailedProgress)
    {
        using var logTopic = _logService.BeginTopic(LogTopic.Startup);
        // 1. Initialize settings registry
        statusProgress.Report("Loading_InitializingSettings");
        StartupLogger.Log("StartupOrchestrator", "Phase 1: Initializing settings registry...");
        try
        {
            await _settingsRegistry.InitializeAsync().ConfigureAwait(false);
            await _settingsPreloader.PreloadAllSettingsAsync().ConfigureAwait(false);
            StartupLogger.Log("StartupOrchestrator", "Phase 1: Settings registry initialized");

            // Initialize tooltip event handler (constructor subscribes to EventBus)
            // Accessing the injected instance ensures it's constructed and subscribed.
            _ = _tooltipEventHandler;

            // Pre-cache regedit icon for Technical Details panel
            RegeditIconProvider.GetIconAsync().FireAndForget(_logService);
        }
        catch (Exception ex)
        {
            StartupLogger.Log("StartupOrchestrator", $"Phase 1: Settings registry FAILED: {ex.Message}");
            _logService.LogWarning($"Failed to initialize settings registry: {ex.Message}");
        }

        statusProgress.Report("Loading_PreparingApp");
        StartupLogger.Log("StartupOrchestrator", "Shell initialization complete");
        return new StartupResult();
    }

    private readonly object _deferredGate = new();
    private Task<StartupResult>? _deferredInitialization;

    /// <summary>Shares deferred inventory and backup work across callers without doing work at construction.</summary>
    public Task<StartupResult> RunDeferredInitializationAsync()
    {
        lock (_deferredGate)
            return _deferredInitialization ??= RunDeferredInitializationCoreAsync();
    }

    /// <summary>Captures the full backup only after installation evidence is available.</summary>
    private async Task<StartupResult> RunDeferredInitializationCoreAsync()
    {
        using var logTopic = _logService.BeginTopic(LogTopic.Startup);
        await LoadWindowsAppsAsync().ConfigureAwait(false);
        var isFirstLaunch = false;

        // First-run backup includes the verified capability and feature inventory.
        try
        {
            var backupCompleted = _preferencesService.GetPreference(
                UserPreferenceKeys.InitialConfigBackupCompleted, false);
            if (!backupCompleted)
            {
                StartupLogger.Log("StartupOrchestrator", "Phase 2: Creating user backup config...");

                var backupTask = _configurationService.CreateUserBackupConfigAsync();
                var completed = await Task.WhenAny(
                    backupTask, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false);

                if (completed == backupTask)
                {
                    await backupTask; // observe exceptions
                    await _preferencesService.SetPreferenceAsync(
                        UserPreferenceKeys.InitialConfigBackupCompleted, true);
                    isFirstLaunch = true;
                    StartupLogger.Log("StartupOrchestrator", "Phase 2: User backup config done");
                }
                else
                {
                    StartupLogger.Log("StartupOrchestrator",
                        "Phase 2: User backup config TIMED OUT (will retry next launch)");
                    _logService.LogWarning(
                        "User backup config timed out after 30s — will retry next launch");
                }
            }
            else
            {
                StartupLogger.Log("StartupOrchestrator", "Phase 2: User backup config already completed");
            }
        }
        catch (Exception ex)
        {
            StartupLogger.Log("StartupOrchestrator", $"Phase 2: User backup config FAILED: {ex.Message}");
            _logService.LogWarning($"User backup config failed: {ex.Message}");
        }

        StartupLogger.Log("StartupOrchestrator", "Deferred initialization complete");

        return new StartupResult { IsFirstLaunch = isFirstLaunch };
    }

    /// <summary>Loads inventory after presentation while preserving failure reporting.</summary>
    private async Task LoadWindowsAppsAsync()
    {
        try
        {
            StartupLogger.Log("StartupOrchestrator", "Windows Apps initialization starting...");
            await _windowsApps.LoadItemsAsync().ConfigureAwait(false);
            StartupLogger.Log("StartupOrchestrator", "Windows Apps initialization complete");
        }
        catch (Exception ex)
        {
            _logService.LogWarning($"Windows Apps initialization failed: {ex.Message}");
        }
    }

}
