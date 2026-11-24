using System.Globalization;

namespace WinOptimizer.UI.Features.Common.Utilities;

/// <summary>Formats byte totals using binary units and the caller's display culture.</summary>
internal static class ByteSizeFormatter
{
    private static readonly string[] Units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];

    internal static string FormatBytes(decimal value, CultureInfo culture)
    {
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1) { value /= 1024; unit++; }
        return value.ToString(unit == 0 ? "N0" : "N2", culture) + " " + Units[unit];
    }
}
