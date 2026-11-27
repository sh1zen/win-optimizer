using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.UI.Features.Management.ViewModels;

namespace WinOptimizer.UI.Features.Management;

/// <summary>Displays the outcome and available action for the latest servicing report.</summary>
public sealed partial class WindowsStatusResultsPage : Page
{
    public WindowsStatusViewModel ViewModel { get; }

    public WindowsStatusResultsPage()
    {
        ViewModel = App.Services.GetRequiredService<WindowsStatusViewModel>();
        InitializeComponent();
    }

    private Visibility ShowText(string text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Keeps the action right-aligned while giving the description the full width on narrow layouts.</summary>
    private void ReportLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 560;
        Grid.SetColumnSpan(ReportContent, narrow ? 2 : 1);
        Grid.SetRow(ReportRepairButton, narrow ? 1 : 0);
        ReportRepairButton.Margin = narrow ? new Thickness(0, 12, 0, 0) : default;
    }
}
