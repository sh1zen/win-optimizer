using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinOptimizer.UI.Features.AdvancedTools.ViewModels;
using WinOptimizer.UI.Features.Common.Helpers;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Models;
using WinOptimizer.UI.Helpers;
using WinOptimizer.UI.Features.Restore;
using WinOptimizer.UI.Features.SystemInformation;

namespace WinOptimizer.UI.Features.AdvancedTools;

/// <summary>
/// Page displaying advanced tools with overview cards and detail navigation.
/// </summary>
public sealed partial class AdvancedToolsPage : Page, ISectionNavigationPage
{
    public AdvancedToolsViewModel ViewModel { get; }
    public bool IsInDetailPage => ViewModel.IsInDetailPage;

    public AdvancedToolsPage()
    {
        this.InitializeComponent();
        ViewModel = App.Services.GetRequiredService<AdvancedToolsViewModel>();

        // PageUp/PageDown fast-scroll + Home/End jump for the overview scroller (issue #581).
        PageScrollHelper.Attach(this, OverviewScrollView);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ShowDestination(e.Parameter as SectionPageNavigation);
    }

    /// <summary>Records tool navigation alongside the shell's other section destinations.</summary>
    public void NavigateToSection(string sectionKey)
    {
        NavigationRouter.NavigateWithinSection(Frame, new SectionPageNavigation(sectionKey));
    }

    /// <summary>Restores the tool associated with the current shell journal entry.</summary>
    private void ShowDestination(SectionPageNavigation? destination)
    {
        Type? pageType = destination?.SectionKey switch
        {
            "WimUtil" => typeof(WimUtilPage),
            "AutounattendXml" => typeof(AutounattendGeneratorPage),
            _ => null
        };

        if (pageType != null)
        {
            InnerContentFrame.Navigate(pageType);
        }
        else
        {
            ViewModel.CurrentSectionKey = "Overview";
            InnerContentFrame.Content = null;
            UpdateContentVisibility();
        }
    }

    /// <summary>Opens this section's home as a native journal destination.</summary>
    public void NavigateToOverview()
    {
        NavigationRouter.NavigateWithinSection(Frame);
    }

    private void InnerContentFrame_Navigated(object sender, NavigationEventArgs e)
    {
        ViewModel.CurrentSectionKey = e.SourcePageType.Name switch
        {
            nameof(WimUtilPage) => "WimUtil",
            nameof(AutounattendGeneratorPage) => "AutounattendXml",
            _ => "Overview"
        };

        UpdateContentVisibility();
    }

    private void UpdateContentVisibility()
    {
        var isInDetailPage = ViewModel.IsInDetailPage;

        OverviewContent.Visibility = isInDetailPage ? Visibility.Collapsed : Visibility.Visible;
        InnerContentFrame.Visibility = isInDetailPage ? Visibility.Visible : Visibility.Collapsed;
    }

    // Overview card click handlers
    private void WimUtilCard_Click(object sender, RoutedEventArgs e)
    {
        NavigateToSection("WimUtil");
    }

    private void AutounattendXmlCard_Click(object sender, RoutedEventArgs e)
    {
        NavigateToSection("AutounattendXml");
    }

    /// <summary>Opens recovery in the shell's Back/Forward navigation history.</summary>
    private void RestoreCard_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(RestorePage));
    }

    /// <summary>Opens the machine inventory while retaining Advanced Tools as its sidebar owner.</summary>
    private void SystemInformationCard_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(SystemInformationPage));
    }

}
