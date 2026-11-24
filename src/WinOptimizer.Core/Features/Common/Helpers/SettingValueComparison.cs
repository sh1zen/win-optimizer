using System;

namespace WinOptimizer.Core.Features.Common.Helpers;

/// <summary>Compares observed setting values with the corresponding catalogue values.</summary>
public static class SettingValueComparison
{
    /// <summary>Matches equal values, equivalent integer representations or case-insensitive text.</summary>
    public static bool AreEqual(object? actual, object? expected)
    {
        if (actual == null && expected == null) return true;
        if (actual == null || expected == null) return false;
        if (Equals(actual, expected)) return true;
        try
        {
            return Convert.ToInt64(actual) == Convert.ToInt64(expected);
        }
        catch
        {
            return string.Equals(actual.ToString(), expected.ToString(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
