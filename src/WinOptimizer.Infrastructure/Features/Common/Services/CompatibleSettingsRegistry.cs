using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Customize.Models;
using WinOptimizer.Core.Features.Optimize.Models;

namespace WinOptimizer.Infrastructure.Features.Common.Services;

public class CompatibleSettingsRegistry : ICompatibleSettingsRegistry
{
    private readonly IWindowsCompatibilityFilter _windowsFilter;
    private readonly IHardwareCompatibilityFilter _hardwareFilter;
    private readonly IPowerSettingsValidationService _powerValidation;
    private readonly ILogService _logService;

    private bool _isInitialized;
    private readonly SemaphoreSlim _initializationLock = new SemaphoreSlim(1, 1);
    private readonly Dictionary<string, IEnumerable<SettingDefinition>> _preFilteredSettings = new();
    private readonly Dictionary<string, IEnumerable<SettingDefinition>> _windowsFilterBypassedSettings = new();
    private Dictionary<string, (SettingDefinition Setting, string FeatureId)> _filteredById = new();
    private Dictionary<string, (SettingDefinition Setting, string FeatureId)> _bypassedById = new();
    private bool _filterEnabled = true;

    public bool IsInitialized => _isInitialized;

    public CompatibleSettingsRegistry(
        IWindowsCompatibilityFilter windowsFilter,
        IHardwareCompatibilityFilter hardwareFilter,
        IPowerSettingsValidationService powerValidation,
        ILogService logService)
    {
        _windowsFilter = windowsFilter ?? throw new ArgumentNullException(nameof(windowsFilter));
        _hardwareFilter = hardwareFilter ?? throw new ArgumentNullException(nameof(hardwareFilter));
        _powerValidation = powerValidation ?? throw new ArgumentNullException(nameof(powerValidation));
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized) return;

