using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Events;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Application.Configuration;

/// <summary>Owns ConfigReviewOrchestration workflows through application boundaries.</summary>
public class ConfigReviewOrchestrationService : IConfigReviewOrchestrationService, IDisposable
{
    private bool _disposed;
    private readonly ILogService _logService;
    private readonly IDialogService _dialogService;
    private readonly ILocalizationService _localizationService;
    private readonly IConfigReviewModeService _configReviewModeService;
    private readonly IConfigReviewDiffService _configReviewDiffService;
    private readonly IConfigImportOverlayService _overlayService;
    private readonly IConfigImportState _configImportState;
    private readonly IConfigAppSelectionService _configAppSelectionService;
    private readonly IConfigApplicationExecutionService _configExecutionService;
    private readonly IConfigLoadService _configLoadService;
    private readonly ICompatibleSettingsRegistry _compatibleSettingsRegistry;
    private readonly IEventBus _eventBus;
    private readonly IReviewModeViewModelCoordinator _vmCoordinator;
    private readonly IPolicyCleanupService _policyCleanupService;
    private readonly IChangeHistoryService _changeHistoryService;
    private readonly IUninstallResidueReview _uninstallReview;

    public ConfigReviewOrchestrationService(
        ILogService logService,
        IDialogService dialogService,
        ILocalizationService localizationService,
        IConfigReviewModeService configReviewModeService,
        IConfigReviewDiffService configReviewDiffService,
        IConfigImportOverlayService overlayService,
        IConfigImportState configImportState,
        IConfigAppSelectionService configAppSelectionService,
        IConfigApplicationExecutionService configExecutionService,
        IConfigLoadService configLoadService,
        ICompatibleSettingsRegistry compatibleSettingsRegistry,
        IEventBus eventBus,
        IReviewModeViewModelCoordinator vmCoordinator,
        IPolicyCleanupService policyCleanupService,
        IChangeHistoryService changeHistoryService,
        IUninstallResidueReview uninstallReview)
    {
        _logService = logService;
        _dialogService = dialogService;
        _localizationService = localizationService;
        _configReviewModeService = configReviewModeService;
        _configReviewDiffService = configReviewDiffService;
        _overlayService = overlayService;
        _configImportState = configImportState;
        _configAppSelectionService = configAppSelectionService;
        _configExecutionService = configExecutionService;
        _configLoadService = configLoadService;
        _compatibleSettingsRegistry = compatibleSettingsRegistry;
        _eventBus = eventBus;
        _vmCoordinator = vmCoordinator;
        _policyCleanupService = policyCleanupService;
        _changeHistoryService = changeHistoryService;
        _uninstallReview = uninstallReview;

        // Listen for review mode exit to clear review state from all loaded settings
        _configReviewModeService.ReviewModeChanged += OnReviewModeChanged;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _configReviewModeService.ReviewModeChanged -= OnReviewModeChanged;
    }

    private void OnReviewModeChanged(object? sender, EventArgs e)
    {
        if (_configReviewModeService.IsInReviewMode)
        {
            // Review mode was entered - reapply diffs to any already-loaded singleton VMs
            _vmCoordinator.ReapplyReviewDiffsToExistingSettings();
            return;
        }

        // Review mode was exited - clear review state from all loaded SettingItemViewModels
        ClearReviewStateFromAllSettings();
    }

    private void ClearReviewStateFromAllSettings()
    {
        _eventBus.Publish(new ReviewModeExitedEvent());
        _logService.Log(LogLevel.Info, "Published ReviewModeExitedEvent to clear review state from all loaded settings");
    }


