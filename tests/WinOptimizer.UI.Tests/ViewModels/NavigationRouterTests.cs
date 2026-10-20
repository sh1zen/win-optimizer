using Moq;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Features.AdvancedTools;
using WinOptimizer.UI.Features.Cleaner;
using WinOptimizer.UI.Features.Extensions;
using WinOptimizer.UI.Features.Restore;
using WinOptimizer.UI.Features.SystemInformation;
using WinOptimizer.UI.Features.Utilities;
using WinOptimizer.UI.Features.Management;
using WinOptimizer.UI.Features.SoftwareApps;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Helpers;
using WinOptimizer.UI.Tests.Helpers;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

/// <summary>Exercises policies shared by sidebar navigation and the native Frame journal.</summary>
public sealed class NavigationRouterTests
{
    [Fact]
    public void DetailAndResultPagesKeepTheirOwningSidebarSectionSelected()
    {
        var router = new NavigationRouter(null, null, new ExtensionTestDispatcher());
        Assert.Equal("Cleaner", router.GetTagForCurrentPage(typeof(CleanerResultsPage)));
        Assert.Equal("Cleaner", router.GetTagForCurrentPage(typeof(EventsCleanupPage)));
        Assert.Equal("Security", router.GetTagForCurrentPage(typeof(WinOptimizer.UI.Features.Security.SecurityPage)));
        Assert.Equal("Customize", router.GetTagForCurrentPage(typeof(WinOptimizer.UI.Features.Customize.Pages.ContextMenuCustomizePage)));
        Assert.Equal("Optimize", router.GetTagForCurrentPage(typeof(WinOptimizer.UI.Features.Optimize.Pages.NetworkOptimizePage)));
        Assert.Equal("Optimize", router.GetTagForCurrentPage(typeof(WinOptimizer.UI.Features.Optimize.Pages.FeaturesOptimizePage)));
        Assert.Equal("AdvancedTools", router.GetTagForCurrentPage(typeof(RestorePage)));
        Assert.Equal("AdvancedTools", router.GetTagForCurrentPage(typeof(SystemInformationPage)));
        foreach (var page in new[] { typeof(UtilitiesPage), typeof(SystemMonitorSettingsPage) })
            Assert.Equal("Functionalities", router.GetTagForCurrentPage(page));
        foreach (var page in new[] { typeof(ManagementPage), typeof(EnvironmentEditorPage), typeof(HostsEditorPage), typeof(BatteryChargePage), typeof(ConnectionPage), typeof(SavedWifiPage), typeof(WindowsStatusPage), typeof(AutorunsPage), typeof(ProcessMonitorPage) })
            Assert.Equal("Management", router.GetTagForCurrentPage(page));
        Assert.Null(router.GetTagForCurrentPage(null));
        Assert.Null(router.GetTagForCurrentPage(typeof(NavigationRouterTests)));
    }

    [Fact]
    public void ReviewPolicyAppliesToHistoryDestinationsAndReevaluatesWhenReviewEnds()
    {
        var review = new Mock<IConfigReviewService>();
        review.SetupGet(service => service.IsInReviewMode).Returns(true);
        var router = new NavigationRouter(review.Object, null, new ExtensionTestDispatcher());
        Assert.False(router.CanNavigateTo(null));
        Assert.False(router.CanNavigateTo(typeof(AdvancedToolsPage)));
        Assert.True(router.CanNavigateTo(typeof(SoftwareAppsPage)));
        Assert.True(router.CanNavigateTo(typeof(SystemMonitorSettingsPage)));
        review.SetupGet(service => service.IsInReviewMode).Returns(false);
        Assert.True(router.CanNavigateTo(typeof(AdvancedToolsPage)));
    }

    [Fact]
    public void CompletedSoftwareAppsVisitsTrackReviewFeaturesAndDispatchBadgeUpdates()
    {
        var review = new Mock<IConfigReviewService>();
        review.SetupGet(service => service.IsInReviewMode).Returns(true);
        var badges = new Mock<INavBadgeService>();
        Action? changed = null;
        badges.Setup(service => service.SubscribeToSoftwareAppsChanges(It.IsAny<Action>()))
            .Callback<Action>(callback => changed = callback);
        var router = new NavigationRouter(review.Object, badges.Object, new ExtensionTestDispatcher());
        var updates = 0;
        router.OnNavigated(typeof(SystemMonitorSettingsPage), () => updates++);
        review.Verify(service => service.MarkFeatureVisited(It.IsAny<string>()), Times.Never);
        router.OnNavigated(typeof(SoftwareAppsPage), () => updates++);
        review.Verify(service => service.MarkFeatureVisited(FeatureIds.WindowsApps), Times.Once);
        review.Verify(service => service.MarkFeatureVisited(FeatureIds.ExternalApps), Times.Once);
        Assert.NotNull(changed);
        changed();
        Assert.Equal(1, updates);
        review.SetupGet(service => service.IsInReviewMode).Returns(false);
        router.OnNavigated(typeof(SoftwareAppsPage), () => updates++);
        review.Verify(service => service.MarkFeatureVisited(FeatureIds.WindowsApps), Times.Once);
        badges.Verify(service => service.SubscribeToSoftwareAppsChanges(It.IsAny<Action>()), Times.Once);
    }
}
