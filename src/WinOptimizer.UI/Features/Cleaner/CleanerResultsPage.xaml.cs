using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinOptimizer.UI.Features.Cleaner.ViewModels;

namespace WinOptimizer.UI.Features.Cleaner;

/// <summary>Reviews the shared completed scan and hosts selection and confirmed cleanup.</summary>
public sealed partial class CleanerResultsPage : Page
{
    public CleanerViewModel ViewModel { get; } = App.Services.GetRequiredService<CleanerViewModel>();

    public CleanerResultsPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // A results route is only valid while a completed scan is available.
        if (!ViewModel.HasScanned) ReturnToScan();
    }

    /// <summary>Stacks review actions using page width, including sidebar and app zoom.</summary>
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
