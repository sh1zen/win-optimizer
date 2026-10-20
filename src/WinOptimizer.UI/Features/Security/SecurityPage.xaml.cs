using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace WinOptimizer.UI.Features.Security;

/// <summary>Hosts explicit scans without starting Windows work during page construction.</summary>
public sealed partial class SecurityPage : Page
{
    public SecurityViewModel ViewModel { get; }
    public SecurityPage() : this(App.Services.GetRequiredService<SecurityViewModel>()) { }
    public SecurityPage(SecurityViewModel viewModel) { ViewModel = viewModel; InitializeComponent(); }

    /// <summary>Uses a category sidebar on wide pages and a picker when space is limited.</summary>
    private void LayoutRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wide = e.NewSize.Width >= 900;
        CategoryColumn.Width = new GridLength(wide ? 230 : 0);
        ResultsLayout.ColumnSpacing = wide ? 16 : 0;
        CategoryList.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        CategoryPicker.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Keeps finding descriptions readable by stacking status/recommendations in narrow result tables.</summary>
    private void FindingLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var row = (Grid)sender;
        var wide = e.NewSize.Width >= 650;
        TableHeaders.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumnSpan((FrameworkElement)row.FindName("FindingContent"), wide ? 1 : 3);
        ((FrameworkElement)row.FindName("FindingState")).Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        ((FrameworkElement)row.FindName("FindingRecommendation")).Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        ((FrameworkElement)row.FindName("CompactReview")).Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) { ViewModel.Deactivate(); base.OnNavigatedFrom(e); }
}
