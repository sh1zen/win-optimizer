using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Navigation;
using WinOptimizer.UI.Features.Drivers.ViewModels;

namespace WinOptimizer.UI.Features.Drivers;

/// <summary>Hosts driver review and cancels work when navigation leaves.</summary>
public sealed partial class DriversPage : Page
{
    public DriversViewModel ViewModel { get; } = App.Services.GetRequiredService<DriversViewModel>();
    public DriversPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.InitializeAsync();
    }

    /// <summary>Adapts actions and category navigation to the available page width.</summary>
    private void LayoutRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wide = e.NewSize.Width >= 860;
        WideLayoutTrigger.IsActive = wide;
        CompactLayoutTrigger.IsActive = !wide;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) { ViewModel.Dispose(); base.OnNavigatedFrom(e); }
}
