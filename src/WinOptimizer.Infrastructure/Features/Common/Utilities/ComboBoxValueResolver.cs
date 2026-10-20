using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Infrastructure.Features.Common.Utilities;

/// <summary>Matches observed setting values to catalogue options without discovery or UI dependencies.</summary>
internal static class ComboBoxValueResolver
{
    public static int ResolveIndex(SettingDefinition setting, IReadOnlyDictionary<string, object?> rawValues)
    {
        // Handle DetectedIndex from custom detection (e.g., DnsServer)
        if (rawValues.TryGetValue("DetectedIndex", out var detectedIndex) && detectedIndex is int di)
            return di;

        var options = setting.ComboBox?.Options;
        if (options == null)
        {
            return 0;
        }

        if (rawValues.TryGetValue("CurrentPolicyIndex", out var policyIndex))
        {
            return policyIndex is int index ? index : 0;
        }

        if (!options.Any(option => option.ValueMappings != null)) return 0;

        var currentValues = new Dictionary<string, object?>();

        if (setting.PowerCfgSettings?.Count > 0 && rawValues.TryGetValue("PowerCfgValue", out var powerCfgValue))
        {
            currentValues["PowerCfgValue"] = powerCfgValue != null ? Convert.ToInt32(powerCfgValue) : null;
        }

        foreach (var registrySetting in setting.RegistrySettings)
        {
            var key = registrySetting.ValueName ?? "KeyExists";
            if (rawValues.TryGetValue(key, out var rawValue) && rawValue != null)
            {
                currentValues[key] = rawValue;
            }
            else if (registrySetting.DefaultValue != null)
            {
                currentValues[key] = registrySetting.DefaultValue;
            }
            else
            {
                currentValues[key] = null;
            }
        }

        for (int index = 0; index < options.Count; index++)
        {
            var expectedValues = options[index].ValueMappings;
            if (expectedValues == null) continue;

            bool allMatch = true;
            foreach (var expectedValue in expectedValues)
            {
                if (!currentValues.TryGetValue(expectedValue.Key, out var currentValue))
                {
                    currentValue = null;
                }

                if (!ValueComparer.ValuesAreEqual(currentValue, expectedValue.Value))
                {
                    allMatch = false;
                    break;
                }
            }

            if (allMatch && expectedValues.Count > 0)
            {
                return index;
            }
        }

        // No option matched. Fall back to the IsDefault option when either:
        //  - every backing registry value is absent (a pristine system is the Windows default), or
        //  - the setting opts in via ResolveUnmatchedToDefault (its default state isn't a single
        //    enumerable value, so any unrecognised state is treated as the default).
        bool allBackingValuesAbsent = currentValues.Count > 0 && currentValues.Values.All(v => v is null);
        if (allBackingValuesAbsent || setting.ResolveUnmatchedToDefault)
        {
            for (int i = 0; i < options.Count; i++)
            {
                if (options[i].IsDefault)
                {
                    return i;
                }
            }
        }

        return ComboBoxConstants.CustomStateIndex;
    }

}