    public async Task EnterReviewModeAsync(UnifiedConfigurationFile config, bool isWindowsDefaults = false)
    {
        try
        {
            // Filter incompatible settings
            var incompatibleSettings = _configLoadService.DetectIncompatibleSettings(config);
            if (incompatibleSettings.Any())
            {
                config = _configLoadService.FilterConfigForCurrentSystem(config);
                _logService.Log(LogLevel.Info, $"Silently filtered {incompatibleSettings.Count} incompatible settings from config");
            }

            // Force filter ON before computing diffs so version-filtered settings
            // don't generate phantom diffs when the user had the filter toggled off
            _compatibleSettingsRegistry.SetFilterEnabled(true);

            // Enter review mode on the service (eagerly computes diffs and fires events)
            await _configReviewModeService.EnterReviewModeAsync(config, isWindowsDefaults);

            // Pre-select Windows Apps from config
            if (config.WindowsApps.Items.Count > 0)
            {
                await _configAppSelectionService.SelectWindowsAppsFromConfigAsync(config.WindowsApps);
                _logService.Log(LogLevel.Info, $"Pre-selected {config.WindowsApps.Items.Count} Windows Apps for review");
            }

            // Pre-select External Apps from config
            if (config.ExternalApps.Items.Count > 0)
            {
                await _configAppSelectionService.SelectExternalAppsFromConfigAsync(config.ExternalApps);
                _logService.Log(LogLevel.Info, $"Pre-selected {config.ExternalApps.Items.Count} External Apps for review");
            }

            _logService.Log(LogLevel.Info, "Review mode activated - user can now navigate and review changes");
        }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Error, $"Error entering review mode: {ex.Message}");
            _configReviewModeService.ExitReviewMode();
            _dialogService.ShowMessage($"Error entering review mode: {ex.Message}", "Error");
        }
    }

    public async Task ApplyReviewedConfigAsync()
    {
        if (!_configReviewModeService.IsInReviewMode || _configReviewModeService.ActiveConfig == null)
        {
            _logService.Log(LogLevel.Warning, "ApplyReviewedConfigAsync called but not in review mode");
            return;
        }

        await using var deferredReviews = _uninstallReview.Defer();
        var config = _configReviewModeService.ActiveConfig;
        var approvedDiffs = _configReviewDiffService.GetApprovedDiffs();

        try
        {
            // Build the list of sections that have approved changes
            var selectedSections = new List<string>();

            // Check if any Windows Apps are selected and determine action
            bool hasWindowsApps = _vmCoordinator.HasSelectedWindowsApps;
            bool windowsAppsRemove = _vmCoordinator.IsWindowsAppsRemoveAction;
            if (hasWindowsApps && windowsAppsRemove)
                selectedSections.Add("WindowsApps");

            // Check if any External Apps are selected and determine action
            bool hasExternalApps = _vmCoordinator.HasSelectedExternalApps;
            bool externalAppsRemove = _vmCoordinator.IsExternalAppsRemoveAction;
            if (hasExternalApps && externalAppsRemove)
                selectedSections.Add("ExternalApps");

            // Check approved Optimize/Customize diffs (including action settings)
            var approvedSettingIds = new HashSet<string>(approvedDiffs.Select(d => d.SettingId));
            var approvedActionSettingIds = new HashSet<string>(
                approvedDiffs.Where(d => d.IsActionSetting).Select(d => d.SettingId));

            if (config.Optimize.Features.Any(f => f.Value.Items.Any(i => approvedSettingIds.Contains(i.Id))))
            {
                selectedSections.Add("Optimize");
                foreach (var feature in config.Optimize.Features)
                {
                    if (feature.Value.Items.Any(i => approvedSettingIds.Contains(i.Id)))
                        selectedSections.Add($"Optimize_{feature.Key}");
                }
            }

            if (config.Customize.Features.Any(f => f.Value.Items.Any(i => approvedSettingIds.Contains(i.Id))))
            {
                selectedSections.Add("Customize");
                foreach (var feature in config.Customize.Features)
                {
                    if (feature.Value.Items.Any(i => approvedSettingIds.Contains(i.Id)))
                        selectedSections.Add($"Customize_{feature.Key}");
                }
            }

            // Build import options based on action choices
            var importOptions = new ImportOptions
            {
                ProcessWindowsAppsRemoval = hasWindowsApps && windowsAppsRemove,
                ProcessExternalAppsRemoval = hasExternalApps && externalAppsRemove,
                ApplyThemeWallpaper = approvedDiffs.Any(d => d.SettingId == SettingIds.ThemeModeWindows && d.IsActionApproved),
                ApplyCleanTaskbar = approvedSettingIds.Contains("taskbar-clean"),
                ApplyCleanStartMenu = approvedSettingIds.Contains("start-menu-clean-10") || approvedSettingIds.Contains("start-menu-clean-11"),
            };

            // Ensure action settings add their parent feature sections even if no regular settings approved
            importOptions = ConfigActionSelection.IncludeSections(selectedSections, importOptions);

            if (!selectedSections.Any())
            {
                _dialogService.ShowMessage(
                    _localizationService.GetString("Config_Import_Error_NoSelection") ?? "No changes to apply.",
                    _localizationService.GetString("Config_Import_Error_NoSelection_Title") ?? "No Changes");
                return;
            }

            // Build a filtered config containing only approved settings
            var filteredConfig = BuildFilteredConfigFromApprovals(config, approvedSettingIds);

            // Capture current external app UI selections BEFORE exiting review mode
            // This preserves user's checkbox changes made during review
            List<string>? selectedExternalAppIds = null;
            if (hasExternalApps && externalAppsRemove)
            {
                selectedExternalAppIds = _vmCoordinator.GetSelectedExternalAppIds();
            }

            // Confirm Windows Apps removal if remove action is chosen
            bool saveRemovalScripts = true;
            if (hasWindowsApps && windowsAppsRemove)
            {
                var (shouldContinue, saveScripts) = await _configAppSelectionService.ConfirmWindowsAppsRemovalAsync();
                saveRemovalScripts = saveScripts;
                if (!shouldContinue)
                {
                    await _configAppSelectionService.ClearWindowsAppsSelectionAsync();
                    selectedSections.Remove("WindowsApps");
                    _logService.Log(LogLevel.Info, "User cancelled Windows Apps removal during review apply");
                }
            }

            // Show overlay
            var overlayStatus = _localizationService.GetString("Config_Import_Status_Applying")
                ?? "Sit back, relax and watch while win-optimizer enhances Windows with your desired settings...";
            _overlayService.ShowOverlay(overlayStatus);

            _configImportState.IsActive = true;
            _configImportState.ImportSuppliesPowerValues = false;
            var changeBatch = _changeHistoryService.BeginBatch(BuildImportBatchHeader());

            try
            {
                await _configExecutionService.ApplyConfigurationWithOptionsAsync(filteredConfig, selectedSections, importOptions);

                // When restoring Windows defaults, clean up all policy registry keys AFTER
                // applying settings, because applying "disabled" values can re-create the keys
                if (_configReviewModeService.IsWindowsDefaults)
                {
                    _logService.Log(LogLevel.Info, "Windows Defaults import (review mode): cleaning up policy registry keys");
                    await Task.Run(() => _policyCleanupService.CleanupPolicyKeys());
                }
            }
            catch (Exception ex)
            {
                _logService.Log(LogLevel.Error, $"Error applying reviewed config: {ex.Message}");
            }
            finally
            {
                _configImportState.IsActive = false;
                _configImportState.ImportSuppliesPowerValues = false;
                changeBatch.Dispose();
                _overlayService.HideOverlay();
            }

            // Exit review mode after applying
            _configReviewModeService.ExitReviewMode();

            // Show success message and wait for user dismissal
            await _dialogService.ShowInformationAsync(
                _localizationService.GetString("Config_Import_Success_Message") ?? "Configuration imported successfully.",
                _localizationService.GetString("Config_Import_Success_Title") ?? "Import Successful");

            // Remove the captured External Apps selection after the success dialog closes.
            // Use captured user selections instead of config section to honor user's review choices
            if (selectedExternalAppIds != null && selectedExternalAppIds.Count > 0)
            {
                await _configAppSelectionService.RemoveSelectedExternalAppsAsync(selectedExternalAppIds);
            }
        }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Error, $"Error in ApplyReviewedConfigAsync: {ex.Message}");
            _overlayService.HideOverlay();
            _configReviewModeService.ExitReviewMode();
            _dialogService.ShowMessage($"Error applying configuration: {ex.Message}", "Error");
        }
    }

    public async Task CancelReviewModeAsync()
    {
        if (!_configReviewModeService.IsInReviewMode) return;

        // Preserve app selections on cancel — only exit review mode.
        // Clearing selections was destructive: users who imported a config and clicked
        // Cancel would lose all their carefully chosen checkboxes in Software & Apps.
        // Cancel means "cancel the review operation", not "discard my selections".
        // Review diffs and badges are still cleaned up via ReviewModeExitedEvent
        // fired by ExitReviewMode() through OnReviewModeChanged.

        _configReviewModeService.ExitReviewMode();
        _logService.Log(LogLevel.Info, "Review mode cancelled - selections preserved");
    }

    private string BuildImportBatchHeader()
    {
        var label = _localizationService.GetString("ChangeHistory_ConfigImport");
        var source = _configImportState.SourceName;
        return string.IsNullOrEmpty(source) ? label : $"{label} ({source})";
    }

    private UnifiedConfigurationFile BuildFilteredConfigFromApprovals(
        UnifiedConfigurationFile original,
        HashSet<string> approvedSettingIds)
    {
        var filtered = new UnifiedConfigurationFile
        {
            Version = original.Version,
            CreatedAt = original.CreatedAt,
            WindowsApps = original.WindowsApps,   // Apps are filtered by checkbox selection, not diffs
            ExternalApps = original.ExternalApps,  // Same - filtered by checkbox selection
        };

        // Filter Optimize features to only include approved settings
        filtered.Optimize = FilterFeatureGroupByApprovals(original.Optimize, approvedSettingIds);
        filtered.Customize = FilterFeatureGroupByApprovals(original.Customize, approvedSettingIds);

        return filtered;
    }

    private static FeatureGroupSection FilterFeatureGroupByApprovals(
        FeatureGroupSection original,
        HashSet<string> approvedSettingIds)
    {
        var filteredFeatures = new Dictionary<string, ConfigSection>();

        foreach (var feature in original.Features)
        {
            var approvedItems = feature.Value.Items
                .Where(item => approvedSettingIds.Contains(item.Id))
                .ToList();

            if (approvedItems.Any())
            {
                filteredFeatures[feature.Key] = new ConfigSection
                {
                    IsIncluded = feature.Value.IsIncluded,
                    Items = approvedItems
                };
            }
        }

        return new FeatureGroupSection
        {
            IsIncluded = original.IsIncluded,
            Features = filteredFeatures
        };
    }
}
