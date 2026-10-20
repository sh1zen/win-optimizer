using WinOptimizer.Core.Features.Common.Interfaces;

namespace WinOptimizer.UI.Features.Common.Helpers;

/// <summary>Identifies settings in the fixed configuration review queue using the authoritative diff store.</summary>
public static class ReviewModeFilter
{
    /// <summary>
    /// Returns true if the setting is in the review queue and should be shown when
    /// configuration review is active. Backed by the service, never by per-VM flags.
    /// </summary>
    /// <param name="settingId">The id of the setting being filtered.</param>
    /// <param name="diffService">The review diff service. If null (DI not yet wired, or
    /// the view is being initialized), the predicate returns false.</param>
    public static bool ShouldShowInReviewQueue(string? settingId, IConfigReviewDiffService? diffService)
    {
        if (diffService == null || string.IsNullOrEmpty(settingId)) return false;
        return diffService.GetDiffForSetting(settingId) != null;
    }
}
