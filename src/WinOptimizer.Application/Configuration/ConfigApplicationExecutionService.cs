using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using System.Threading;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.SoftwareApps.Models;

namespace WinOptimizer.Application.Configuration;

/// <summary>Owns ConfigApplicationExecution workflows through application boundaries.</summary>
public class ConfigApplicationExecutionService : IConfigApplicationExecutionService
{
    private readonly ILogService _logService;
    private readonly IDialogService _dialogService;
    private readonly ILocalizationService _localizationService;
    private readonly IWindowsVersionService _windowsVersionService;
    private readonly IConfigurationApplicationBridgeService _bridgeService;
    private readonly IWindowsUIManagementService _windowsUIManagementService;
    private readonly IProcessExecutor _processExecutor;
    private readonly IConfigImportOverlayService _overlayService;
    private readonly IConfigImportState _configImportState;
    private readonly IConfigAppSelectionService _configAppSelectionService;
    private readonly IConfigLoadService _configLoadService;
    private readonly IReviewModeViewModelCoordinator _vmCoordinator;
    private readonly IPolicyCleanupService _policyCleanupService;
    private readonly IChangeHistoryService _changeHistoryService;
    private readonly IUninstallResidueReview _uninstallReview;
    private readonly IApplicationModeService _applicationModeService;

    public ConfigApplicationExecutionService(
        ILogService logService,
        IDialogService dialogService,
        ILocalizationService localizationService,
        IWindowsVersionService windowsVersionService,
        IConfigurationApplicationBridgeService bridgeService,
        IWindowsUIManagementService windowsUIManagementService,
        IProcessExecutor processExecutor,
        IConfigImportOverlayService overlayService,
        IConfigImportState configImportState,
        IConfigAppSelectionService configAppSelectionService,
        IConfigLoadService configLoadService,
        IReviewModeViewModelCoordinator vmCoordinator,
        IPolicyCleanupService policyCleanupService,
        IChangeHistoryService changeHistoryService,
        IApplicationModeService applicationModeService,
        IUninstallResidueReview uninstallReview)
    {
        _logService = logService;
        _dialogService = dialogService;
        _localizationService = localizationService;
        _windowsVersionService = windowsVersionService;
        _bridgeService = bridgeService;
        _windowsUIManagementService = windowsUIManagementService;
        _processExecutor = processExecutor;
        _overlayService = overlayService;
        _configImportState = configImportState;
        _configAppSelectionService = configAppSelectionService;
        _configLoadService = configLoadService;
        _vmCoordinator = vmCoordinator;
        _policyCleanupService = policyCleanupService;
        _changeHistoryService = changeHistoryService;
        _uninstallReview = uninstallReview;
        _applicationModeService = applicationModeService;
    }

