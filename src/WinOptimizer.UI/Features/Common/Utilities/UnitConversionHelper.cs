namespace WinOptimizer.UI.Features.Common.Utilities;

/// <summary>
/// Shared utility for converting power setting values between system units and display units.
/// </summary>
internal static class UnitConversionHelper
{
    /// <summary>Converts authored display values to the units stored in configuration files.</summary>
    public static int ConvertToSystemUnits(int displayValue, string? displayUnits) =>
        displayUnits?.ToLowerInvariant() switch
        {
            "minutes" => checked(displayValue * 60),
            "hours" => checked(displayValue * 3600),
            _ => displayValue
        };

    /// <summary>
    /// Converts a raw powercfg API value to display units based on the display unit string.
    /// For example, converts 1200 seconds to 20 minutes when display units are "Minutes".
    /// </summary>
    public static int ConvertFromSystemUnits(int systemValue, string? displayUnits)
    {
        return displayUnits?.ToLowerInvariant() switch
        {
            "minutes" => systemValue / 60,        // powercfg stores time in seconds
            "hours" => systemValue / 3600,
            // USB selective suspend timeouts use milliseconds in both the registry and UI.
            "milliseconds" => systemValue,
            _ => systemValue
        };
    }
}
