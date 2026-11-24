using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Infrastructure.Features.Extensions;

namespace WinOptimizer.UI.Features.Extensions.Notch;

/// <summary>Shows confirmed Windows values and serializes edits from the Quick Settings view.</summary>
public sealed partial class NotchQuickSettings : UserControl, IDisposable
{
    private readonly DispatcherQueueTimer _poll;
    private readonly DispatcherQueueTimer _commit;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _gate = new(1);
    private IDesktopQuickSettings _settings = null!;
    private ILocalizationService _localization = null!;
    private ILogService _log = null!;
    private DesktopQuickSettings _state = new();
    private int? _brightness;
    private int? _volume;
    private bool _rendering;
    private bool _actionPending;
    private bool _levelsQueued;
    private bool _disposed;
    private bool _failed;

    public NotchQuickSettings()
    {
        InitializeComponent();
        _poll = DispatcherQueue.CreateTimer();
        _poll.Interval = TimeSpan.FromSeconds(2);
        _poll.Tick += (_, _) => Refresh();
        _commit = DispatcherQueue.CreateTimer();
        _commit.Interval = TimeSpan.FromMilliseconds(160);
        _commit.IsRepeating = false;
        _commit.Tick += (_, _) => CommitLevels();
    }

    internal void Configure(IDesktopQuickSettings settings, ILocalizationService localization, ILogService log)
    {
        _settings = settings;
        _localization = localization;
        _log = log;
    }

    /// <summary>Samples Windows while the view is visible. Pending user edits can finish during collapse.</summary>
    /// <summary>Reflows the same controls into a scrollable tile while preserving the full-width page.</summary>
    internal void SetOverviewLayout(bool overview)
    {
        CornerRadius = overview ? new CornerRadius(14) : new CornerRadius(0);
        QuickSettingsScroll.Padding = overview ? new Thickness(8) : new Thickness(0);
        SetTilePosition(WiFi, 0, 0, 0, 2);
        SetTilePosition(Bluetooth, 1, 0, 2, 3);
        SetTilePosition(NightLight, 2, 1, 0, 2);
        SetTilePosition(DoNotDisturb, 3, 1, 2, 3);
        SetTilePosition(Mute, 4, 2, 0, 5);
        SetTilePosition(Battery, 0, 0, 0, 4);
        SetTilePosition(Efficiency, 1, 1, 0, 4);
        SetTilePosition(Balanced, 2, 2, 0, 4);
        SetTilePosition(Performance, 3, 3, 0, 4);
        SetTilePosition(PowerSettings, 1, 1, 0, 4, 3);
        BatteryColumn.Width = overview ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        RadioSettings.RowSpacing = PowerModes.RowSpacing = overview ? 4 : 0;

        void SetTilePosition(FrameworkElement control, int fullColumn, int row, int column, int span, int fullSpan = 1)
        {
            Grid.SetRow(control, overview ? row : 0);
            Grid.SetColumn(control, overview ? column : fullColumn);
            Grid.SetColumnSpan(control, overview ? span : fullSpan);
        }
    }

    internal void SetActive(bool active)
    {
        if (active == _poll.IsRunning || _disposed) return;
        if (active)
        {
            _poll.Start();
            Refresh();
        }
        else
        {
            _poll.Stop();
            _commit.Stop();
            CommitLevels();
        }
    }

    internal void ApplyLanguage()
    {
        AutomationProperties.SetName(Brightness, Text("Notch_Brightness"));
        AutomationProperties.SetName(Volume, Text("Notch_Volume"));
        NightLight.Content = Text("Notch_NightLight");
        DoNotDisturbLabel.Text = Text("Notch_QuickDoNotDisturb");
        AutomationProperties.SetName(DoNotDisturb, DoNotDisturbLabel.Text);
        Mute.Content = Text("Notch_Mute");
        Efficiency.Content = Text("PowerPlan_PowerSaver_Name");
        Balanced.Content = Text("PowerPlan_Balanced_Name");
        Performance.Content = Text("PowerPlan_HighPerformance_Name");
        PowerSettings.Content = Text("Feature_Power_Name") + " ↗";
        Render();
    }

    private string Text(string key) => _localization.GetString(key);

    private void Refresh()
    {
        if (!_disposed && _poll.IsRunning && _gate.CurrentCount > 0 && !_commit.IsRunning) _ = RunAsync();
    }

