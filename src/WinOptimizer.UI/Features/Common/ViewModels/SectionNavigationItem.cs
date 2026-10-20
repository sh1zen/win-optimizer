using CommunityToolkit.Mvvm.ComponentModel;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.Common.Helpers;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Models;

namespace WinOptimizer.UI.Features.Common.ViewModels;

/// <summary>Shares the live feature and badge presentation between overview cards and section navigation.</summary>
public sealed class SectionNavigationItem(
    SectionPageRoute route,
    ISettingsFeatureViewModel feature,
    ILocalizationService localization,
    IConfigReviewService review) : ObservableObject
{
    public SectionPageRoute Route { get; } = route;
    public ISettingsFeatureViewModel Feature { get; } = feature;
    public string Key => Route.Section.Key;
    public string Title => Feature.DisplayName;
    public string Description => Feature.GroupDescriptionText;
    public string IconResourceKey => Route.Section.IconGlyphKey;
    public FeatureBadgeSummary Summary { get; private set; } = new(0, 0, 0, 0);
    public bool ShowSummary => Summary.TotalWithBadgeData > 0;
    public bool ShowCustom => Summary.CustomCount > 0;
    public double RecommendedOpacity => Summary.RecommendedCount > 0 ? 1 : 0.4;
    public double DefaultOpacity => Summary.DefaultCount > 0 ? 1 : 0.4;
    public string RecommendedText => $"{localization.GetString("InfoBadge_Recommended") ?? "Recommended"} {Summary.RecommendedCount}/{Summary.TotalWithBadgeData}";
    public string DefaultText => $"{localization.GetString("InfoBadge_Default") ?? "Default"} {Summary.DefaultCount}/{Summary.TotalWithBadgeData}";
    public string CustomText => $"{localization.GetString("InfoBadge_Custom") ?? "Custom"} {Summary.CustomCount}/{Summary.TotalWithBadgeData}";
    public bool ShowReviewed { get; private set; }
    public bool ShowPending { get; private set; }
    public int PendingCount { get; private set; }

    /// <summary>Aggregates once per refresh and publishes the same result to every presentation.</summary>
    public void Refresh()
    {
        Summary = FeatureBadgeAggregator.Aggregate(Feature);
        var featureId = Route.Section.ModuleId;
        int differences = review.IsInReviewMode ? review.GetFeatureDiffCount(featureId) : 0;
        ShowReviewed = review.IsInReviewMode && (differences > 0
            ? review.IsFeatureFullyReviewed(featureId)
            : review.IsFeatureInConfig(featureId));
        ShowPending = review.IsInReviewMode && differences > 0 && !ShowReviewed;
        PendingCount = ShowPending ? review.GetFeaturePendingDiffCount(featureId) : 0;
        OnPropertyChanged(string.Empty);
    }
}
