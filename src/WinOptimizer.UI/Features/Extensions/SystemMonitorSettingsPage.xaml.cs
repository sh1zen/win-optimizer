using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.UI.Features.Extensions.ViewModels;

namespace WinOptimizer.UI.Features.Extensions;

/// <summary>Displays SysMonitor preferences with a dedicated page heading.</summary>
public sealed partial class SystemMonitorSettingsPage : Page
{
    public ExtensionsViewModel ViewModel { get; } = App.Services.GetRequiredService<ExtensionsViewModel>();

    public SystemMonitorSettingsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.LoadAsync();
    }
}
