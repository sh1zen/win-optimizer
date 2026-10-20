using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.UI.Features.Management.ViewModels;

namespace WinOptimizer.UI.Features.Management;

/// <summary>Shows servicing actions and retained results without starting maintenance on entry.</summary>
public sealed partial class WindowsStatusPage : Page
{
    public WindowsStatusViewModel ViewModel { get; }
    public WindowsStatusPage() : this(App.Services.GetRequiredService<WindowsStatusViewModel>()) { }
    public WindowsStatusPage(WindowsStatusViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    /// <summary>Places the main action beside the guidance when the page has sufficient width.</summary>
    private void LayoutRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wide = e.NewSize.Width >= 760;
        WideLayoutTrigger.IsActive = wide;
        CompactLayoutTrigger.IsActive = !wide;
    }
}
