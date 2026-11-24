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

    /// <summary>Places the scan action beside its guidance and shows a category sidebar when the page is wide.</summary>
    private void LayoutRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wide = e.NewSize.Width >= 900;
        WideLayoutTrigger.IsActive = wide;
        CompactLayoutTrigger.IsActive = !wide;
        CategoryColumn.Width = new GridLength(wide ? 264 : 0);
        ResultsLayout.ColumnSpacing = wide ? (double)Microsoft.UI.Xaml.Application.Current.Resources["PageCardSpacing"] : 0;
        CategorySidebar.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        CategoryPicker.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(ResultsCard, wide ? 0 : 1);
        Grid.SetRowSpan(ResultsCard, wide ? 2 : 1);
    }

    /// <summary>Reveals the unavailable finding after its category is bound to the list.</summary>
    private void ViewUnavailable_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ViewUnavailable() is { } row)
            DispatcherQueue.TryEnqueue(() => FindingsList.ScrollIntoView(row));
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) { ViewModel.Deactivate(); base.OnNavigatedFrom(e); }
}
