using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinOptimizer.Infrastructure.Features.Extensions;

namespace WinOptimizer.UI.Features.Extensions.Notch;

/// <summary>Presentation of a confirmed pin in the current editing mode.</summary>
internal sealed record NotchShortcutTile(NotchShortcut Shortcut, bool CanOpen, Visibility RemoveVisibility, string RemoveLabel, Brush Surface)
{
    public string Hint => Shortcut.AppName ?? Shortcut.Path;
}

/// <summary>Selects the pin or add-action template within the same tile grid.</summary>
public sealed partial class NotchShortcutTemplateSelector : DataTemplateSelector
{
    public DataTemplate PinTemplate { get; set; } = null!;
    public DataTemplate AddTemplate { get; set; } = null!;

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) =>
        item is NotchShortcutTile ? PinTemplate : AddTemplate;
}

public sealed partial class NotchWindow
{
    private readonly IDesktopApplications _applications;
    private readonly CancellationTokenSource _shortcutLifetime = new();
    private readonly Dictionary<string, Task<WriteableBitmap?>> _shortcutIcons = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _appPickerLoad;

    /// <summary>Loads the currently available Start menu apps for the add tile.</summary>
    private async void OnAddFavoriteApp(object sender, RoutedEventArgs args)
    {
        if (AppPicker.Visibility == Visibility.Visible || _pickingShortcutFile || !_extension.CanEdit || _editingShortcuts) return;
        using var request = new CancellationTokenSource();
        _appPickerLoad = request;
        AppPicker.Visibility = Visibility.Visible;
        ShortcutActions.Visibility = ShortcutItems.Visibility = Visibility.Collapsed;
        RenderShortcutsStatus();
        AppChoice.IsEnabled = false;
        AppPickerStatus.Text = "";
        AppPickerProgress.IsActive = true;
        try
        {
            var apps = await _applications.ReadAsync(request.Token);
            if (request.IsCancellationRequested) return;
            var pinned = _content.Shortcuts.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var choices = apps.Where(app => !pinned.Contains(app.Target)).ToArray();
            AppChoice.ItemsSource = choices;
            AppChoice.IsEnabled = choices.Length > 0;
            if (choices.Length == 0) AppPickerStatus.Text = Text("Notch_AppListEmpty");
            else AppChoice.Focus(FocusState.Programmatic);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex) when (ex is COMException or Win32Exception or InvalidOperationException)
        {
            if (request.IsCancellationRequested) return;
            _log.LogError("Could not read the installed apps for Notch.", ex);
            AppPickerStatus.Text = Text("Notch_AppListFailed");
        }
        finally
        {
            if (_appPickerLoad == request)
            {
                _appPickerLoad = null;
                AppPickerProgress.IsActive = false;
            }
        }
    }

    private void OnAppChoiceChanged(object sender, SelectionChangedEventArgs args) =>
        ConfirmApp.IsEnabled = AppChoice.SelectedItem is DesktopApplication;

    private async void OnConfirmApp(object sender, RoutedEventArgs args)
    {
        if (AppChoice.SelectedItem is not DesktopApplication app || !_extension.CanEdit) return;
        CloseAppPicker();
        await _extension.AddApplicationAsync(app);
        ShowShortcutSaveResult();
    }

    private void OnCancelApp(object sender, RoutedEventArgs args) => CloseAppPicker();

    /// <summary>Cancels any pending catalogue request when leaving the picker.</summary>
    private void CloseAppPicker()
    {
        var request = _appPickerLoad;
        _appPickerLoad = null;
        request?.Cancel();
        if (_closed || AppPicker.Visibility != Visibility.Visible) return;
        AppPickerProgress.IsActive = false;
        AppChoice.IsDropDownOpen = false;
        AppChoice.ItemsSource = null;
        AppPicker.Visibility = Visibility.Collapsed;
        ShortcutActions.Visibility = ShortcutItems.Visibility = Visibility.Visible;
        RenderShortcutsStatus();
    }

    /// <summary>Shows the Explorer dialog above this widget, then pins the confirmed selection.</summary>
    private async void OnAddShortcut(object sender, RoutedEventArgs args)
    {
        if (_pickingShortcutFile || !_extension.CanEdit) return;
        var path = PickLocalFile("Notch_PinFile");
        if (path is null || _closed) return;
        await _extension.FlushAsync();
        if (_closed) return;
        await _extension.AddShortcutAsync(path);
        ShowShortcutSaveResult();
    }

    private void OnEditShortcuts(object sender, RoutedEventArgs args) =>
        SetEditingShortcuts(!_editingShortcuts && _content.Shortcuts.Length > 0);

    private async void OnRemoveShortcut(object sender, RoutedEventArgs args)
    {
        if (!_editingShortcuts || !_extension.CanEdit || ((Button)sender).Tag is not NotchShortcut shortcut ||
            !_content.Shortcuts.Contains(shortcut)) return;
        await _extension.RemoveShortcutAsync(shortcut);
        ShowShortcutSaveResult();
    }