    public async Task ExecuteConfigImportAsync(UnifiedConfigurationFile config, ImportOptions dialogOptions)
    {
        using var logTopic = _logService.BeginTopic(LogTopic.Configuration);
        if (_applicationModeService.CurrentMode == WinOptimizerMode.Builder)
        {
            await _dialogService.ShowInformationAsync(_localizationService.GetString("TrialMode_Blocked"),
                _localizationService.GetString("TrialMode_Label"));
            return;
        }
        using var operation = _applicationModeService.BeginSystemOperation();
        await using var deferredReviews = _uninstallReview.Defer();
        try
        {
            var incompatibleSettings = _configLoadService.DetectIncompatibleSettings(config);

            if (incompatibleSettings.Count != 0)
            {
                config = _configLoadService.FilterConfigForCurrentSystem(config);
                _logService.Log(LogLevel.Info, $"Silently filtered {incompatibleSettings.Count} incompatible settings from config");
            }

            // Build selected sections from what's available in the config
            var selectedSections = new List<string>();

            bool hasWindowsApps = config.WindowsApps.Items.Count > 0;
            bool hasExternalApps = config.ExternalApps.Items.Count > 0;

            if (hasWindowsApps) selectedSections.Add("WindowsApps");
            if (hasExternalApps) selectedSections.Add("ExternalApps");

            foreach (var feature in config.Optimize.Features)
            {
                if (feature.Value.Items.Count > 0)
                {
                    if (!selectedSections.Contains("Optimize")) selectedSections.Add("Optimize");
                    selectedSections.Add($"Optimize_{feature.Key}");
                }
            }

            foreach (var feature in config.Customize.Features)
            {
                if (feature.Value.Items.Count > 0)
                {
                    if (!selectedSections.Contains("Customize")) selectedSections.Add("Customize");
                    selectedSections.Add($"Customize_{feature.Key}");
                }
            }

            // Pre-select Windows Apps from config
            bool saveRemovalScripts = true;
            if (hasWindowsApps)
            {
                await _configAppSelectionService.SelectWindowsAppsFromConfigAsync(config.WindowsApps);

                // Only confirm removal when uninstall is selected
                if (dialogOptions.ProcessWindowsAppsRemoval)
                {
                    var (shouldContinue, saveScripts) = await _configAppSelectionService.ConfirmWindowsAppsRemovalAsync();
                    saveRemovalScripts = saveScripts;
                    if (!shouldContinue)
                    {
                        await _configAppSelectionService.ClearWindowsAppsSelectionAsync();
                        selectedSections.Remove("WindowsApps");
                        _logService.Log(LogLevel.Info, "User cancelled Windows Apps removal");
                    }
                }
            }

            // Pre-select External Apps from config
            if (hasExternalApps)
            {
                await _configAppSelectionService.SelectExternalAppsFromConfigAsync(config.ExternalApps);
            }

            // Use the user's dialog choices, validated against config availability
            var importOptions = new ImportOptions
            {
                ProcessWindowsAppsRemoval = hasWindowsApps && selectedSections.Contains("WindowsApps") && dialogOptions.ProcessWindowsAppsRemoval,
                SaveRemovalScripts = saveRemovalScripts,
                ProcessExternalAppsRemoval = hasExternalApps && dialogOptions.ProcessExternalAppsRemoval,
                ApplyThemeWallpaper = dialogOptions.ApplyThemeWallpaper,
                ApplyCleanTaskbar = dialogOptions.ApplyCleanTaskbar,
                ApplyCleanStartMenu = dialogOptions.ApplyCleanStartMenu,
                IsWindowsDefaults = dialogOptions.IsWindowsDefaults,
            };

            // Add action-only subsections for Customize actions that the user enabled
            importOptions = ConfigActionSelection.IncludeSections(selectedSections, importOptions);

            if (selectedSections.Count == 0 && !importOptions.ApplyThemeWallpaper)
            {
                _dialogService.ShowMessage(
                    _localizationService.GetString("Config_Import_Error_NoSelection") ?? "No changes to apply.",
                    _localizationService.GetString("Config_Import_Error_NoSelection_Title") ?? "No Changes");
                return;
            }

            // Show overlay during config application
            var overlayStatus = _localizationService.GetString("Config_Import_Status_Applying")
                ?? "Sit back, relax and watch while win-optimizer enhances Windows with your desired settings...";
            _overlayService.ShowOverlay(overlayStatus);

            _configImportState.IsActive = true;
            _configImportState.ImportSuppliesPowerValues = false;
            var changeBatch = _changeHistoryService.BeginBatch(BuildImportBatchHeader());

            try
            {
                await ApplyConfigurationWithOptionsAsync(config, selectedSections, importOptions);
            }
            finally
            {
                _configImportState.IsActive = false;
                _configImportState.ImportSuppliesPowerValues = false;
                changeBatch.Dispose();
                _overlayService.HideOverlay();
            }

            // Show success message and wait for user dismissal
            await ShowImportSuccessMessage();

            // Process External Apps AFTER success dialog dismissal (needs UI thread)
            if (hasExternalApps && importOptions.ProcessExternalAppsRemoval)
            {
                await _configAppSelectionService.ProcessExternalAppsRemovalAsync(config.ExternalApps);
            }
        }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Error, $"Error importing configuration: {ex.Message}");
            _overlayService.HideOverlay();
            _dialogService.ShowMessage($"Error importing configuration: {ex.Message}", "Error");
        }
    }

    public async Task ApplyConfigurationWithOptionsAsync(
        UnifiedConfigurationFile config,
        List<string> selectedSections,
        ImportOptions options)
    {
        _logService.Log(LogLevel.Info, $"Applying configuration to: {string.Join(", ", selectedSections)}");

        bool shouldRemoveApps = selectedSections.Contains("WindowsApps") && options.ProcessWindowsAppsRemoval;
        bool hasOptimize = selectedSections.Any(s => s == "Optimize" || s.StartsWith("Optimize_"));
        bool hasCustomize = selectedSections.Any(s => s == "Customize" || s.StartsWith("Customize_"));

        var parallelTasks = new List<Task>();
        Task<OperationResult<AppUninstallResult>>? removalTask = null;
        Task<(bool Success, bool HasWork)>? settingsTask = null;

        // Branch 1: Bloat removal (no Task.Run - ViewModel needs UI thread for property change notifications)
        if (shouldRemoveApps)
        {
            _logService.Log(LogLevel.Info, "Processing Windows Apps removal (parallel branch)");
            removalTask = _vmCoordinator.RemoveWindowsAppsAsync(skipConfirmation: true, saveRemovalScripts: options.SaveRemovalScripts);
            parallelTasks.Add(removalTask);
        }

        // Branch 2: All settings (Optimize + Customize in parallel within)
        if (hasOptimize || hasCustomize)
        {
            settingsTask = ApplyAllSettingsGroupsAsync(config, selectedSections, options, hasOptimize, hasCustomize);
            parallelTasks.Add(settingsTask);
        }

        await Task.WhenAll(parallelTasks);
        var settings = settingsTask == null ? (Success: true, HasWork: false) : await settingsTask;
        var removedAnyApps = false;
        List<string> errors = [];
        if (!settings.Success)
            errors.Add("One or more configuration settings failed. Review the log and refresh the affected settings before retrying.");
        if (removalTask != null)
        {
            var removal = await removalTask;
            if (!removal.Success || removal.Result == null)
                errors.Add(removal.ErrorMessage ?? "Windows app removal did not complete.");
            else
            {
                removedAnyApps = removal.Result.Applications.Any(app => app.State == UninstallVerificationState.Removed);
                errors.AddRange(removal.Result.Applications
                    .Where(app => app.State != UninstallVerificationState.Removed)
                    .Select(app => $"{app.AppName}: {app.Detail}"));
            }
        }
        if (errors.Count != 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));

        // Defaults can write disabled policy values during import. Clear them before refreshing the shell.
        if (options.IsWindowsDefaults)
            await Task.Run(() => _policyCleanupService.CleanupPolicyValues());

        // A wallpaper-only request reads the active theme after the selected settings finish.
        if (options.ApplyThemeWallpaper && !IncludesThemeSelection(config, selectedSections))
            await _bridgeService.ApplyDefaultWallpaperForCurrentThemeAsync();
        if (!settings.HasWork && !removedAnyApps) return;

        // Refresh the shell after applying settings or removing apps.
        _overlayService.UpdateStatus(
            _localizationService.GetString("Config_Import_Status_Applying")
                ?? "Sit back, relax and watch while win-optimizer enhances Windows with your desired settings...",
            _localizationService.GetString("Config_Import_Status_RestartingExplorer")
                ?? "Restarting Explorer...");
        await Task.Run(async () =>
        {
            // Broadcast regional setting change so apps pick up intl changes from import
            _windowsUIManagementService.BroadcastRegionalSettingChange();

            await RestartExplorerAsync();
        });
    }

    private static bool IncludesThemeSelection(UnifiedConfigurationFile config, List<string> selectedSections) =>
        selectedSections.Contains($"Customize_{FeatureIds.WindowsTheme}") &&
        config.Customize.Features.TryGetValue(FeatureIds.WindowsTheme, out var section) &&
        section.Items.Any(item => item.Id == SettingIds.ThemeModeWindows);

    private async Task<(bool Success, bool HasWork)> ApplyAllSettingsGroupsAsync(
        UnifiedConfigurationFile config,
        List<string> selectedSections,
        ImportOptions options,
        bool hasOptimize,
        bool hasCustomize)
    {
        // Count total features across both groups for progress reporting
        int totalFeatures = 0;
        if (hasOptimize && config.Optimize?.Features != null)
        {
            totalFeatures += config.Optimize.Features
                .Count(f => selectedSections.Contains($"Optimize_{f.Key}"));
        }
        if (hasCustomize && config.Customize?.Features != null)
        {
            totalFeatures += config.Customize.Features
                .Count(f => selectedSections.Contains($"Customize_{f.Key}"));
        }
        // Count action-only subsections that aren't already in the feature groups
        var actionOnlyExtras = options.ActionOnlySubsections
            .Where(s =>
            {
                if (s.StartsWith("Optimize_"))
                {
                    var featureName = s.Substring("Optimize_".Length);
                    return config.Optimize?.Features?.ContainsKey(featureName) != true;
                }
                if (s.StartsWith("Customize_"))
                {
                    var featureName = s.Substring("Customize_".Length);
                    return config.Customize?.Features?.ContainsKey(featureName) != true;
                }
                return false;
            })
            .ToList();
        totalFeatures += actionOnlyExtras.Count;

        if (totalFeatures == 0)
            totalFeatures = 1; // Avoid division by zero in display

        int completedFeatures = 0;

        var statusText = _localizationService.GetString("Config_Import_Status_Applying")
            ?? "Sit back, relax and watch while win-optimizer enhances Windows with your desired settings...";
        _overlayService.UpdateStatus(statusText, $"0/{totalFeatures} features processed");

        Action<string> onFeatureCompleted = featureName =>
        {
            var completed = Interlocked.Increment(ref completedFeatures);
            _overlayService.UpdateStatus(statusText, $"{completed}/{totalFeatures} features processed");
            _logService.Log(LogLevel.Info, $"Feature completed: {featureName} ({completed}/{totalFeatures})");
        };

        var groupTasks = new List<Task<(bool Success, bool HasWork)>>();

        if (hasOptimize)
        {
            groupTasks.Add(Task.Run(async () =>
            {
                var result = await ApplyFeatureGroupWithOptionsAsync(
                    config.Optimize!, "Optimize", options, selectedSections, onFeatureCompleted);
                _logService.Log(LogLevel.Info, $"  Optimize group: {(result.Success ? "Success" : "Failed")}");
                return result;
            }));
        }

        if (hasCustomize)
        {
            groupTasks.Add(Task.Run(async () =>
            {
                var result = await ApplyFeatureGroupWithOptionsAsync(
                    config.Customize!, "Customize", options, selectedSections, onFeatureCompleted);
                _logService.Log(LogLevel.Info, $"  Customize group: {(result.Success ? "Success" : "Failed")}");
                return result;
            }));
        }

        var results = await Task.WhenAll(groupTasks);
        return (results.All(result => result.Success), results.Any(result => result.HasWork));
    }

    /// <summary>Applies the selected sections while keeping cleanup consent independent of imported records.</summary>
    private async Task<(bool Success, bool HasWork)> ApplyFeatureGroupWithOptionsAsync(
        FeatureGroupSection? featureGroup,
        string groupName,
        ImportOptions options,
        List<string> selectedSections,
        Action<string> onFeatureCompleted)
    {
        var prefix = groupName + "_";
        var featureTasks = new List<Task<bool>>();
        var hasWork = false;

        foreach (var featureKey in selectedSections.Where(section => section.StartsWith(prefix, StringComparison.Ordinal)).Distinct())
        {
            var featureName = featureKey[prefix.Length..];
            var actionOnly = options.ActionOnlySubsections.Contains(featureKey);
            var configSection = featureGroup?.Features.GetValueOrDefault(featureName);
            if (configSection == null && !actionOnly) continue;

            var actions = BuildActionItems(options, featureName);
            var settings = actionOnly || configSection == null ? [] : configSection.Items
                .Where(item => item.Id is not (SettingIds.TaskbarClean or SettingIds.StartMenuCleanWin10 or SettingIds.StartMenuCleanWin11))
                .ToArray();
            hasWork |= actions.Count != 0 || settings.Length != 0;

            featureTasks.Add(Task.Run(async () =>
            {
                var success = true;
                var sectionName = $"{groupName}.{featureName}";
                if (actions.Count != 0)
                    success = await ApplyItemsAsync(actions, included: true);
                if (settings.Length != 0)
                    success = await ApplyItemsAsync(settings, configSection!.IsIncluded) && success;

                onFeatureCompleted(featureName);
                return success;

                Task<bool> ApplyItemsAsync(IReadOnlyList<ConfigurationItem> items, bool included) =>
                    _bridgeService.ApplyConfigurationSectionAsync(
                        new ConfigSection { IsIncluded = included, Items = items }, sectionName,
                        (settingId, _, _) => Task.FromResult((true,
                            settingId != SettingIds.ThemeModeWindows || options.ApplyThemeWallpaper)));
            }));
        }

        if (featureTasks.Count == 0)
        {
            _logService.Log(LogLevel.Warning, $"{groupName} has no features to apply");
            return (false, false);
        }

        var results = await Task.WhenAll(featureTasks);
        return (results.All(result => result), hasWork);
    }

    private List<ConfigurationItem> BuildActionItems(ImportOptions options, string featureName)
    {
        var items = new List<ConfigurationItem>();

        if (options.ApplyCleanTaskbar && featureName == FeatureIds.Taskbar)
        {
            items.Add(new ConfigurationItem
            {
                Id = SettingIds.TaskbarClean,
                Name = "Clean Taskbar",
                IsSelected = true,
                InputType = InputType.Action
            });
        }

        if (options.ApplyCleanStartMenu && featureName == FeatureIds.StartMenu)
        {
            var settingId = _windowsVersionService.IsWindows11() ? SettingIds.StartMenuCleanWin11 : SettingIds.StartMenuCleanWin10;
            items.Add(new ConfigurationItem
            {
                Id = settingId,
                Name = "Clean Start Menu",
                IsSelected = true,
                InputType = InputType.Action
            });
        }

        return items;
    }

    private string BuildImportBatchHeader()
    {
        var label = _localizationService.GetString("ChangeHistory_ConfigImport");
        var source = _configImportState.SourceName;
        return string.IsNullOrEmpty(source) ? label : $"{label} ({source})";
    }

    private async Task ShowImportSuccessMessage()
    {
        await _dialogService.ShowInformationAsync(
            _localizationService.GetString("Config_Import_Success_Message") ?? "Configuration imported successfully.",
            _localizationService.GetString("Config_Import_Success_Title") ?? "Import Successful");
    }

    private async Task RestartExplorerAsync()
    {
        const string restartError = "The changes were applied, but Windows Explorer could not be restarted.";
        try
        {
            if (_windowsUIManagementService.IsProcessRunning("explorer"))
            {
                _logService.Log(LogLevel.Info, "Stopping Explorer to apply changes");
                _windowsUIManagementService.KillProcess("explorer", waitForExit: true);

                for (int attempt = 0; attempt < 20; attempt++)
                {
                    if (_windowsUIManagementService.IsProcessRunning("explorer")) return;
                    await Task.Delay(250).ConfigureAwait(false);
                }
            }

            _logService.Log(LogLevel.Info, "Starting Explorer");
            var launch = await _processExecutor.ShellExecuteAsync("explorer.exe").ConfigureAwait(false);
            if (launch == 0 && await VerifyManualExplorerStartAsync(
                    () => _windowsUIManagementService.IsProcessRunning("explorer"), TimeProvider.System).ConfigureAwait(false))
                return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException($"{restartError} {ex.Message}", ex);
        }

        throw new InvalidOperationException(restartError);
    }

    /// <summary>Checks a completed launch immediately and retains the full verification window for slow starts.</summary>
    internal static async Task<bool> VerifyManualExplorerStartAsync(Func<bool> isRunning, TimeProvider clock)
    {
        if (isRunning()) return true;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), clock).ConfigureAwait(false);
            if (isRunning()) return true;
        }
        return false;
    }
}
