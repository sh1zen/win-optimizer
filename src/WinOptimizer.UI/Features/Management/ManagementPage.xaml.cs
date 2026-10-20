using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.UI.Features.Management.ViewModels;
using WinOptimizer.UI.Features.Utilities;

namespace WinOptimizer.UI.Features.Management;

/// <summary>Lists management tools as destinations in the shell's navigation journal.</summary>
public sealed partial class ManagementPage : Page
{
    public ManagementViewModel ViewModel { get; }

    public ManagementPage() : this(App.Services.GetRequiredService<ManagementViewModel>()) { }

    /// <summary>Builds the overview without starting connection discovery.</summary>
    public ManagementPage(ManagementViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    /// <summary>Opens environment-variable editing within Control's navigation journal.</summary>
    private void EnvironmentCard_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CurrentSourcePageType != typeof(EnvironmentEditorPage)) Frame.Navigate(typeof(EnvironmentEditorPage));
    }

    /// <summary>Opens hosts-file editing within Control's navigation journal.</summary>
    private void HostsCard_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CurrentSourcePageType != typeof(HostsEditorPage)) Frame.Navigate(typeof(HostsEditorPage));
    }

    /// <summary>Opens battery charge controls within Control's navigation journal.</summary>
    private void BatteryCard_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CurrentSourcePageType != typeof(BatteryChargePage)) Frame.Navigate(typeof(BatteryChargePage));
    }

    /// <summary>Opens Connection while retaining Management as the selected sidebar section.</summary>
    private void ConnectionCard_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CurrentSourcePageType != typeof(ConnectionPage)) Frame.Navigate(typeof(ConnectionPage));
    }

    /// <summary>Opens explicit Windows servicing while preserving the Management sidebar owner.</summary>
    private void WindowsStatusCard_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CurrentSourcePageType != typeof(WindowsStatusPage)) Frame.Navigate(typeof(WindowsStatusPage));
    }

    /// <summary>Opens startup controls and advanced loading-point discovery within Control.</summary>
    private void AutorunsCard_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CurrentSourcePageType != typeof(AutorunsPage)) Frame.Navigate(typeof(AutorunsPage));
    }

    /// <summary>Opens live, read-only process observation within Control.</summary>
    private void ProcessMonitorCard_Click(object sender, RoutedEventArgs e)
    { if (Frame.CurrentSourcePageType != typeof(ProcessMonitorPage)) Frame.Navigate(typeof(ProcessMonitorPage)); }
}
