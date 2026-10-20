using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Core.Features.Common.Helpers;

/// <summary>
/// Windows compatibility rules shared by setting discovery, import and bulk actions.
/// When a setting has both a major-build bound and a revision bound, the revision is
/// only compared when the current major build equals the boundary.
/// </summary>
/// <remarks>
/// A null revision bound means "no revision constraint at this boundary" — i.e. any
/// revision of the boundary build is accepted.
/// </remarks>
public static class BuildVersionGate
{
    /// <summary>
    /// Returns the compatibility message token for an unsupported setting, or null.
    /// OS restrictions apply first; explicit supported ranges take precedence over bounds.
    /// </summary>
    public static string? GetCompatibilityMessage(
        SettingDefinition setting, bool isWindows11, int currentBuild, int currentRevision)
    {
        if (setting.IsWindows10Only && isWindows11) return "Compatibility_Windows10Only";
        if (setting.IsWindows11Only && !isWindows11) return "Compatibility_Windows11Only";

        if (setting.SupportedBuildRanges is { Count: > 0 } ranges)
        {
            return ranges.Any(range => currentBuild >= range.MinBuild && currentBuild <= range.MaxBuild)
                ? null
                : $"Compatibility_BuildRange|{string.Join(" or ", ranges.Select(range => $"{range.MinBuild}-{range.MaxBuild}"))}";
        }

        if (!IsCompatible(currentBuild, currentRevision,
                setting.MinimumBuildNumber, setting.MinimumBuildRevision, null, null))
        {
            var boundary = currentBuild == setting.MinimumBuildNumber && setting.MinimumBuildRevision.HasValue
                ? $"{setting.MinimumBuildNumber}.{setting.MinimumBuildRevision}"
                : $"{setting.MinimumBuildNumber}";
            return $"Compatibility_MinBuild|{boundary}";
        }
        if (!IsCompatible(currentBuild, currentRevision,
                null, null, setting.MaximumBuildNumber, setting.MaximumBuildRevision))
        {
            var boundary = currentBuild == setting.MaximumBuildNumber && setting.MaximumBuildRevision.HasValue
                ? $"{setting.MaximumBuildNumber}.{setting.MaximumBuildRevision}"
                : $"{setting.MaximumBuildNumber}";
            return $"Compatibility_MaxBuild|{boundary}";
        }
        return null;
    }

    /// <summary>
    /// Returns true when (currentBuild, currentRevision) falls inside the bounds
    /// described by the four nullable bound values. Bounds that are null are ignored.
    /// </summary>
    public static bool IsCompatible(
        int currentBuild,
        int currentRevision,
        int? minBuild,
        int? minRevision,
        int? maxBuild,
        int? maxRevision)
    {
        if (minBuild.HasValue)
        {
            if (currentBuild < minBuild.Value) return false;
            if (currentBuild == minBuild.Value
                && minRevision.HasValue
                && currentRevision < minRevision.Value)
            {
                return false;
            }
        }

        if (maxBuild.HasValue)
        {
            if (currentBuild > maxBuild.Value) return false;
            if (currentBuild == maxBuild.Value
                && maxRevision.HasValue
                && currentRevision > maxRevision.Value)
            {
                return false;
            }
        }

        return true;
    }
}
