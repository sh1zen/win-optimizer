using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.UI.Features.Extensions;
using WinOptimizer.UI.Features.Extensions.Notch;
using WinOptimizer.UI.Features.Extensions.Preview;
using WinOptimizer.UI.Features.Extensions.ViewModels;
using WinOptimizer.UI.Features.Utilities.ViewModels;

namespace WinOptimizer.UI.Features.Utilities;

/// <summary>Lists desktop extensions with native Frame destinations and shared widget state.</summary>
public sealed partial class UtilitiesPage : Page
{
    public UtilitiesCatalogViewModel ViewModel { get; }
    public ExtensionsViewModel Extensions { get; }
    public PreviewSettingsViewModel Preview { get; }
    public NotchExtension Notch { get; }

    public UtilitiesPage() : this(App.Services.GetRequiredService<UtilitiesCatalogViewModel>(),
        App.Services.GetRequiredService<ExtensionsViewModel>(), App.Services.GetRequiredService<PreviewSettingsViewModel>(),
        App.Services.GetRequiredService<NotchExtension>()) { }

    /// <summary>Loads extension state from the same view models as the detail pages.</summary>
    internal UtilitiesPage(UtilitiesCatalogViewModel viewModel, ExtensionsViewModel extensions, PreviewSettingsViewModel preview, NotchExtension notch)
    {
        ViewModel = viewModel;
        Extensions = extensions;
        Preview = preview;
        Notch = notch;
        InitializeComponent();
        Loaded += async (_, _) => await Task.WhenAll(Extensions.LoadAsync(), Preview.LoadAsync(), Notch.LoadAsync());
    }

    /// <summary>Opens SysMonitor settings in the shell journal.</summary>
    private void OpenMonitorSettings_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CurrentSourcePageType != typeof(SystemMonitorSettingsPage))
            Frame.Navigate(typeof(SystemMonitorSettingsPage));
    }

    private void OpenPreview_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(PreviewPage));
    private void OpenNotch_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(NotchSettingsPage));

    /// <summary>Only user changes differ from the confirmed shortcut state.</summary>
    private async void OnPreviewToggled(object sender, RoutedEventArgs e)
    {
        var enabled = ((ToggleSwitch)sender).IsOn;
        if (enabled != Preview.ShortcutEnabled)
            await Preview.SetShortcutAsync(enabled);
    }

    private async void ReloadPreview_Click(object sender, RoutedEventArgs e) => await Preview.LoadAsync();

    /// <summary>Restores the confirmed switch position after a failed update.</summary>
    private async void OnNotchToggled(object sender, RoutedEventArgs e)
    {
        var toggle = (ToggleSwitch)sender;
        if (toggle.IsOn == Notch.IsEnabled) return;
        await Notch.SetEnabledAsync(toggle.IsOn);
        toggle.IsOn = Notch.IsEnabled;
    }
}
