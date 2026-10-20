using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.UI.Features.Optimize.ViewModels;

namespace WinOptimizer.UI.Features.Optimize.Pages;

/// <summary>Loads current feature restrictions and presents confirmed toggles on the right of each card.</summary>
public sealed partial class FeaturesOptimizePage : Page
{
    public FeatureOptimizationViewModel ViewModel { get; } = App.Services.GetRequiredService<FeatureOptimizationViewModel>();
    public FeaturesOptimizePage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.RefreshAsync();
    }
    private async void Feature_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch { IsLoaded: true, DataContext: FeatureOptimizationChoice choice } toggle)
        {
            await ViewModel.SetAsync(choice, toggle.IsOn);
            toggle.IsOn = choice.IsOn;
        }
    }
}
