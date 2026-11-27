using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
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

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        base.OnNavigatedFrom(e);
    }

    /// <summary>Opens a completed report while this page owns the navigation frame.</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.Report)) OpenReport();
    }

    private void ReviewReport_Click(object sender, RoutedEventArgs e) => OpenReport();

    private void OpenReport()
    {
        if (ViewModel.HasReport && Frame is { } frame && ReferenceEquals(frame.Content, this))
            frame.Navigate(typeof(WindowsStatusResultsPage));
    }

    /// <summary>Places the scan action and servicing cards side by side when each has enough space.</summary>
    private void LayoutRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wide = e.NewSize.Width >= 760;
        WideLayoutTrigger.IsActive = wide;
        CompactLayoutTrigger.IsActive = !wide;
        var cardsFitSideBySide = e.NewSize.Width >= 980;
        SideBySideCardsTrigger.IsActive = cardsFitSideBySide;
        StackedCardsTrigger.IsActive = !cardsFitSideBySide;
    }
}
