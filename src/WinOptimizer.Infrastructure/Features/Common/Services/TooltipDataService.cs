
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Infrastructure.Features.Common.Utilities;

namespace WinOptimizer.Infrastructure.Features.Common.Services;

public class TooltipDataService(
    IWindowsRegistryService windowsRegistryService,
    ILogService logService,
    IPowerSettingsQueryService powerSettingsQueryService) : ITooltipDataService
{
    private readonly IWindowsRegistryService _registryService = windowsRegistryService ?? throw new ArgumentNullException(nameof(windowsRegistryService));
    private readonly ILogService _logService = logService ?? throw new ArgumentNullException(nameof(logService));
    private readonly IPowerSettingsQueryService _powerSettingsQueryService = powerSettingsQueryService ?? throw new ArgumentNullException(nameof(powerSettingsQueryService));

    public Task<IReadOnlyDictionary<string, SettingTooltipData>> GetTooltipDataAsync(IEnumerable<SettingDefinition> settings) =>
        ReadTooltipDataAsync(settings, "Error fetching bulk tooltip data");

    public async Task<SettingTooltipData?> RefreshTooltipDataAsync(string settingId, SettingDefinition setting)
    {
        try
        {
            return await GetTooltipDataForSettingAsync(setting).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logService.LogWarning($"[TooltipDataService] Error refreshing tooltip for '{settingId}': {ex.Message}");
            return null;
        }
    }

    public Task<IReadOnlyDictionary<string, SettingTooltipData>> RefreshMultipleTooltipDataAsync(IEnumerable<SettingDefinition> settings) =>
        ReadTooltipDataAsync(settings, "Error refreshing multiple tooltips");

    /// <summary>Reads settings in order and retains completed results if enumeration fails.</summary>
    private async Task<IReadOnlyDictionary<string, SettingTooltipData>> ReadTooltipDataAsync(
        IEnumerable<SettingDefinition> settings, string errorMessage)
    {
        var tooltipData = new Dictionary<string, SettingTooltipData>();
        try
        {
            foreach (var setting in settings)
            {
                var data = await GetTooltipDataForSettingAsync(setting).ConfigureAwait(false);
                if (data != null) tooltipData[setting.Id] = data;
            }
        }
        catch (Exception ex)
        {
            _logService.LogWarning($"[TooltipDataService] {errorMessage}: {ex.Message}");
        }
        return tooltipData;
    }

    /// <summary>Checks whether technical details can render any of the definition's operations.</summary>
    internal static bool HasTooltipData(SettingDefinition setting) =>
        !setting.DisableTooltip &&
        (setting.RegistrySettings?.Count > 0 || setting.ScheduledTaskSettings?.Count > 0 ||
         setting.PowerCfgSettings?.Count > 0 || setting.PowerShellScripts?.Count > 0 ||
         setting.RegContents?.Count > 0 || setting.Dependencies?.Count > 0);

    /// <summary>Snapshots operation metadata while retaining the originating definition and observed values.</summary>
    internal static SettingTooltipData CreateTooltipData(SettingDefinition setting, string displayValue,
        IReadOnlyDictionary<RegistrySetting, string?> registryValues,
        IReadOnlyDictionary<PowerCfgSetting, (int? AC, int? DC)> powerValues) => new()
    {
        SettingId = setting.Id,
        DisplayValue = displayValue,
        IndividualRegistryValues = registryValues,
        ScheduledTaskSettings = setting.ScheduledTaskSettings?.ToList() ?? new List<ScheduledTaskSetting>(),
        PowerCfgSettings = setting.PowerCfgSettings?.ToList() ?? new List<PowerCfgSetting>(),
        PowerShellScripts = setting.PowerShellScripts?.ToList() ?? new List<PowerShellScriptSetting>(),
        RegContents = setting.RegContents?.ToList() ?? new List<RegContentSetting>(),
        Dependencies = setting.Dependencies?.ToList() ?? new List<SettingDependency>(),
        CurrentPowerValues = powerValues,
        SettingDefinition = setting
    };

    private async Task<SettingTooltipData?> GetTooltipDataForSettingAsync(SettingDefinition setting)
    {
        if (!HasTooltipData(setting)) return null;

        try
        {
            string displayValue = string.Empty;
            IReadOnlyDictionary<RegistrySetting, string?> individualRegistryValues = new Dictionary<RegistrySetting, string?>();

            if (setting.RegistrySettings?.Count > 0)
            {
                var registrySettings = setting.RegistrySettings!.ToList();
                var individualValues = new Dictionary<RegistrySetting, string?>();
                var primaryRegistrySetting = registrySettings.First();
                string? primaryDisplayValue = null;

                foreach (var registrySetting in registrySettings)
                {
                    try
                    {
                        object? currentValue;
                        if (registrySetting.ApplyPerNetworkInterface || registrySetting.ApplyPerMonitor)
                        {
                            // Read from the first subkey as a representative value
                            var subKeys = _registryService.GetSubKeyNames(registrySetting.KeyPath);
                            if (subKeys.Length > 0)
                            {
                                currentValue = _registryService.GetValue(
                                    $@"{registrySetting.KeyPath}\{subKeys[0]}",
                                    registrySetting.ValueName!);
                            }
                            else
                            {
                                currentValue = null;
                            }
                        }
                        else
                        {
                            currentValue = _registryService.GetValue(registrySetting.KeyPath, registrySetting.ValueName!);
                        }
                        var formattedValue = RegistryValueFormatter.Format(currentValue, registrySetting);
                        individualValues[registrySetting] = formattedValue;

                        if (registrySetting == primaryRegistrySetting)
                        {
                            primaryDisplayValue = formattedValue;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logService.LogDebug($"[TooltipDataService] Error reading registry for tooltip '{registrySetting.KeyPath}\\{registrySetting.ValueName}': {ex.Message}");
                        individualValues[registrySetting] = null;
                    }
                }

                displayValue = primaryDisplayValue ?? string.Empty;
                individualRegistryValues = individualValues;
            }

            var powerValues = new Dictionary<PowerCfgSetting, (int? AC, int? DC)>();
            var data = CreateTooltipData(setting, displayValue, individualRegistryValues, powerValues);
            await ReadCurrentPowerValuesAsync(setting.PowerCfgSettings, powerValues).ConfigureAwait(false);
            return data;
        }
        catch (Exception ex)
        {
            _logService.LogWarning($"[TooltipDataService] Error building tooltip data for '{setting.Id}': {ex.Message}");
            return null;
        }
    }

    /// <summary>Fills the snapshot's power observations after its operation metadata has been captured.</summary>
    private async Task ReadCurrentPowerValuesAsync(
        IReadOnlyList<PowerCfgSetting>? powerCfgSettings,
        Dictionary<PowerCfgSetting, (int? AC, int? DC)> powerValues)
    {
        if (powerCfgSettings is null || powerCfgSettings.Count == 0) return;
        // Sequential rather than Task.WhenAll: GetPowerSettingACDCValuesAsync wraps synchronous
        // PInvoke (PowerReadACValueIndex/PowerReadDCValueIndex); there is no parallelism gain
        // and PowerCfgSettings is typically 0-2 entries per setting.
        foreach (var pcs in powerCfgSettings)
        {
            var values = await _powerSettingsQueryService.GetPowerSettingACDCValuesAsync(pcs).ConfigureAwait(false);
            powerValues[pcs] = (values.acValue, values.dcValue);
        }
    }
}
