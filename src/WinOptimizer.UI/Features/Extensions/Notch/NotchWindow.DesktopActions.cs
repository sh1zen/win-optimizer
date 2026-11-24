using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;

namespace WinOptimizer.UI.Features.Extensions.Notch;

public sealed partial class NotchWindow
{
    private readonly NotchFileShare? _fileShare;
    private readonly Func<string, Task> _shareFile;
    private readonly DispatcherQueueTimer _statusTimer;
    private NotchStatusChanges? _statusChanges;
    private bool _sharingFile;
    private bool _fpsUnavailableReported;

    private Thickness DefaultPanelMargin => (Thickness)Root.Resources["NotchPanelMargin"];
    private Thickness ViewPanelMargin
    {
        get
        {
            var margin = DefaultPanelMargin;
            if (_view == NotchView.Dashboard) margin.Bottom = 10;
            return margin;
        }
    }
    private double StatusBannerSpace => StatusBanner.Height + StatusBanner.Margin.Top + StatusBanner.Margin.Bottom;

    /// <summary>Shows the latest desktop transition temporarily, in either Notch size.</summary>
    private void ShowStatus(NotchNotice notice)
    {
        if (_closed) return;
        bool wasVisible = StatusBanner.Visibility == Visibility.Visible;
        bool charging = notice.Icon == NotchNoticeIcon.Charging;
        StatusIcon.Glyph = notice.Icon switch
        {
            NotchNoticeIcon.Battery => "\uEBA7",
            // The charging battery icons are consecutive, from empty to full.
            NotchNoticeIcon.Charging => ((char)('\uEBAB' + (Math.Clamp(notice.Percent ?? 0, 0, 100) + 5) / 10)).ToString(),
            NotchNoticeIcon.Bluetooth => "\uE702",
            NotchNoticeIcon.Device => "\uE772",
            NotchNoticeIcon.Volume => "\uE767",
            NotchNoticeIcon.Brightness => "\uE706",
            _ => "\uE916"
        };
        StatusBanner.Background = (Brush)Root.Resources[charging ? "NotchChargingSurface" : "NotchStatusSurface"];
        StatusIcon.Foreground = (Brush)Root.Resources[charging ? "NotchChargingAccent" : "NotchStatusIcon"];
        StatusValue.Foreground = charging ? StatusIcon.Foreground : (Brush)Root.Resources["NotchStatusValue"];
        StatusLabel.Text = notice.Device is null ? Text(notice.TextKey) : _localization.GetString(notice.TextKey, notice.Device);
        StatusValue.Text = notice.Percent is int value ? $"{value}%" : "";
        AutomationProperties.SetName(StatusLabel, $"{StatusLabel.Text} {StatusValue.Text}".TrimEnd());
        StatusLevel.Visibility = VisibilityFor(notice.Percent.HasValue &&
            notice.Icon is NotchNoticeIcon.Volume or NotchNoticeIcon.Brightness);
        if (notice.Percent is int level) StatusLevel.Value = level;
        StatusBanner.Visibility = Visibility.Visible;
        UpdateStatusLayout();
        if (!wasVisible && _xamlRoot is not null) RefreshFrame();
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private void HideStatus()
    {
        if (StatusBanner.Visibility == Visibility.Collapsed) return;
        _statusTimer.Stop();
        StatusBanner.Visibility = Visibility.Collapsed;
        UpdateStatusLayout();
        if (_xamlRoot is not null) RefreshFrame();
    }

    private void UpdateStatusLayout()
    {
        bool visible = StatusBanner.Visibility == Visibility.Visible;
        if (visible && !_expanded) EndCompactDrag();
        CompactReadouts.Visibility = VisibilityFor(!visible || _expanded);
        UpdatePanelMargin();
    }

    /// <summary>Applies the active view's margin and reserves room above it for a visible notice.</summary>
    private void UpdatePanelMargin()
    {
        var margin = ViewPanelMargin;
        if (StatusBanner.Visibility == Visibility.Visible && _expanded) margin.Top = StatusBannerSpace;
        Panel.Margin = margin;
    }

    private string? PickLocalFile(string titleKey)
    {
        _pickingShortcutFile = true;
        try { return _pickShortcutFile(this, Text(titleKey)); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            _log.LogError("Could not open the Notch file picker.", ex);
            _shortcutSaveError = null;
            _shortcutErrorKey = "Notch_FilePickerFailed";
            RenderShortcutsStatus();
            return null;
        }
        finally { _pickingShortcutFile = false; }
    }

    private async void OnShareToPhone(object sender, RoutedEventArgs args)
    {
        if (_pickingShortcutFile || _sharingFile) return;
        var path = PickLocalFile("Notch_ShareToPhone");
        if (path is null || _closed) return;
        _sharingFile = true;
        _shortcutSaveError = null;
        try
        {
            await _shareFile(path);
            _shortcutErrorKey = null;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log.LogError("Could not open Windows Share for the selected file.", ex);
            _shortcutErrorKey = "Notch_ShareFailed";
        }
        finally
        {
            _sharingFile = false;
            if (!_closed) RenderShortcutsStatus();
        }
    }
}
