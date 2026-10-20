using System.Collections.ObjectModel;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Models;
using WinOptimizer.UI.Features.Common.ViewModels;
using WinOptimizer.UI.Features.Optimize.ViewModels;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

/// <summary>Verifies that shared category cards preserve live feature and configuration-review state.</summary>
public sealed class SectionNavigationItemTests
{
    [Theory]
    [InlineData(false, 3, false, true, false, false, 0)]
    [InlineData(true, 3, false, true, true, false, 2)]
    [InlineData(true, 3, true, true, false, true, 0)]
    [InlineData(true, 0, false, true, false, true, 0)]
    [InlineData(true, 0, false, false, false, false, 0)]
    public void RefreshPreservesReviewBadgeSemantics(bool reviewing, int differences, bool fullyReviewed,
        bool inConfig, bool pending, bool reviewed, int count)
    {
        var feature = new Mock<ISettingsFeatureViewModel>();
        feature.SetupGet(vm => vm.Settings).Returns(new ObservableCollection<SettingItemViewModel>());
        var review = new Mock<IConfigReviewService>();
        review.SetupGet(service => service.IsInReviewMode).Returns(reviewing);
        review.Setup(service => service.GetFeatureDiffCount(FeatureIds.Privacy)).Returns(differences);
        review.Setup(service => service.GetFeaturePendingDiffCount(FeatureIds.Privacy)).Returns(2);
        review.Setup(service => service.IsFeatureFullyReviewed(FeatureIds.Privacy)).Returns(fullyReviewed);
        review.Setup(service => service.IsFeatureInConfig(FeatureIds.Privacy)).Returns(inConfig);
        var route = new SectionPageRoute(OptimizeViewModel.Sections[0], typeof(object));
        var item = new SectionNavigationItem(route, feature.Object, Mock.Of<ILocalizationService>(), review.Object);

        item.Refresh();

        Assert.Same(route, item.Route);
        Assert.Same(feature.Object, item.Feature);
        Assert.Equal(pending, item.ShowPending);
        Assert.Equal(reviewed, item.ShowReviewed);
        Assert.Equal(count, item.PendingCount);
        Assert.False(item.ShowSummary);
    }

    [Fact]
    public void RefreshPublishesLiveNamesLocalizationAndReviewExitToEveryBinding()
    {
        var feature = new Mock<ISettingsFeatureViewModel>();
        feature.SetupGet(vm => vm.Settings).Returns(new ObservableCollection<SettingItemViewModel>());
        feature.SetupGet(vm => vm.DisplayName).Returns("Original");
        var strings = new Mock<ILocalizationService>();
        strings.Setup(service => service.GetString(It.IsAny<string>())).Returns("Original");
        var review = new Mock<IConfigReviewService>();
        review.SetupGet(service => service.IsInReviewMode).Returns(true);
        review.Setup(service => service.GetFeatureDiffCount(FeatureIds.Privacy)).Returns(2);
        var item = new SectionNavigationItem(
            new SectionPageRoute(OptimizeViewModel.Sections[0], typeof(object)), feature.Object, strings.Object, review.Object);
        item.Refresh();
        Assert.True(item.ShowPending);
        string? changed = null;
        item.PropertyChanged += (_, args) => changed = args.PropertyName;

        feature.SetupGet(vm => vm.DisplayName).Returns("Localized title");
        feature.SetupGet(vm => vm.GroupDescriptionText).Returns("Localized groups");
        strings.Setup(service => service.GetString(It.IsAny<string>())).Returns("Localized");
        review.SetupGet(service => service.IsInReviewMode).Returns(false);
        item.Refresh();

        Assert.Equal(string.Empty, changed);
        Assert.Equal("Localized title", item.Title);
        Assert.Equal("Localized groups", item.Description);
        Assert.Equal("Localized 0/0", item.RecommendedText);
        Assert.False(item.ShowPending);
        Assert.False(item.ShowReviewed);
    }
}
