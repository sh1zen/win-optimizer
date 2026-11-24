using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.Core.Features.Common.Interfaces;

namespace WinOptimizer.UI.Features.Extensions.Preview;

/// <summary>Exposes Preview from the Extensions navigation journal.</summary>
public sealed partial class PreviewPage : Page
{
    private Window? _window;
    private readonly ILocalizationService _localization;
    public PreviewSettingsViewModel ViewModel { get; }
    private string PreviewTitle => _localization.GetString("QuickPreview_Title");
    private string PreviewDescription => _localization.GetString("QuickPreview_Description");
    private string ReloadLabel => _localization.GetString("Button_Refresh");

    public PreviewPage() : this(App.Services.GetRequiredService<ILocalizationService>(),
        App.Services.GetRequiredService<PreviewSettingsViewModel>()) { }

    internal PreviewPage(ILocalizationService localization, PreviewSettingsViewModel viewModel)
    {
        _localization = localization;
        ViewModel = viewModel;
        InitializeComponent();
        Options.Localize(_localization);
        Options.ShortcutChanged += OnShortcutChanged;
        Options.StartupChanged += OnStartupChanged;
        Options.BackgroundOpacityChanged += OnBackgroundOpacityChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        _window = App.MainWindow;
        if (_window is not null) _window.Activated += OnActivated;
        ViewModel.PropertyChanged += OnSettingsChanged;
        ShowSettings();
        await ViewModel.LoadAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        ViewModel.PropertyChanged -= OnSettingsChanged;
        if (_window is not null) _window.Activated -= OnActivated;
        _window = null;
    }

    private async void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
            await ViewModel.LoadAsync();
    }

    private async void OnReload(object sender, RoutedEventArgs args) => await ViewModel.LoadAsync();

    private async void OnShortcutChanged(object? sender, EventArgs args) =>
        await ViewModel.SetShortcutAsync(Options.ShortcutEnabled);

    private async void OnStartupChanged(object? sender, EventArgs args) =>
        await ViewModel.SetStartupAsync(Options.StartsWithWindows);

    private async void OnBackgroundOpacityChanged(object? sender, EventArgs args) =>
        await ViewModel.SetBackgroundOpacityAsync(Options.BackgroundOpacity);

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ViewModel.Settings)) ShowSettings();
    }

    /// <summary>Reflects the companion's confirmed settings.</summary>
    private void ShowSettings()
    {
        Options.ShowShortcut(ViewModel.ShortcutEnabled);
        Options.ShowStartup(ViewModel.Settings?.StartsWithWindows ?? false);
        Options.ShowOpacity(ViewModel.BackgroundOpacity);
    }
}

