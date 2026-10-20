using System.Text.Json;
using WinOptimizer.Core.Features.AdvancedTools.Interfaces;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Application.Configuration;

/// <summary>Owns ConfigExport workflows through application boundaries.</summary>
public class ConfigExportService : IConfigExportService
{
    private readonly ILogService _logService;
    private readonly IDialogService _dialogService;
    private readonly ILocalizationService _localizationService;
    private readonly ICompatibleSettingsRegistry _compatibleSettingsRegistry;
    private readonly IGlobalSettingsPreloader _settingsPreloader;
    private readonly ISystemSettingsDiscoveryService _discoveryService;
    private readonly IInteractiveUserService _interactiveUserService;
    private readonly IConfigurationAppSource _apps;
    private readonly IFileSystemService _fileSystemService;
    private readonly IConfigurationFilePicker _filePicker;
    private readonly IApplicationModeService _applicationModeService;
    private readonly IAutounattendXmlGeneratorService _autounattendGenerator;
    private readonly IPowerPlanComboBoxService _powerPlans;

    public ConfigExportService(
        ILogService logService,
        IDialogService dialogService,
        ILocalizationService localizationService,
        ICompatibleSettingsRegistry compatibleSettingsRegistry,
        IGlobalSettingsPreloader settingsPreloader,
        ISystemSettingsDiscoveryService discoveryService,
        IInteractiveUserService interactiveUserService,
        IConfigurationAppSource apps,
        IFileSystemService fileSystemService,
        IConfigurationFilePicker filePicker,
        IApplicationModeService applicationModeService,
        IAutounattendXmlGeneratorService autounattendGenerator,
        IPowerPlanComboBoxService powerPlans)
    {
        _logService = logService;
        _dialogService = dialogService;
        _localizationService = localizationService;
        _compatibleSettingsRegistry = compatibleSettingsRegistry;
        _settingsPreloader = settingsPreloader;
        _discoveryService = discoveryService;
        _interactiveUserService = interactiveUserService;
        _apps = apps;
        _fileSystemService = fileSystemService;
        _filePicker = filePicker;
        _applicationModeService = applicationModeService;
        _autounattendGenerator = autounattendGenerator;
        _powerPlans = powerPlans;
    }

    private Task EnsureRegistryInitializedAsync()
        => ConfigRegistryInitializer.EnsureInitializedAsync(_compatibleSettingsRegistry, _settingsPreloader, _logService);

