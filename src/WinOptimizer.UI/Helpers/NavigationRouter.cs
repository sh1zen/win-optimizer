using Microsoft.UI.Xaml.Controls;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Services;
using WinOptimizer.UI.Features.AdvancedTools;
using WinOptimizer.UI.Features.Common.Controls;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Customize;
using WinOptimizer.UI.Features.Optimize;
using WinOptimizer.UI.Features.Settings;
using WinOptimizer.UI.Features.Restore;
using WinOptimizer.UI.Features.Cleaner;
using WinOptimizer.UI.Features.Drivers;
using WinOptimizer.UI.Features.SystemInformation;
using WinOptimizer.UI.Features.Extensions;
using WinOptimizer.UI.Features.Utilities;
using WinOptimizer.UI.Features.Management;
using WinOptimizer.UI.Features.SoftwareApps;
using System;
using System.Collections.Generic;

namespace WinOptimizer.UI.Helpers;

/// <summary>
/// Handles page navigation routing, tag-to-Type mapping, and review mode feature tracking.
/// Extracted from MainWindow to reduce code-behind complexity.
/// </summary>
internal sealed class NavigationRouter
{
    private readonly IConfigReviewService? _configReviewService;
    private readonly INavBadgeService? _navBadgeService;
    private readonly IDispatcherService _dispatcher;

    private static readonly Dictionary<string, Type> TagToPageType = new()
    {
        ["Settings"] = typeof(SettingsPage),
        ["Restore"] = typeof(RestorePage),
        ["Optimize"] = typeof(OptimizePage),
        ["Cleaner"] = typeof(CleanerPage),
        ["Drivers"] = typeof(DriversPage),
        ["SystemInformation"] = typeof(SystemInformationPage),
        ["Functionalities"] = typeof(UtilitiesPage),
        ["Management"] = typeof(ManagementPage),
        ["Security"] = typeof(WinOptimizer.UI.Features.Security.SecurityPage),
        ["EventsCleanup"] = typeof(EventsCleanupPage),
        ["WindowsStatus"] = typeof(WindowsStatusPage),
        ["Autoruns"] = typeof(AutorunsPage),
        ["ProcessMonitor"] = typeof(ProcessMonitorPage),
        ["SysMonitor"] = typeof(SystemMonitorSettingsPage),
        ["Customize"] = typeof(CustomizePage),
        ["AdvancedTools"] = typeof(AdvancedToolsPage),
        ["SoftwareApps"] = typeof(SoftwareAppsPage),
    };

    private static readonly Dictionary<string, string> PageTypeNameToTag = new()
    {
        [nameof(SettingsPage)] = "Settings",
        [nameof(RestorePage)] = "AdvancedTools",
        [nameof(OptimizePage)] = "Optimize",
        [nameof(WinOptimizer.UI.Features.Optimize.Pages.NetworkOptimizePage)] = "Optimize",
        [nameof(WinOptimizer.UI.Features.Optimize.Pages.FeaturesOptimizePage)] = "Optimize",
        [nameof(CleanerPage)] = "Cleaner",
        [nameof(DriversPage)] = "Drivers",
        [nameof(SystemInformationPage)] = "AdvancedTools",
        [nameof(UtilitiesPage)] = "Functionalities",
        [nameof(EnvironmentEditorPage)] = "Management",
        [nameof(HostsEditorPage)] = "Management",
        [nameof(BatteryChargePage)] = "Management",
        [nameof(ManagementPage)] = "Management",
        [nameof(WinOptimizer.UI.Features.Security.SecurityPage)] = "Security",
        [nameof(EventsCleanupPage)] = "Cleaner",
        [nameof(WindowsStatusPage)] = "Management",
        [nameof(AutorunsPage)] = "Management",
        [nameof(ProcessMonitorPage)] = "Management",
        [nameof(ConnectionPage)] = "Management",
        [nameof(SavedWifiPage)] = "Management",
        [nameof(SystemMonitorSettingsPage)] = "Functionalities",
        [nameof(CleanerResultsPage)] = "Cleaner",
        [nameof(CustomizePage)] = "Customize",
        [nameof(WinOptimizer.UI.Features.Customize.Pages.ContextMenuCustomizePage)] = "Customize",
        [nameof(AdvancedToolsPage)] = "AdvancedTools",
        [nameof(SoftwareAppsPage)] = "SoftwareApps",
    };

