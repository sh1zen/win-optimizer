using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.UI.Features.Utilities.ViewModels;

namespace WinOptimizer.UI.Features.Utilities;

/// <summary>Discovers battery support only on explicit page entry or Refresh.</summary>
public sealed partial class BatteryChargePage : Page
{
    public BatteryChargeViewModel ViewModel { get; } = App.Services.GetRequiredService<BatteryChargeViewModel>();
    public BatteryChargePage() { InitializeComponent(); Loaded += async (_, _) => await ViewModel.LoadAsync(); }

    /// <summary>Places sensor panels beside each other only when the actual content viewport has room.</summary>
    private void TelemetryPanels_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var grid = (Grid)sender;
        var wide = e.NewSize.Width >= 760;
        grid.ColumnDefinitions[1].Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        grid.ColumnSpacing = wide ? 24 : 0;
        var consumption = (StackPanel)grid.Children[1];
        Grid.SetColumn(consumption, wide ? 1 : 0);
        Grid.SetRow(consumption, wide ? 0 : 1);
    }
}