    public async Task ExportConfigurationAsync()
    {
        try
        {
            _logService.Log(LogLevel.Info, "Starting configuration export");

            await EnsureRegistryInitializedAsync();

            var config = _applicationModeService.CurrentMode == WinOptimizerMode.Builder
                ? await CreateConfigurationFromUiStateAsync()
                : await CreateConfigurationFromSystemAsync();

            if (config.WindowsApps.Items.Count == 0)
            {
                var continueAnyway = (await _dialogService.ShowConfirmationAsync(new ConfirmationRequest
                {
                    Message = _localizationService.GetString("Dialog_NoAppsSelected_Config_Message"),
                    Title = _localizationService.GetString("Dialog_NoAppsSelected_Title"),
                })).Confirmed;
                if (!continueAnyway)
                    return;
            }

            if (!_filePicker.IsAvailable)
            {
                _logService.Log(LogLevel.Error, "Cannot show file dialog - no main window");
                await _dialogService.ShowErrorAsync("Cannot show file dialog.", "Error");
                return;
            }

            var defaultFileName = $"WinOptimizer_Config_{DateTime.Now:yyyyMMdd}{ConfigFileConstants.FileExtension}";
            var filePath = _filePicker.SaveFile(
                "Save Configuration",
                ConfigFileConstants.FileFilter,
                ConfigFileConstants.FilePattern,
                defaultFileName,
                "win-optimizer");

            if (string.IsNullOrEmpty(filePath))
            {
                _logService.Log(LogLevel.Info, "Export canceled by user");
                return;
            }

            var json = JsonSerializer.Serialize(config, ConfigFileConstants.JsonOptions);
            await _fileSystemService.WriteAllTextAsync(filePath, json);

            _logService.Log(LogLevel.Info, $"Configuration exported to {filePath}");

            await _dialogService.ShowInformationAsync(
                _localizationService.GetString("Config_Export_Success_Message", filePath)
                    ?? $"Configuration exported to {filePath}",
                _localizationService.GetString("Config_Export_Success_Title") ?? "Export Successful");
        }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Error, $"Error exporting configuration: {ex.Message}");
            await _dialogService.ShowErrorAsync(
                _localizationService.GetString("Config_Export_Error_Message", ex.Message)
                    ?? $"Error exporting configuration: {ex.Message}",
                _localizationService.GetString("Config_Export_Error_Title") ?? "Export Error");
        }
    }

    public async Task CreateUserBackupConfigAsync()
    {
        try
        {
            _logService.Log(LogLevel.Info, "Creating user backup configuration from current system state");

            await EnsureRegistryInitializedAsync();

            var config = await CreateConfigurationFromSystemAsync(isBackup: true);

            var configDir = AppDataPaths.ConfigurationBackups(_interactiveUserService);

            _fileSystemService.CreateDirectory(configDir);

            var fileName = $"UserBackup_{DateTime.Now:yyyyMMdd_HHmmss}{ConfigFileConstants.FileExtension}";
            var filePath = _fileSystemService.CombinePath(configDir, fileName);

            var json = JsonSerializer.Serialize(config, ConfigFileConstants.JsonOptions);
            await _fileSystemService.WriteAllTextAsync(filePath, json);

            _logService.Log(LogLevel.Info, $"User backup configuration saved to {filePath}");
        }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Error, $"Error creating user backup configuration: {ex.Message}");
        }
    }

    public async Task<UnifiedConfigurationFile> CreateConfigurationFromSystemAsync(bool isBackup = false)
    {
        var config = new UnifiedConfigurationFile
        {
            Version = ConfigFileConstants.CurrentVersion,
            CreatedAt = DateTime.UtcNow
        };

        await FeatureConfigurationSnapshot.PopulateAsync(
            config, _compatibleSettingsRegistry, _discoveryService, _logService, CreateConfigurationItem);
        await PopulateAppsSections(config, isBackup);

        return config;
    }

    public async Task<UnifiedConfigurationFile> CreateConfigurationFromUiStateAsync(bool isBackup = false)
    {
        // Seed from current system state (Builder reflects the machine), then overlay
        // the user's authored Builder edits so the saved file captures their intent.
        if (isBackup) return await CreateConfigurationFromSystemAsync(isBackup: true);
        var config = new UnifiedConfigurationFile { Version = ConfigFileConstants.CurrentVersion, CreatedAt = DateTime.UtcNow };
        await FeatureConfigurationSnapshot.PopulateAsync(
            config, _compatibleSettingsRegistry, _discoveryService, _logService, CreateConfigurationItem);
        await PopulateAppsSections(config);
        await ApplyBuilderEditsAsync(config);
        return config;
    }

    /// <summary>Copies the authored setting changes into a configuration for the existing apply pipeline.</summary>
    public async Task<UnifiedConfigurationFile> CreateConfigurationFromBuilderEditsAsync()
    {
        var ids = _applicationModeService.GetBuilderEdits().Select(edit => edit.SettingId).ToHashSet();
        if (ids.Count == 0) return new UnifiedConfigurationFile();
        await EnsureRegistryInitializedAsync();
        var config = await CreateConfigurationFromUiStateAsync();
        foreach (var group in new[] { config.Optimize, config.Customize })
        {
            group.Features = group.Features.Where(pair => pair.Value.Items.Any(item => ids.Contains(item.Id)))
                .ToDictionary(pair => pair.Key, pair => new ConfigSection
                {
                    IsIncluded = true, Items = pair.Value.Items.Where(item => ids.Contains(item.Id)).ToArray()
                });
            group.IsIncluded = group.Features.Count > 0;
        }
        config.WindowsApps = new ConfigSection();
        config.ExternalApps = new ConfigSection();
        return config;
    }

    private async Task ApplyBuilderEditsAsync(UnifiedConfigurationFile config)
    {
        var edits = _applicationModeService.GetBuilderEdits();
        if (edits.Count == 0)
        {
            return;
        }

        var editsById = edits.ToDictionary(e => e.SettingId, e => e);

        var sections = config.Optimize.Features.Values
            .Concat(config.Customize.Features.Values);

        foreach (var section in sections)
        {
            foreach (var item in section.Items)
            {
                if (!editsById.TryGetValue(item.Id, out var edit))
                {
                    continue;
                }

                switch (edit.InputType)
                {
                    case InputType.Toggle:
                    case InputType.CheckBox:
                    case InputType.Action:
                        item.IsSelected = edit.IsSelected;
                        break;

                    case InputType.Selection:
                        item.PowerSettings = edit.PowerSettings is null ? null : new(edit.PowerSettings);
                        if (edit.CustomStateValues != null)
                        {
                            item.CustomStateValues = new(edit.CustomStateValues);
                            item.SelectedIndex = null;
                        }
                        else
                        {
                            item.SelectedIndex = edit.SelectedIndex;
                            item.CustomStateValues = null;
                        }
                        if (item.Id == SettingIds.PowerPlanSelection && edit.SelectedIndex is int planIndex)
                        {
                            var plan = await _powerPlans.ResolvePowerPlanByIndexAsync(planIndex);
                            if (!plan.Success) throw new InvalidOperationException(plan.ErrorMessage);
                            item.PowerPlanGuid = plan.Guid;
                            item.PowerPlanName = plan.DisplayName;
                            item.SelectedIndex = null;
                        }
                        break;

                    case InputType.NumericRange:
                        item.PowerSettings = edit.PowerSettings is null ? null : new(edit.PowerSettings);
                        break;
                }
            }
        }

        _logService.Log(LogLevel.Info,
            $"[ConfigExportService] Applied {edits.Count} Builder edit(s) onto the seeded configuration");
    }

    public async Task<bool> ExportBuilderConfigAsync()
    {
        try
        {
            _logService.Log(LogLevel.Info, "Starting Builder configuration export");

            await EnsureRegistryInitializedAsync();

            var config = await CreateConfigurationFromUiStateAsync();

            if (!_filePicker.IsAvailable)
            {
                _logService.Log(LogLevel.Error, "Cannot show file dialog - no main window");
                await _dialogService.ShowErrorAsync("Cannot show file dialog.", "Error");
                return false;
            }

            var defaultFileName = $"WinOptimizer_Config_{DateTime.Now:yyyyMMdd}{ConfigFileConstants.FileExtension}";
            var filePath = _filePicker.SaveFile(
                "Save Configuration",
                ConfigFileConstants.FileFilter,
                ConfigFileConstants.FilePattern,
                defaultFileName,
                "win-optimizer");

            if (string.IsNullOrEmpty(filePath))
            {
                _logService.Log(LogLevel.Info, "Builder export canceled by user");
                return false;
            }

            var json = JsonSerializer.Serialize(config, ConfigFileConstants.JsonOptions);
            await _fileSystemService.WriteAllTextAsync(filePath, json);

            _logService.Log(LogLevel.Info, $"Builder configuration exported to {filePath}");

            await _dialogService.ShowInformationAsync(
                _localizationService.GetString("Config_Export_Success_Message", filePath)
                    ?? $"Configuration saved to {filePath}",
                _localizationService.GetString("Config_Export_Success_Title") ?? "Save Successful");
            return true;
        }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Error, $"Error exporting Builder configuration: {ex.Message}");
            await _dialogService.ShowErrorAsync(
                _localizationService.GetString("Config_Export_Error_Message", ex.Message)
                    ?? $"Error saving configuration: {ex.Message}",
                _localizationService.GetString("Config_Export_Error_Title") ?? "Save Error");
            return false;
        }
    }

    public async Task ExportBuilderAutounattendAsync()
    {
        try
        {
            _logService.Log(LogLevel.Info, "Starting Builder autounattend.xml export");

            await EnsureRegistryInitializedAsync();

            var config = await CreateConfigurationFromUiStateAsync();

            if (!_filePicker.IsAvailable)
            {
                _logService.Log(LogLevel.Error, "Cannot show file dialog - no main window");
                await _dialogService.ShowErrorAsync("Cannot show file dialog.", "Error");
                return;
            }

            var defaultFileName = "autounattend.xml";
            var filePath = _filePicker.SaveFile(
                "Save autounattend.xml",
                "Autounattend XML File",
                "*.xml",
                defaultFileName,
                "xml");

            if (string.IsNullOrEmpty(filePath))
            {
                _logService.Log(LogLevel.Info, "Builder autounattend export canceled by user");
                return;
            }

            await _autounattendGenerator.GenerateFromConfigAsync(config, filePath);

            _logService.Log(LogLevel.Info, $"Builder autounattend.xml exported to {filePath}");

            await _dialogService.ShowInformationAsync(
                _localizationService.GetString("Config_Export_Success_Message", filePath)
                    ?? $"autounattend.xml saved to {filePath}",
                _localizationService.GetString("Config_Export_Success_Title") ?? "Save Successful");
        }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Error, $"Error exporting Builder autounattend.xml: {ex.Message}");
            await _dialogService.ShowErrorAsync(
                _localizationService.GetString("Config_Export_Error_Message", ex.Message)
                    ?? $"Error saving autounattend.xml: {ex.Message}",
                _localizationService.GetString("Config_Export_Error_Title") ?? "Save Error");
        }
    }

    /// <summary>Projects discovered state into configuration selection indices and power values.</summary>
    private ConfigurationItem CreateConfigurationItem(SettingDefinition setting, SettingStateResult? state)
    {
        var item = new ConfigurationItem
        {
            Id = setting.Id,
            Name = setting.Name,
            InputType = setting.InputType
        };

        if (setting.InputType == InputType.Toggle)
        {
            item.IsSelected = state?.IsEnabled ?? false;
        }
        else if (setting.InputType == InputType.Selection)
        {
            var (selectedIndex, customStateValues, powerPlanGuid, powerPlanName) = GetSelectionStateFromState(setting, state);

            if (setting.Id == SettingIds.PowerPlanSelection)
            {
                item.PowerPlanGuid = powerPlanGuid;
                item.PowerPlanName = powerPlanName;
            }
            else
            {
                bool hasAcDcPowerSettings = false;

                if (setting.PowerCfgSettings?.Any() == true &&
                    setting.PowerCfgSettings[0].PowerModeSupport == PowerModeSupport.Separate &&
                    state?.RawValues != null)
                {
                    var acValue = state.RawValues.TryGetValue("ACValue", out var acVal) ? acVal : null;
                    var dcValue = state.RawValues.TryGetValue("DCValue", out var dcVal) ? dcVal : null;

                    if (acValue != null || dcValue != null)
                    {
                        var acIndex = ResolveValueToIndex(setting, acValue);
                        var dcIndex = ResolveValueToIndex(setting, dcValue);

                        item.PowerSettings = new Dictionary<string, object>
                        {
                            ["ACIndex"] = acIndex,
                            ["DCIndex"] = dcIndex
                        };
                        hasAcDcPowerSettings = true;
                    }
                }

                if (!hasAcDcPowerSettings)
                {
                    item.SelectedIndex = selectedIndex;
                }

                item.CustomStateValues = customStateValues;
            }
        }
        else if (setting.InputType == InputType.NumericRange)
        {
            if (state?.CurrentValue != null)
            {
                if (setting.PowerCfgSettings?.Any() == true &&
                    setting.PowerCfgSettings[0].PowerModeSupport == PowerModeSupport.Separate &&
                    state.RawValues != null)
                {
                    var acValue = state.RawValues.TryGetValue("ACValue", out var acVal) ? acVal : null;
                    var dcValue = state.RawValues.TryGetValue("DCValue", out var dcVal) ? dcVal : null;

                    if (acValue != null || dcValue != null)
                    {
                        item.PowerSettings = new Dictionary<string, object>
                        {
                            ["ACValue"] = acValue!,
                            ["DCValue"] = dcValue!
                        };
                    }
                }
                else
                {
                    item.PowerSettings = new Dictionary<string, object>
                    {
                        ["Value"] = state.CurrentValue
                    };
                }
            }
        }

        return item;
    }

    private async Task PopulateAppsSections(UnifiedConfigurationFile config, bool useInstalledStatus = false)
    {
        config.WindowsApps = await _apps.GetWindowsAppsAsync(useInstalledStatus);
        _logService.Log(LogLevel.Info, $"Exported {config.WindowsApps.Items.Count} {(useInstalledStatus ? "installed" : "checked")} Windows Apps");

        if (!useInstalledStatus)
        {
            config.ExternalApps = await _apps.GetExternalAppsAsync();
            _logService.Log(LogLevel.Info, $"Exported {config.ExternalApps.Items.Count} checked External Apps");
        }
    }

    private (int? selectedIndex, Dictionary<string, object>? customStateValues, string? powerPlanGuid, string? powerPlanName)
        GetSelectionStateFromState(SettingDefinition setting, SettingStateResult? state)
    {
        if (state?.CurrentValue is not int index)
            return (0, null, null, null);

        if (setting.Id == SettingIds.PowerPlanSelection && state.RawValues != null)
        {
            var guid = state.RawValues.TryGetValue("ActivePowerPlanGuid", out var g) ? g?.ToString() : null;
            var name = state.RawValues.TryGetValue("ActivePowerPlan", out var n) ? n?.ToString() : null;

            _logService.Log(LogLevel.Info, $"[ConfigExportService] Exporting power plan: {name} ({guid})");
            return (index, null, guid, name);
        }

        if (index == ComboBoxConstants.CustomStateIndex)
        {
            var customValues = new Dictionary<string, object>();

            if (state.RawValues != null)
            {
                foreach (var registrySetting in setting.RegistrySettings)
                {
                    var key = registrySetting.ValueName ?? "KeyExists";
                    if (state.RawValues.TryGetValue(key, out var value) && value != null)
                    {
                        customValues[key] = value;
                    }
                }
            }

            return (null, customValues.Count > 0 ? customValues : null, null, null);
        }

        return (index, null, null, null);
    }

    private static int ResolveValueToIndex(SettingDefinition setting, object? value)
    {
        if (value == null) return 0;

        var intValue = Convert.ToInt32(value);

        var options = setting.ComboBox?.Options;
        if (options == null)
            return 0;

        for (int i = 0; i < options.Count; i++)
        {
            var mapping = options[i].ValueMappings;
            if (mapping == null) continue;

            if (mapping.TryGetValue("PowerCfgValue", out var expectedValue) &&
                expectedValue != null && Convert.ToInt32(expectedValue) == intValue)
            {
                return i;
            }
        }

        return 0;
    }
}