    private void OnBrightnessChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (_disposed || _rendering || _state.Brightness is null) return;
        _brightness = (int)args.NewValue;
        BrightnessValue.Text = Percent(_brightness);
        ScheduleCommit();
    }

    private void OnVolumeChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (_disposed || _rendering || _state.Volume is null) return;
        _volume = (int)args.NewValue;
        VolumeValue.Text = Percent(_volume);
        ScheduleCommit();
    }

    private void ScheduleCommit()
    {
        _commit.Stop();
        _commit.Start();
    }

    /// <summary>Coalesces rapid slider edits while preserving the latest value of each control.</summary>
    private async void CommitLevels()
    {
        if (_disposed || _levelsQueued || (_brightness is null && _volume is null)) return;
        _levelsQueued = true;
        await RunAsync(async token =>
        {
            var brightness = _brightness;
            var volume = _volume;
            _brightness = _volume = null;
            if (brightness is int newBrightness) await ApplyAsync(ct => _settings.SetBrightnessAsync(newBrightness, ct), token);
            if (volume is int newVolume) await ApplyAsync(ct => _settings.SetVolumeAsync(newVolume, ct), token);
        });
        _levelsQueued = false;
        if (!_disposed && (_brightness is not null || _volume is not null) && !_commit.IsRunning) ScheduleCommit();
    }

    private void OnWiFi(object sender, RoutedEventArgs args) => ChangeRadio(DesktopRadio.WiFi, _state.WiFi, DesktopSettingsPage.WiFi);
    private void OnBluetooth(object sender, RoutedEventArgs args) => ChangeRadio(DesktopRadio.Bluetooth, _state.Bluetooth, DesktopSettingsPage.Bluetooth);
    private void OnNightLight(object sender, RoutedEventArgs args)
    {
        if (_state.NightLight is bool enabled) Change(token => _settings.SetNightLightAsync(!enabled, token));
    }

    private void OnDoNotDisturb(object sender, RoutedEventArgs args)
    {
        if (_state.DoNotDisturb is bool enabled) Change(token => _settings.SetDoNotDisturbAsync(!enabled, token));
    }

    private void OnPowerSettings(object sender, RoutedEventArgs args) => Open(DesktopSettingsPage.Power);
    private void OnEfficiency(object sender, RoutedEventArgs args) => SetPowerMode(DesktopPowerMode.Efficiency);
    private void OnBalanced(object sender, RoutedEventArgs args) => SetPowerMode(DesktopPowerMode.Balanced);
    private void OnPerformance(object sender, RoutedEventArgs args) => SetPowerMode(DesktopPowerMode.Performance);

    private void OnMute(object sender, RoutedEventArgs args)
    {
        if (_state.Volume is { } volume) Change(token => _settings.SetMuteAsync(!volume.IsMuted, token));
    }

    private void SetPowerMode(DesktopPowerMode mode) => Change(token => _settings.SetPowerModeAsync(mode, token));
    private void Open(DesktopSettingsPage page) => Change(token => _settings.OpenAsync(page, token));

    private void ChangeRadio(DesktopRadio radio, bool? enabled, DesktopSettingsPage page) => Change(async token =>
    {
        if (enabled is bool state)
        {
            try { await _settings.SetRadioAsync(radio, !state, token); return; }
            catch (UnauthorizedAccessException) { /* Windows Settings can still manage a radio denied to this app. */ }
        }
        await _settings.OpenAsync(page, token);
    });

    private async void Change(Func<CancellationToken, Task> action)
    {
        if (_rendering || _actionPending || _disposed) return;
        _actionPending = true;
        Render();
        await RunAsync(token => ApplyAsync(action, token));
        _actionPending = false;
        if (!_disposed) Render();
    }

    private async Task RunAsync(Func<CancellationToken, Task>? change = null)
    {
        var token = _lifetime.Token;
        try
        {
            await _gate.WaitAsync(token);
            try
            {
                if (change is not null)
                {
                    _failed = false;
                    await change(token);
                }
                await ApplyAsync(async ct => _state = await _settings.ReadAsync(ct), token);
            }
            finally { _gate.Release(); }
            if (!_disposed) Render();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task ApplyAsync(Func<CancellationToken, Task> action, CancellationToken token)
    {
        try { await action(token); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or COMException or UnauthorizedAccessException or
            Win32Exception or SecurityException or InvalidOperationException or EntryPointNotFoundException)
        {
            _failed = true;
            _log.LogDebug($"Windows Quick Settings: {ex.Message}");
        }
    }

    private void Render()
    {
        _rendering = true;
        try
        {
            SetLevel(Brightness, BrightnessValue, _state.Brightness, _brightness);
            SetLevel(Volume, VolumeValue, _state.Volume?.Percent, _volume);
            WiFi.Content = _state.WiFi is null ? "Wi-Fi ↗" : "Wi-Fi";
            Bluetooth.Content = _state.Bluetooth is null ? "Bluetooth ↗" : "Bluetooth";
            WiFi.IsChecked = _state.WiFi == true;
            Bluetooth.IsChecked = _state.Bluetooth == true;
            Mute.IsChecked = _state.Volume?.IsMuted == true;
            WiFi.IsEnabled = Bluetooth.IsEnabled = PowerSettings.IsEnabled = !_actionPending;
            NightLight.IsChecked = _state.NightLight == true;
            NightLight.IsEnabled = !_actionPending && _state.NightLight is not null;
            DoNotDisturb.IsChecked = _state.DoNotDisturb == true;
            DoNotDisturb.IsEnabled = !_actionPending && _state.DoNotDisturb is not null;
            Mute.IsEnabled = !_actionPending && _state.Volume is not null;
            var mode = _state.PowerMode;
            Efficiency.Visibility = Balanced.Visibility = Performance.Visibility = mode is null ? Visibility.Collapsed : Visibility.Visible;
            PowerSettings.Visibility = mode is null ? Visibility.Visible : Visibility.Collapsed;
            Efficiency.IsEnabled = Balanced.IsEnabled = Performance.IsEnabled = !_actionPending;
            Efficiency.IsChecked = mode == DesktopPowerMode.Efficiency;
            Balanced.IsChecked = mode == DesktopPowerMode.Balanced;
            Performance.IsChecked = mode == DesktopPowerMode.Performance;
            Battery.Visibility = _state.Battery is null ? Visibility.Collapsed : Visibility.Visible;
            if (_state.Battery is { } battery)
                BatteryValue.Text = $"{Percent(battery.Percent)}  {Text(battery.OnAc ? "PowerStatus_PluggedIn" : "Notch_OnBattery")}";
            SettingsError.Text = _failed ? Text("Notch_QuickSettingsError") : "";
            SettingsError.Visibility = _failed ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _rendering = false; }
    }

    private static void SetLevel(Slider slider, TextBlock label, int? current, int? pending)
    {
        slider.IsEnabled = current is not null;
        var value = pending ?? current;
        slider.Value = value ?? 0;
        label.Text = Percent(value);
    }

    private static string Percent(int? value) => value is int percent ? $"{percent / 100d:P0}" : "—";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _poll.Stop();
        _commit.Stop();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
