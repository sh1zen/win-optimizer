using System.Text.Json;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Helpers;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Common.Constants;

namespace WinOptimizer.Application.Configuration;

/// <summary>Owns ConfigLoad workflows through application boundaries.</summary>
public class ConfigLoadService : IConfigLoadService
{
    private readonly ILogService _logService;
    private readonly IDialogService _dialogService;
    private readonly ILocalizationService _localizationService;
    private readonly IWindowsVersionService _windowsVersionService;
    private readonly ICompatibleSettingsRegistry _compatibleSettingsRegistry;
    private readonly IInteractiveUserService _interactiveUserService;
    private readonly IFileSystemService _fileSystemService;
    private readonly IConfigurationFilePicker _filePicker;
    private readonly IConfigImportState _configImportState;

    public ConfigLoadService(
        ILogService logService,
        IDialogService dialogService,
        ILocalizationService localizationService,
        IWindowsVersionService windowsVersionService,
        ICompatibleSettingsRegistry compatibleSettingsRegistry,
        IInteractiveUserService interactiveUserService,
        IFileSystemService fileSystemService,
        IConfigurationFilePicker filePicker,
        IConfigImportState configImportState)
    {
        _logService = logService;
        _dialogService = dialogService;
        _localizationService = localizationService;
        _windowsVersionService = windowsVersionService;
        _compatibleSettingsRegistry = compatibleSettingsRegistry;
        _interactiveUserService = interactiveUserService;
        _fileSystemService = fileSystemService;
        _filePicker = filePicker;
        _configImportState = configImportState;
    }