    private void ShowShortcutSaveResult()
    {
        if (_closed) return;
        _shortcutErrorKey = null;
        _shortcutSaveError = _extension.Error;
        RenderShortcutsStatus();
    }

    private void SetEditingShortcuts(bool editing)
    {
        if (_editingShortcuts == editing) return;
        _editingShortcuts = editing;
        RenderShortcutItems();
    }

    /// <summary>Projects the saved pins and add action into the same grid.</summary>
    private void RenderShortcutItems()
    {
        EditShortcuts.Visibility = VisibilityFor(_content.Shortcuts.Length > 0);
        EditShortcuts.Content = Text(_editingShortcuts ? "Notch_FinishEditingShortcuts" : "Notch_EditShortcuts");
        var surface = (Brush)Root.Resources["NotchShortcutSurface"];
        var tiles = new List<object>(_content.Shortcuts.Length + 1);
        foreach (var shortcut in _content.Shortcuts)
            tiles.Add(new NotchShortcutTile(shortcut, !_editingShortcuts, VisibilityFor(_editingShortcuts),
                _localization.GetString("Notch_RemoveShortcut", shortcut.Name), surface));
        if (!_editingShortcuts && _content.Shortcuts.Length < NotchContent.MaximumShortcuts)
            tiles.Add(Text("Notch_PinApp"));
        ShortcutItems.ItemsSource = tiles;
        foreach (var target in _shortcutIcons.Keys.Where(target => !_content.Shortcuts.Any(item =>
                     StringComparer.OrdinalIgnoreCase.Equals(item.Path, target))).ToArray())
            _shortcutIcons.Remove(target);
    }

    /// <summary>Reuses a rendered icon while the corresponding application remains pinned.</summary>
    private async void OnShortcutIconLoaded(object sender, RoutedEventArgs args)
    {
        var image = (Image)sender;
        if (_closed || image.Tag is not NotchShortcut { AppName: not null } shortcut) return;
        if (!_shortcutIcons.TryGetValue(shortcut.Path, out var icon))
            _shortcutIcons[shortcut.Path] = icon = ReadShortcutIconAsync(shortcut.Path, _shortcutLifetime.Token);
        if (await icon is { } source && !_closed && image.IsLoaded && image.Tag is NotchShortcut current &&
            current == shortcut && image.Parent is Grid artwork)
        {
            image.Source = source;
            artwork.Children[0].Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Copies shell pixels into a XAML-owned bitmap before releasing the shell bitmap.</summary>
    private async Task<WriteableBitmap?> ReadShortcutIconAsync(string target, CancellationToken token)
    {
        try
        {
            using var bitmap = await _applications.ReadIconAsync(target, token);
            if (bitmap is null || token.IsCancellationRequested) return null;
            // WinUI can reread the pixels after an icon leaves and reenters the visual tree.
            var source = new WriteableBitmap(bitmap.PixelWidth, bitmap.PixelHeight);
            bitmap.CopyToBuffer(source.PixelBuffer);
            source.Invalidate();
            return source;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is COMException or ArgumentException or Win32Exception)
        {
            _log.LogDebug($"Could not read a Notch application icon: {ex.GetType().Name}.");
            return null;
        }
    }

    private void OnOpenShortcut(object sender, RoutedEventArgs args)
    {
        if (_editingShortcuts || ((Button)sender).Tag is not NotchShortcut shortcut || !_content.Shortcuts.Contains(shortcut)) return;
        _shortcutSaveError = null;
        try
        {
            _openShortcut(shortcut.Path);
            _shortcutErrorKey = null;
            SetExpanded(false);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException
            or Win32Exception or COMException)
        {
            _log.LogDebug($"Could not open a Notch shortcut: {ex.GetType().Name}.");
            _shortcutErrorKey = "Notch_ShortcutOpenFailed";
        }
        RenderShortcutsStatus();
    }

    /// <summary>Keeps the empty hint and add tile together below the dedicated toolbar.</summary>
    private void RenderShortcutsStatus()
    {
        var key = _shortcutErrorKey ?? (_content.Shortcuts.Length == 0 ? "Notch_ShortcutsEmpty" : null);
        var message = !string.IsNullOrEmpty(_shortcutSaveError) ? _shortcutSaveError : key is null ? "" : Text(key);
        ShortcutsStatus.Visibility = VisibilityFor(message.Length > 0 && AppPicker.Visibility != Visibility.Visible);
        ShortcutsStatus.Text = message;
        bool centerEmpty = !_overviewLayout && ShortcutsStatus.Visibility == Visibility.Visible &&
            key == "Notch_ShortcutsEmpty" && string.IsNullOrEmpty(_shortcutSaveError);
        ShortcutsBody.VerticalAlignment = centerEmpty ? VerticalAlignment.Center : VerticalAlignment.Top;
        ShortcutsStatus.TextAlignment = centerEmpty ? TextAlignment.Center : TextAlignment.Left;
        ShortcutItems.HorizontalAlignment = centerEmpty ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        ShortcutItems.MaxWidth = centerEmpty ? 156 : double.PositiveInfinity;
    }
}
