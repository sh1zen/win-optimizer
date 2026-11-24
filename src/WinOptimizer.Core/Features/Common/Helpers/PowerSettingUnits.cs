using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Core.Features.Common.Helpers;

/// <summary>Defines the relationship between display values and stored power setting values.</summary>
public static class PowerSettingUnits
{
    /// <summary>Uses the numeric range's units if present, then the primary PowerCfg entry.</summary>
    public static string GetDisplayUnits(SettingDefinition setting) =>
        setting.NumericRange?.Units ?? setting.PowerCfgSettings?[0]?.Units ?? string.Empty;

    /// <summary>Returns the number of stored units per displayed unit.</summary>
    public static int SystemUnitsPerDisplayUnit(string? displayUnits) =>
        displayUnits?.ToLowerInvariant() switch
        {
            "minutes" => 60,
            "hours" => 3600,
            // USB selective suspend uses milliseconds in both the registry and UI.
            _ => 1
        };

    /// <summary>Converts a stored value to display units, truncating fractional units.</summary>
    public static int ToDisplayValue(int systemValue, string? displayUnits) =>
        systemValue / SystemUnitsPerDisplayUnit(displayUnits);
}
