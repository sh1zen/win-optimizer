using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinOptimizer.UI.Features.Cleaner.ViewModels;

namespace WinOptimizer.UI.Features.Cleaner;

/// <summary>Reviews the shared completed scan and hosts selection and confirmed cleanup.</summary>
public sealed partial class CleanerResultsPage : Page
{
    public CleanerViewModel ViewModel { get; }

    public CleanerResultsPage() : this(App.Services.GetRequiredService<CleanerViewModel>()) { }

    public CleanerResultsPage(CleanerViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // A results route is only valid while a completed scan is available.
        if (!ViewModel.HasScanned) ReturnToScan();
    }

    /// <summary>Sizes category navigation and review actions using the available page width.</summary>
    private void LayoutRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wide = e.NewSize.Width >= 720;
        WideLayoutTrigger.IsActive = wide;
        CompactLayoutTrigger.IsActive = !wide;
    }

    private void ReturnToScan()
    {
        if (Frame.BackStack.LastOrDefault()?.SourcePageType == typeof(CleanerPage))
            Frame.GoBack();
        else
            Frame.Navigate(typeof(CleanerPage));
    }
}
