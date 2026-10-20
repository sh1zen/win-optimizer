using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using System.Globalization;
using WinOptimizer.UI.Features.Optimize.ViewModels;

namespace WinOptimizer.UI.Features.Optimize.Pages;

/// <summary>Explicitly reads global network configuration when the tool page opens.</summary>
public sealed partial class NetworkOptimizePage : Page
{
    public NetworkOptimizationViewModel ViewModel { get; }
    public NetworkOptimizePage() : this(App.Services.GetRequiredService<NetworkOptimizationViewModel>()) { }

    /// <summary>Uses an explicit view model so native presentation checks can isolate the Windows boundary.</summary>
    public NetworkOptimizePage(NetworkOptimizationViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.RefreshAsync();
    }

    private async void Setting_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch { IsLoaded: true, DataContext: NetworkTuningChoice { IsUpdating: false } choice } toggle)
        {
            await ViewModel.SetAsync(choice, toggle.IsOn ? "Enabled" : "Disabled");
            toggle.IsOn = choice.IsOn;
        }
    }

    private async void Setting_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { IsLoaded: true, DataContext: NetworkTuningChoice { IsUpdating: false } choice, SelectedItem: NetworkTuningOption option } selector)
        {
            await ViewModel.SetAsync(choice, option.Value);
            selector.SelectedItem = choice.SelectedOption;
        }
    }

    private async void Setting_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (sender is { IsLoaded: true, DataContext: NetworkTuningChoice { IsUpdating: false } choice } && !double.IsNaN(args.NewValue))
        {
            await ViewModel.SetAsync(choice, args.NewValue.ToString(CultureInfo.InvariantCulture));
            sender.Value = choice.NumberValue;
        }
    }

    private async void ClearOverride_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NetworkTuningChoice choice }) await ViewModel.SetAsync(choice, null);
    }
}
