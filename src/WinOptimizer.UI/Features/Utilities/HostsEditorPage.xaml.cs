using WinOptimizer.UI.Features.Common.Models;
using WinOptimizer.UI.Features.Common.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.UI.Features.Utilities.ViewModels;

namespace WinOptimizer.UI.Features.Utilities;

/// <summary>Presents structured mappings and an optional text editor over the same versioned draft.</summary>
public sealed partial class HostsEditorPage : Page
{
    public HostsEditorViewModel ViewModel { get; } = App.Services.GetRequiredService<HostsEditorViewModel>();
    public HostsEditorPage() { InitializeComponent(); Loaded += async (_, _) => await ViewModel.LoadAsync(); }

    private void HostsContent_TextChanged(object sender, TextChangedEventArgs e) => ViewModel.UpdateContentFromEditor(HostsContent.Text);

    private async void NewEntry_Click(object sender, RoutedEventArgs e) => await ShowEntryDialogAsync(null);
    private async void EditEntry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HostsEntryViewModel entry }) await ShowEntryDialogAsync(entry);
    }
    private void RemoveEntry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HostsEntryViewModel entry } && ViewModel.RemoveEntryCommand.CanExecute(entry))
            ViewModel.RemoveEntryCommand.Execute(entry);
    }

    /// <summary>Collects and validates mapping fields without applying any system changes.</summary>
    private Task ShowEntryDialogAsync(HostsEntryViewModel? entry) => PopupHandler.RunModalAsync(async () =>
    {
        if (!ViewModel.CanEditEntries) return false;
        var address = new TextBox { Header = ViewModel.AddressLabel, Text = entry?.Address ?? "", PlaceholderText = "127.0.0.1", IsSpellCheckEnabled = false };
        var names = new TextBox { Header = ViewModel.HostNamesLabel, Text = entry?.HostNames ?? "", PlaceholderText = "example.test alias.test", IsSpellCheckEnabled = false };
        var comment = new TextBox { Header = ViewModel.CommentLabel, Text = entry?.Mapping.Comment ?? "" };
        var enabled = new ToggleSwitch { Header = ViewModel.EnabledLabel, IsOn = entry?.IsEnabled ?? true };
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false };
        var fields = new StackPanel { Spacing = 14, MinWidth = 280 };
        fields.Children.Add(address); fields.Children.Add(names); fields.Children.Add(comment); fields.Children.Add(enabled); fields.Children.Add(error);
        var dialog = PopupHandler.CreateDialog(new PopupDialogOptions
        {
            Title = entry == null ? ViewModel.NewEntryLabel : ViewModel.EditEntryLabel,
            Content = new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = (Thickness)Microsoft.UI.Xaml.Application.Current.Resources["ScrollContentPadding"] },
            PrimaryButtonText = entry == null ? ViewModel.NewEntryLabel : ViewModel.EditEntryLabel,
            CloseButtonText = ViewModel.CancelLabel, DefaultButton = ContentDialogButton.Primary
        });
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try { ViewModel.ApplyEntry(entry, address.Text, names.Text, comment.Text, enabled.IsOn); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            { error.Message = ex.Message; error.IsOpen = true; args.Cancel = true; }
        };
        dialog.Opened += (_, _) => address.Focus(FocusState.Programmatic);
        await PopupHandler.ShowDialogAsync(dialog, XamlRoot);
        return true;
    });
}