    public NavigationRouter(
        IConfigReviewService? configReviewService,
        INavBadgeService? navBadgeService,
        IDispatcherService dispatcher)
    {
        _configReviewService = configReviewService;
        _navBadgeService = navBadgeService;
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Navigates the content frame to the page corresponding to the given tag.
    /// Sidebar destinations open their section overview; repeated overviews do not add entries.
    /// </summary>
    /// <param name="frame">The content frame to navigate.</param>
    /// <param name="tag">The navigation tag (e.g., "SoftwareApps", "Optimize").</param>
    /// <param name="parameter">Optional navigation parameter.</param>
    public void NavigateToPage(Frame frame, string? tag, object? parameter = null)
    {
        StartupLogger.Log("NavigationRouter", $"NavigateToPage called with tag: {tag}");

        if (tag == null || !TagToPageType.TryGetValue(tag, out var pageType))
        {
            StartupLogger.Log("NavigationRouter", $"Skipping navigation - unknown tag: {tag}");
            return;
        }

        StartupLogger.Log("NavigationRouter", $"Resolved page type: {pageType.Name}");

        if (CanNavigateTo(pageType))
        {
            try
            {
                StartupLogger.Log("NavigationRouter", $"Navigating to {pageType.Name}...");
                var result = NavigateToDestination(frame, pageType, parameter);
                StartupLogger.Log("NavigationRouter", $"Navigate result: {result}");

            }
            catch (Exception ex)
            {
                StartupLogger.Log("NavigationRouter", $"Navigation EXCEPTION: {ex}");
            }
        }
        else
        {
            StartupLogger.Log("NavigationRouter", "Skipping navigation - restricted destination");
        }
    }

    /// <summary>Records a nested destination on its owning section's shell entry, retaining native Back/Forward.</summary>
    public static void NavigateWithinSection(Frame frame, object? destination = null)
    {
        NavigateToDestination(frame, frame.CurrentSourcePageType, destination);
    }

    /// <summary>Shares overview reset and duplicate-entry policy between sidebar and nested navigation.</summary>
    private static bool NavigateToDestination(Frame frame, Type pageType, object? parameter)
    {
        if (frame.CurrentSourcePageType == pageType && parameter == null &&
            frame.Content is not ISectionNavigationPage { IsInDetailPage: true }) return false;
        return frame.Navigate(pageType, parameter);
    }

    /// <summary>Applies the same review restriction to sidebar routes and journal destinations.</summary>
    public bool CanNavigateTo(Type? pageType) => pageType != null &&
        !(_configReviewService?.IsInReviewMode == true && pageType == typeof(AdvancedToolsPage));

    /// <summary>Tracks completed visits equally for new navigation, Back and Forward.</summary>
    public void OnNavigated(Type? pageType, Action applyNavBadges)
    {
        if (pageType != typeof(SoftwareAppsPage) || _configReviewService?.IsInReviewMode != true) return;
        _configReviewService.MarkFeatureVisited(FeatureIds.WindowsApps);
        _configReviewService.MarkFeatureVisited(FeatureIds.ExternalApps);
        _navBadgeService?.SubscribeToSoftwareAppsChanges(() => _dispatcher.RunOnUIThread(applyNavBadges));
    }

    /// <summary>
    /// Gets the navigation tag for the currently displayed page type.
    /// </summary>
    public string? GetTagForCurrentPage(Type? pageType)
    {
        if (pageType == null) return null;
        return PageTypeNameToTag.GetValueOrDefault(pageType.Name);
    }
}
