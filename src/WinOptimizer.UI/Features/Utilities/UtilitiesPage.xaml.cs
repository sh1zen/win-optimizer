using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.UI.Features.Extensions;
using WinOptimizer.UI.Features.Extensions.ViewModels;
using WinOptimizer.UI.Features.Utilities.ViewModels;

namespace WinOptimizer.UI.Features.Utilities;

/// <summary>Lists desktop extensions with native Frame destinations and shared widget state.</summary>
public sealed partial class UtilitiesPage : Page
{
    public UtilitiesCatalogViewModel ViewModel { get; }
    public ExtensionsViewModel Extensions { get; }

    public UtilitiesPage() : this(App.Services.GetRequiredService<UtilitiesCatalogViewModel>(),
        App.Services.GetRequiredService<ExtensionsViewModel>()) { }

    /// <summary>Uses the same preference owner as SysMonitor settings and explicitly loads it on entry.</summary>
    public UtilitiesPage(UtilitiesCatalogViewModel viewModel, ExtensionsViewModel extensions)
    {
        ViewModel = viewModel;
        Extensions = extensions;
        InitializeComponent();
        Loaded += async (_, _) => await Extensions.LoadAsync();
    }

    /// <summary>Opens SysMonitor settings while retaining Extension as the owning sidebar section.</summary>
    private void OpenMonitorSettings_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CurrentSourcePageType != typeof(SystemMonitorSettingsPage))
            Frame.Navigate(typeof(SystemMonitorSettingsPage));
    }
}
