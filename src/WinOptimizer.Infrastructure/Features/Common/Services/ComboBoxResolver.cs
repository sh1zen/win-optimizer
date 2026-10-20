using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Infrastructure.Features.Common.Services;

public class ComboBoxResolver(
    ISystemSettingsDiscoveryService discoveryService) : IComboBoxResolver
{
    public async Task<object?> ResolveCurrentValueAsync(SettingDefinition setting, Dictionary<string, object?>? existingRawValues = null)
    {
        var rawValues = await GetRawValues(setting, existingRawValues).ConfigureAwait(false);
        
        if (setting.InputType == InputType.Selection && setting.ComboBox?.Options != null)
        {
            return ResolveRawValuesToIndex(setting, rawValues);
        }
        else if (setting.RegistrySettings?.Count > 0)
        {
            return rawValues.Values.FirstOrDefault();
        }
        else if (setting.ScheduledTaskSettings?.Count > 0)
        {
            return rawValues.TryGetValue("ScheduledTaskEnabled", out var taskEnabled) ? taskEnabled : null;
        }

        return null;
    }

    private async Task<Dictionary<string, object?>> GetRawValues(SettingDefinition setting, Dictionary<string, object?>? existingRawValues)
    {
        if (existingRawValues != null)
        {
            return existingRawValues;
        }

        var rawValues = await discoveryService.GetRawSettingsValuesAsync(new[] { setting }).ConfigureAwait(false);
        return rawValues.TryGetValue(setting.Id, out var values) ? values : new Dictionary<string, object?>();
    }

    public int GetValueFromIndex(SettingDefinition setting, int index)
    {
        if (index == ComboBoxConstants.CustomStateIndex) return 0;

        var options = setting.ComboBox?.Options;
        if (options != null && index >= 0 && index < options.Count
            && options[index].ValueMappings is { } values)
        {
            var firstValue = values.Values.FirstOrDefault();
            return firstValue is int intValue ? intValue : firstValue != null ? Convert.ToInt32(firstValue) : index;
        }
        return index;
    }

    public int ResolveRawValuesToIndex(SettingDefinition setting, Dictionary<string, object?> rawValues) =>
        Utilities.ComboBoxValueResolver.ResolveIndex(setting, rawValues);

    public Dictionary<string, object?> ResolveIndexToRawValues(SettingDefinition setting, int index)
    {
        var options = setting.ComboBox?.Options;
        return options != null && index >= 0 && index < options.Count && options[index].ValueMappings is { } values
            ? new Dictionary<string, object?>(values)
            : new Dictionary<string, object?>();
    }

    public int GetIndexFromDisplayName(SettingDefinition setting, string displayName)
    {
        if (setting.ComboBox?.Options is { } options)
        {
            for (int i = 0; i < options.Count; i++)
            {
                if (string.Equals(options[i].DisplayName, displayName, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
        }
        return 0;
    }
}
