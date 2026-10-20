using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media.Animation;
using WinOptimizer.UI.Features.Settings.ViewModels;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Helpers;

namespace WinOptimizer.UI.Features.Settings;

/// <summary>
/// Settings page for application configuration.
/// </summary>
public sealed partial class SettingsPage : Page, ISectionNavigationPage
{
    public bool IsInDetailPage => ViewModel.HasSelection;
    /// <summary>
    /// Gets the ViewModel for this page.
    /// </summary>
    public SettingsViewModel ViewModel { get; }

    /// <summary>
    /// Creates a new instance of the SettingsPage.
    /// </summary>
    public SettingsPage()
    {
        // Resolve ViewModel from DI container
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();

        this.InitializeComponent();
        Loaded += async (_, _) => await ViewModel.InitializeAsync();

        // Category navigation retains the existing controls and preference owners.
        NavigationCacheMode = NavigationCacheMode.Enabled;
    }

    /// <summary>Uses side-by-side categories on desktop widths and hierarchical navigation on narrow pages.</summary>
    private void LayoutRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ViewModel.IsWideLayout = e.NewSize.Width >= 900;
        WideLayoutTrigger.IsActive = ViewModel.IsWideLayout;
        CompactLayoutTrigger.IsActive = !ViewModel.IsWideLayout;
    }

    /// <summary>Restores category and search context when traversing the shell's native journal.</summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var destination = e.Parameter as SettingsPageNavigation;
        ViewModel.Query = destination?.Query ?? "";
        ViewModel.SelectedCategory = destination == null ? null :
            ViewModel.Categories.Single(item => item.Category == destination.Category);
    }

    /// <summary>Updates category content in place and releases the cached page when leaving Settings.</summary>
    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        base.OnNavigatingFrom(e);
        var withinSettings = e.SourcePageType == typeof(SettingsPage);
        NavigationCacheMode = withinSettings ? NavigationCacheMode.Enabled : NavigationCacheMode.Disabled;
        Transitions = withinSettings
            ? new TransitionCollection { new NavigationThemeTransition { DefaultNavigationTransitionInfo = new SuppressNavigationTransitionInfo() } }
            : null;
    }

    /// <summary>Includes category selection in Back/Forward while retaining the category's search context.</summary>
    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Frame != null && e.AddedItems.FirstOrDefault() is SettingsCategoryItem item && item != ViewModel.SelectedCategory)
            NavigationRouter.NavigateWithinSection(Frame, new SettingsPageNavigation(item.Category, ViewModel.Query));
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (e.SourcePageType != typeof(SettingsPage)) ViewModel.Dispose();
        base.OnNavigatedFrom(e);
    }
}

/// <summary>Stores the selected preference category and its originating search in the shell journal.</summary>
public sealed record SettingsPageNavigation(SettingsCategory Category, string Query);