        await _initializationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_isInitialized) return;

            _logService.Log(LogLevel.Info, "Initializing compatible settings registry with auto-discovery");

            await PreFilterAllFeatureSettingsAsync().ConfigureAwait(false);

            _filteredById = BuildIdIndex(_preFilteredSettings);
            _bypassedById = BuildIdIndex(_windowsFilterBypassedSettings, " (bypassed)");
            _isInitialized = true;
            _logService.Log(LogLevel.Info, $"Compatible settings registry initialized with {_preFilteredSettings.Count} pre-filtered features");
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public SettingDefinition? GetById(string settingId)
    {
        if (!_isInitialized)
            throw new InvalidOperationException("Registry not initialized. Call InitializeAsync first.");

        ArgumentNullException.ThrowIfNull(settingId);

        var index = _filterEnabled ? _filteredById : _bypassedById;
        return index.TryGetValue(settingId, out var entry) ? entry.Setting : null;
    }

    public string? GetFeatureIdForSetting(string settingId)
    {
        if (!_isInitialized)
            throw new InvalidOperationException("Registry not initialized. Call InitializeAsync first.");

        ArgumentNullException.ThrowIfNull(settingId);

        var index = _filterEnabled ? _filteredById : _bypassedById;
        return index.TryGetValue(settingId, out var entry) ? entry.FeatureId : null;
    }

    public IEnumerable<SettingDefinition> GetFilteredSettings(string featureId)
    {
        if (!_isInitialized)
            throw new InvalidOperationException("Registry not initialized");

        _logService.Log(LogLevel.Debug, $"GetFilteredSettings for {featureId}: Filter enabled = {_filterEnabled}");

        var catalogue = _filterEnabled ? _preFilteredSettings : _windowsFilterBypassedSettings;
        return catalogue.TryGetValue(featureId, out var settings)
            ? settings
            : Enumerable.Empty<SettingDefinition>();
    }

    public void SetFilterEnabled(bool enabled)
    {
        _filterEnabled = enabled;
    }

    public IReadOnlyDictionary<string, IEnumerable<SettingDefinition>> GetAllFilteredSettings()
    {
        if (!_isInitialized)
            throw new InvalidOperationException("Registry not initialized. Call InitializeAsync first.");

        return _filterEnabled ? _preFilteredSettings : _windowsFilterBypassedSettings;
    }

    public IEnumerable<SettingDefinition> GetBypassedSettings(string featureId)
    {
        if (!_isInitialized)
            throw new InvalidOperationException("Registry not initialized");

        return _windowsFilterBypassedSettings.TryGetValue(featureId, out var settings)
            ? settings
            : Enumerable.Empty<SettingDefinition>();
    }

    public IReadOnlyDictionary<string, IEnumerable<SettingDefinition>> GetAllBypassedSettings()
    {
        if (!_isInitialized)
            throw new InvalidOperationException("Registry not initialized");

        return _windowsFilterBypassedSettings;
    }

    /// <summary>Indexes each snapshot once, keeping its definition and owner together.</summary>
    private Dictionary<string, (SettingDefinition Setting, string FeatureId)> BuildIdIndex(
        IReadOnlyDictionary<string, IEnumerable<SettingDefinition>> catalogue,
        string logSuffix = "")
    {
        var index = new Dictionary<string, (SettingDefinition Setting, string FeatureId)>();
        foreach (var (featureId, settings) in catalogue)
        {
            foreach (var s in settings)
            {
                if (index.TryGetValue(s.Id, out var previous))
                {
                    _logService.Log(LogLevel.Warning,
                        $"Duplicate setting id '{s.Id}' — previously registered under feature '{previous.FeatureId}', now overwritten by '{featureId}'{logSuffix}");
                }
                index[s.Id] = (s, featureId);
            }
        }
        return index;
    }

    private async Task PreFilterAllFeatureSettingsAsync()
    {
        _logService.Log(LogLevel.Info, "Pre-filtering settings for all features");

        var featureProviders = GetKnownFeatureProviders();
        _logService.Log(LogLevel.Info, $"Found {featureProviders.Count} feature providers");

        foreach (var (featureId, provider) in featureProviders)
        {
            try
            {
                _logService.Log(LogLevel.Info, $"Loading raw settings for {featureId}");
                var rawSettings = provider().ToList();
                _logService.Log(LogLevel.Info, $"Loaded {rawSettings.Count} raw settings for {featureId}");

                var filteredSettings = _windowsFilter.FilterSettingsByWindowsVersion(rawSettings);

                if (featureId == FeatureIds.Power)
                {
                    filteredSettings = await _hardwareFilter.FilterSettingsByHardwareAsync(filteredSettings).ConfigureAwait(false);
                    filteredSettings = await _powerValidation.FilterSettingsByExistenceAsync(filteredSettings).ConfigureAwait(false);
                }

                var filteredSnapshot = filteredSettings.ToArray();
                var bypassedSnapshot = _windowsFilter
                    .FilterSettingsByWindowsVersion(rawSettings, applyFilter: false).ToArray();
                _preFilteredSettings[featureId] = filteredSnapshot;
                _windowsFilterBypassedSettings[featureId] = bypassedSnapshot;

                _logService.Log(LogLevel.Info, $"Registered {filteredSnapshot.Length} settings for {featureId}");
            }
            catch (Exception ex)
            {
                _logService.Log(LogLevel.Error,
                    $"Error loading settings for {featureId}: {ex.Message}");
                _preFilteredSettings[featureId] = Enumerable.Empty<SettingDefinition>();
                _windowsFilterBypassedSettings[featureId] = Enumerable.Empty<SettingDefinition>();
            }
        }

        _logService.Log(LogLevel.Info, "Pre-filtering completed");
    }

    /// <summary>
    /// Returns the explicit registry of all feature setting providers.
    /// Each provider is a direct static method call — no reflection, no naming conventions.
    /// To add a new feature, add a single entry here.
    /// </summary>
    private static Dictionary<string, Func<IEnumerable<SettingDefinition>>> GetKnownFeatureProviders()
    {
        return new Dictionary<string, Func<IEnumerable<SettingDefinition>>>
        {
            // Customize features
            [FeatureIds.ExplorerCustomization] = () => ExplorerCustomizations.GetExplorerCustomizations().Settings,
            [FeatureIds.StartMenu] = () => StartMenuCustomizations.GetStartMenuCustomizations().Settings,
            [FeatureIds.Taskbar] = () => TaskbarCustomizations.GetTaskbarCustomizations().Settings,
            [FeatureIds.WindowsTheme] = () => WindowsThemeCustomizations.GetWindowsThemeCustomizations().Settings,

            // Optimize features
            [FeatureIds.Power] = () => PowerOptimizations.GetPowerOptimizations().Settings,
            [FeatureIds.GamingPerformance] = () => GamingAndPerformanceOptimizations.GetGamingAndPerformanceOptimizations().Settings,
            [FeatureIds.Notifications] = () => NotificationOptimizations.GetNotificationOptimizations().Settings,
            [FeatureIds.Privacy] = () => PrivacyAndSecurityOptimizations.GetPrivacyAndSecurityOptimizations().Settings,
            [FeatureIds.Sound] = () => SoundOptimizations.GetSoundOptimizations().Settings,
            [FeatureIds.Update] = () => UpdateOptimizations.GetUpdateOptimizations().Settings,
        };
    }

}
