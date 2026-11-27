using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using WinOptimizer.UI.Features.Common.Helpers;
using WinOptimizer.UI.Features.Management.ViewModels;

namespace WinOptimizer.UI.Features.Management;

/// <summary>Loads startup controls on entry and binds mode changes to consent-gated inventory loading.</summary>
public sealed partial class AutorunsPage : Page
{
    public AutorunsViewModel ViewModel { get; }
    public AutorunsPage() : this(App.Services.GetRequiredService<AutorunsViewModel>()) { }
    public AutorunsPage(AutorunsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        PopupHandler.AttachMenu(ActionsButton, ["WindowsStartupSettings", "ClearStartupFilters"], FlyoutPlacementMode.Top);
        SizeChanged += (_, args) => UpdateLayoutMode(args.NewSize.Width);
        Loaded += async (_, _) =>
        {
            UpdateLayoutMode(ActualWidth);
            await ViewModel.LoadAsync();
        };
    }

    /// <summary>Measures the available page width after accounting for the application sidebar.</summary>
    private void UpdateLayoutMode(double width) => VisualStateManager.GoToState(this, width >= 1000 ? "Wide" : "Narrow", false);

    /// <summary>Keeps the switch on the scanned state until the user confirms and the scan finishes.</summary>
    private async void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        var toggle = (ToggleSwitch)sender;
        var requested = toggle.IsOn;
        if (requested == ViewModel.SelectedEnabled) return;

        toggle.IsOn = ViewModel.SelectedEnabled;
        var action = requested ? ViewModel.EnableCommand : ViewModel.DisableCommand;
        if (action.CanExecute(null)) await action.ExecuteAsync(null);
    }
}
