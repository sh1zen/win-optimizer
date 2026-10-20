using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.UI.Features.Drivers.ViewModels;

namespace WinOptimizer.UI.Features.Drivers;

/// <summary>Edits the shared saved driver update filter.</summary>
public sealed partial class DriverSettingsControl : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(nameof(ViewModel),
        typeof(DriverSettingsViewModel), typeof(DriverSettingsControl), new PropertyMetadata(null));
    public DriverSettingsViewModel ViewModel
    {
        get => (DriverSettingsViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }
    public DriverSettingsControl()
    {
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.LoadAsync();
    }
}
