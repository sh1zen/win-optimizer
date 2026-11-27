using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.UI.Features.Management.ViewModels;

namespace WinOptimizer.UI.Features.Management;

/// <summary>Shows the same servicing progress and cancellation on the overview and report.</summary>
public sealed partial class WindowsStatusProgressView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(WindowsStatusViewModel), typeof(WindowsStatusProgressView), new PropertyMetadata(null));

    public WindowsStatusViewModel? ViewModel
    {
        get => (WindowsStatusViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public WindowsStatusProgressView() => InitializeComponent();
}
