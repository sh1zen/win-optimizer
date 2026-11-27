using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.UI.Features.Common.Helpers;
using WinOptimizer.UI.ViewModels;

namespace WinOptimizer.UI.Features.Common.Controls;

/// <summary>Opens the shared task controls from a title-bar button during activity.</summary>
public sealed partial class TaskProgressControl : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(TaskProgressViewModel), typeof(TaskProgressControl), new PropertyMetadata(null));

    public TaskProgressViewModel? ViewModel
    {
        get => (TaskProgressViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public TaskProgressControl()
    {
        InitializeComponent();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            if (Visibility == Visibility.Collapsed) PopupHandler.SetIsOpen(TaskStatusButton, false);
        });
    }
}