    public async Task<UnifiedConfigurationFile?> LoadAndValidateConfigurationFromFileAsync()
    {
        try
        {
            if (!_filePicker.IsAvailable)
            {
                _logService.Log(LogLevel.Error, "Cannot show file dialog - no main window");
                await _dialogService.ShowErrorAsync("Cannot show file dialog.", "Error");
                return null;
            }

            var filePath = _filePicker.OpenFile(
                "Open Configuration",
                ConfigFileConstants.FileFilter,
                ConfigFileConstants.FilePattern);

            if (string.IsNullOrEmpty(filePath))
                return null;

            _configImportState.SourceName = Path.GetFileName(filePath);

            var json = await _fileSystemService.ReadAllTextAsync(filePath);
            var loadedConfig = await DeserializeConfigurationAsync(json);
            if (loadedConfig is null) return null;

            _logService.Log(LogLevel.Info, $"Loaded config v{loadedConfig.Version}");
            return loadedConfig;
        }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Error, $"Error loading configuration file: {ex.Message}");
            _dialogService.ShowMessage($"Error loading configuration: {ex.Message}", "Error");
            return null;
        }
    }

    public async Task<UnifiedConfigurationFile?> LoadRecommendedConfigurationAsync()
    {
        try
        {
            _logService.Log(LogLevel.Info, "Loading embedded recommended configuration");

            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var resourceName = "WinOptimizer.Application.Configuration.Resources.WinOptimizer_Recommended_Config.win-optimizer";

            using var stream = assembly.GetManifestResourceStream(resourceName);

            if (stream == null)
            {
                _logService.Log(LogLevel.Error, $"Embedded resource not found: {resourceName}");
                _dialogService.ShowMessage(
                    "The recommended configuration file could not be found in the application.",
                    "Resource Error");
                return null;
            }

            _configImportState.SourceName = _localizationService.GetString("Dialog_ImportConfig_Option_Recommended_Title");

            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync();
            var config = await DeserializeConfigurationAsync(json);
            if (config is null) return null;

            _logService.Log(LogLevel.Info, "Successfully loaded embedded recommended configuration");
            return config;
        }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Error, $"Error loading recommended configuration: {ex.Message}");
            _dialogService.ShowMessage($"Error loading configuration: {ex.Message}", "Error");
            return null;
        }
    }

    public async Task<UnifiedConfigurationFile?> LoadWindowsDefaultsConfigurationAsync()
    {
        try
        {
            var isWindows11 = _windowsVersionService.IsWindows11();
            var resourceName = isWindows11
                ? "WinOptimizer.Application.Configuration.Resources.WinOptimizer_Default_Config_Windows11_25H2.win-optimizer"
                : "WinOptimizer.Application.Configuration.Resources.WinOptimizer_Default_Config_Windows10_22H2.win-optimizer";

            _logService.Log(LogLevel.Info, $"Loading embedded Windows defaults configuration for {(isWindows11 ? "Windows 11" : "Windows 10")}: {resourceName}");

            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream(resourceName);

            if (stream == null)
            {
                _logService.Log(LogLevel.Error, $"Embedded resource not found: {resourceName}");
                _dialogService.ShowMessage(
                    "The Windows defaults configuration file could not be found in the application.",
                    "Resource Error");
                return null;
            }

            _configImportState.SourceName = _localizationService.GetString("Dialog_ImportConfig_Option_Defaults_Title");

            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync();
            var config = await DeserializeConfigurationAsync(json);
            if (config is null) return null;

            _logService.Log(LogLevel.Info, "Successfully loaded embedded Windows defaults configuration");
            return config;
        }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Error, $"Error loading Windows defaults configuration: {ex.Message}");
            _dialogService.ShowMessage($"Error loading configuration: {ex.Message}", "Error");
            return null;
        }
    }

    public async Task<UnifiedConfigurationFile?> LoadUserBackupConfigurationAsync()
    {
        try
        {
            var configDir = AppDataPaths.ConfigurationBackups(_interactiveUserService);

            if (!_fileSystemService.DirectoryExists(configDir))
            {
                _dialogService.ShowMessage(
                    _localizationService.GetString("Config_Backup_NotFound") ?? "No backup configuration files found.",
                    _localizationService.GetString("Config_Backup_NotFound_Title") ?? "No Backup Found");
                return null;
            }

            var backupFiles = _fileSystemService.GetFiles(configDir, $"UserBackup_*{ConfigFileConstants.FileExtension}")
                .OrderByDescending(f => f)
                .ToArray();

            if (backupFiles.Length == 0)
            {
                _dialogService.ShowMessage(
                    _localizationService.GetString("Config_Backup_NotFound") ?? "No backup configuration files found.",
                    _localizationService.GetString("Config_Backup_NotFound_Title") ?? "No Backup Found");
                return null;
            }

            string filePath;

            if (backupFiles.Length == 1)
            {
                filePath = backupFiles[0];
            }
            else
            {
                if (!_filePicker.IsAvailable)
                {
                    _logService.Log(LogLevel.Error, "Cannot show file dialog - no main window");
                    await _dialogService.ShowErrorAsync("Cannot show file dialog.", "Error");
                    return null;
                }

                var dialogTitle = _localizationService.GetString("Config_Backup_Select_Title")
                    ?? "Select Backup File";
                var selectedPath = _filePicker.OpenFile(
                    dialogTitle, ConfigFileConstants.FileFilter, ConfigFileConstants.FilePattern, configDir);

                if (string.IsNullOrEmpty(selectedPath))
                {
                    _logService.Log(LogLevel.Info, "Backup import canceled by user");
                    return null;
                }

                filePath = selectedPath;
            }
            _logService.Log(LogLevel.Info, $"Loading user backup configuration from {filePath}");
            _configImportState.SourceName = _localizationService.GetString("Dialog_ImportConfig_Option_Backup_Title");

            var json = await _fileSystemService.ReadAllTextAsync(filePath);
            var config = await DeserializeConfigurationAsync(json);
            if (config is null) return null;

            _logService.Log(LogLevel.Info, "Successfully loaded user backup configuration");
            return config;
        }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Error, $"Error loading user backup configuration: {ex.Message}");
            _dialogService.ShowMessage($"Error loading backup configuration: {ex.Message}", "Error");
            return null;
        }
    }

    /// <summary>Requires the current schema for file, backup and embedded configuration sources.</summary>
    private async Task<UnifiedConfigurationFile?> DeserializeConfigurationAsync(string json)
    {
        var config = JsonSerializer.Deserialize<UnifiedConfigurationFile>(json, ConfigFileConstants.JsonOptions)
            ?? throw new JsonException("A configuration object is required.");
        if (config.Version == ConfigFileConstants.CurrentVersion) return config;

        var versionText = config.Version ?? "unknown";
        await _dialogService.ShowInformationAsync(
            _localizationService.GetString("Config_Unsupported_Message", versionText)
                ?? $"This configuration file version ({versionText}) is not compatible with this version of win-optimizer.",
            _localizationService.GetString("Config_Unsupported_Title") ?? "Incompatible Configuration");
        _logService.Log(LogLevel.Warning, $"Rejected incompatible config version: {config.Version}");
        return null;
    }

    public List<string> DetectIncompatibleSettings(UnifiedConfigurationFile config)
    {
        var incompatible = new List<string>();
        var isWindows11 = _windowsVersionService.IsWindows11();
        var buildNumber = _windowsVersionService.GetWindowsBuildNumber();
        var buildRevision = _windowsVersionService.GetWindowsBuildRevision();

        var allSections = new[] { config.Optimize, config.Customize };

        foreach (var section in allSections)
        {
            if (section?.Features == null) continue;

            foreach (var feature in section.Features)
            {
                var allSettings = _compatibleSettingsRegistry.GetBypassedSettings(feature.Key).ToLookup(setting => setting.Id);

                foreach (var configItem in feature.Value.Items)
                {
                    var settingDef = allSettings[configItem.Id].FirstOrDefault();
                    if (settingDef != null && BuildVersionGate.GetCompatibilityMessage(
                            settingDef, isWindows11, buildNumber, buildRevision) != null)
                    {
                        incompatible.Add($"{settingDef.Name} ({feature.Key})");
                    }
                }
            }
        }

        return incompatible;
    }

    public UnifiedConfigurationFile FilterConfigForCurrentSystem(UnifiedConfigurationFile config)
    {
        var isWindows11 = _windowsVersionService.IsWindows11();
        var buildNumber = _windowsVersionService.GetWindowsBuildNumber();
        var buildRevision = _windowsVersionService.GetWindowsBuildRevision();

        var filteredOptimize = FilterFeatureGroup(config.Optimize, isWindows11, buildNumber, buildRevision);
        var filteredCustomize = FilterFeatureGroup(config.Customize, isWindows11, buildNumber, buildRevision);

        return new UnifiedConfigurationFile
        {
            Version = config.Version,
            Optimize = filteredOptimize,
            Customize = filteredCustomize,
            WindowsApps = config.WindowsApps,
            ExternalApps = config.ExternalApps
        };
    }

    private FeatureGroupSection FilterFeatureGroup(
        FeatureGroupSection section,
        bool isWindows11,
        int buildNumber,
        int buildRevision)
    {
        if (section?.Features == null) return section!;

        var filteredFeatures = new Dictionary<string, ConfigSection>();

        foreach (var feature in section.Features)
        {
            var allSettings = _compatibleSettingsRegistry.GetBypassedSettings(feature.Key).ToLookup(setting => setting.Id);
            var filteredItems = new List<ConfigurationItem>();

            foreach (var item in feature.Value.Items)
            {
                var settingDef = allSettings[item.Id].FirstOrDefault();
                if (settingDef == null || BuildVersionGate.GetCompatibilityMessage(
                        settingDef, isWindows11, buildNumber, buildRevision) == null)
                {
                    filteredItems.Add(item);
                }
            }

            filteredFeatures[feature.Key] = new ConfigSection
            {
                IsIncluded = feature.Value.IsIncluded,
                Items = filteredItems
            };
        }

        return new FeatureGroupSection
        {
            IsIncluded = section.IsIncluded,
            Features = filteredFeatures
        };
    }
}
