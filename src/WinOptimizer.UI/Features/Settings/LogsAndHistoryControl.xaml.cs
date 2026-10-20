using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.UI.Features.Settings.ViewModels;

namespace WinOptimizer.UI.Features.Settings;

/// <summary>Displays selectable journal text inside the Settings details viewport.</summary>
public sealed partial class LogsAndHistoryControl : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(LogsAndHistoryViewModel), typeof(LogsAndHistoryControl), new PropertyMetadata(null));
    public LogsAndHistoryViewModel ViewModel
    {
        get => (LogsAndHistoryViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }
    public LogsAndHistoryControl() => InitializeComponent();
}
