using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.UI.Features.Management.ViewModels;

namespace WinOptimizer.UI.Features.Management;

/// <summary>Scopes sampling and ETW ownership to the visible process-monitor page.</summary>
public sealed partial class ProcessMonitorPage : Page
{
    public ProcessMonitorViewModel ViewModel { get; }
    public ProcessMonitorPage() : this(App.Services.GetRequiredService<ProcessMonitorViewModel>()) { }
    public ProcessMonitorPage(ProcessMonitorViewModel viewModel)
    {
        ViewModel = viewModel; InitializeComponent(); PageRoot.Height = 1200;
        SizeChanged += (_, args) => UpdateViewport(args.NewSize.Width, args.NewSize.Height);
        Loaded += async (_, _) => { UpdateViewport(ActualWidth, ActualHeight); await ViewModel.ActivateAsync(); };
        Unloaded += async (_, _) => await ViewModel.DeactivateAsync();
    }
    /// <summary>Uses the actual content width, including the space consumed by the application sidebar.</summary>
    private void UpdateViewport(double width, double height)
    {
        var wide = width >= 1000;
        PageRoot.Height = Math.Max(wide ? 750 : 1200, height);
        Inspector.Height = wide ? double.NaN : 600;
        VisualStateManager.GoToState(this, wide ? "Wide" : "Narrow", false);
    }
    private async void ProcessesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (e.AddedItems.FirstOrDefault() is ProcessDisplayRow row) await ViewModel.SelectAsync(row); }
    private void ExpandProcess_Click(object sender, RoutedEventArgs e)
    { if (sender is FrameworkElement { DataContext: ProcessDisplayRow row }) ViewModel.ToggleExpanded(row); }
}
