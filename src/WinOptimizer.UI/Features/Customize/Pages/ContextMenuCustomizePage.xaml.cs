using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.UI.Features.Customize.ViewModels;

namespace WinOptimizer.UI.Features.Customize.Pages;

/// <summary>Loads current registrations on entry and routes user toggles through the guarded service.</summary>
public sealed partial class ContextMenuCustomizePage : Page
{
    public ContextMenuViewModel ViewModel { get; }
    public ContextMenuCustomizePage() : this(App.Services.GetRequiredService<ContextMenuViewModel>()) { }
    public ContextMenuCustomizePage(ContextMenuViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.RefreshAsync();
    }

    private async void Entry_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch { Tag: ContextMenuEntryViewModel entry, IsLoaded: true } toggle
            || toggle.IsOn == entry.IsEnabled) return;
        if (ViewModel.CanWrite) await ViewModel.SetEnabledAsync(entry, toggle.IsOn);
        toggle.IsOn = entry.IsEnabled;
    }

    /// <summary>Changes every registration in a program without opening its individual-entry list.</summary>
    private async void Program_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch { Tag: ContextMenuProgramViewModel program, IsLoaded: true } toggle
            || toggle.IsOn == program.HasEnabledEntries) return;
        if (ViewModel.CanWrite) await ViewModel.SetProgramEnabledAsync(program, toggle.IsOn);
        toggle.IsOn = program.HasEnabledEntries;
    }
}
