using WinOptimizer.Infrastructure.Features.Extensions;

namespace WinOptimizer.UI.Features.Extensions.Notch;

internal enum NotchNoticeIcon { Battery, Charging, Bluetooth, Device, Volume, Brightness, Fps }

internal readonly record struct NotchNotice(string TextKey, NotchNoticeIcon Icon, int? Percent = null, string? Device = null);

/// <summary>Compares confirmed readings and emits a transient notice only for a state transition.</summary>
internal sealed class NotchStatusChanges
{
    private DesktopPower? _power;
    private DesktopVolume? _volume;
    private int? _brightness;
    private IReadOnlyList<DesktopBluetoothDevice>? _bluetoothDevices;
    private IReadOnlyList<DesktopLocalDevice>? _localDevices;

    internal NotchNotice? Observe(NotchReadingSnapshot reading)
    {
        var power = ObservePower(reading.Power);
        var device = ObserveDevices(reading.BluetoothDevices, reading.LocalDevices);
        NotchNotice? volume = null, brightness = null;
        if (reading.Volume is { } output && output != _volume)
        {
            if (_volume is { } previous && (!output.IsMuted || !previous.IsMuted))
                volume = new(output.IsMuted ? "Notch_VolumeMuted" : "Notch_Volume", NotchNoticeIcon.Volume,
                    output.IsMuted ? null : output.Percent);
            _volume = output;
        }
        if (reading.Brightness is int level && level != _brightness)
        {
            if (_brightness.HasValue) brightness = new("Notch_Brightness", NotchNoticeIcon.Brightness, level);
            _brightness = level;
        }
        return power ?? device ?? volume ?? brightness;
    }

    private NotchNotice? ObservePower(DesktopPower? power)
    {
        if (power is not { } current) return null;
        var previous = _power;
        _power = current;
        if (previous is not { } old) return null;
        if (old.OnAc != current.OnAc || old.IsCharging != current.IsCharging)
        {
            var key = current switch
            {
                { IsCharging: true } => "Utilities_BatteryCharging",
                { OnAc: false } => "Notch_OnBattery",
                _ when !old.OnAc => "Notch_PowerConnected",
                { Percent: 100 } => "Notch_BatteryFull",
                _ => "Notch_ChargingPaused"
            };
            return new(key, current.IsCharging ? NotchNoticeIcon.Charging : NotchNoticeIcon.Battery, current.Percent);
        }
        return !current.OnAc && old.Percent > 20 && current.Percent <= 20
            ? new("Notch_LowBattery", NotchNoticeIcon.Battery, current.Percent) : null;
    }

    /// <summary>Compares physical devices across both sources, retaining each last successful sample.</summary>
    private NotchNotice? ObserveDevices(IReadOnlyList<DesktopBluetoothDevice>? bluetooth,
        IReadOnlyList<DesktopLocalDevice>? local)
    {
        var previousBluetooth = _bluetoothDevices;
        var previousLocal = _localDevices;
        _bluetoothDevices = bluetooth ?? previousBluetooth;
        _localDevices = local ?? previousLocal;
        if (!HasChanged(previousBluetooth, _bluetoothDevices) && !HasChanged(previousLocal, _localDevices)) return null;

        // A source's first successful sample establishes its baseline, even if the other was ready sooner.
        var previous = ConnectedDevices(previousBluetooth ?? _bluetoothDevices, previousLocal ?? _localDevices)
            .ToDictionary(device => device.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var device in ConnectedDevices(_bluetoothDevices, _localDevices))
            if (!previous.Remove(device.Id))
                return new("Notch_DeviceConnected", device.Icon, device.Percent, device.Name);

        if (previous.Count == 0) return null;
        var removed = previous.Values.First();
        return new("Notch_DeviceDisconnected", removed.Icon, Device: removed.Name);
    }

    private static bool HasChanged<T>(IReadOnlyList<T>? previous, IReadOnlyList<T>? current) =>
        previous is not null && current is not null && !ReferenceEquals(previous, current) && !previous.SequenceEqual(current);

    private static IEnumerable<(string Id, string Name, NotchNoticeIcon Icon, int? Percent)> ConnectedDevices(
        IReadOnlyList<DesktopBluetoothDevice>? bluetooth, IReadOnlyList<DesktopLocalDevice>? local) =>
        (bluetooth ?? []).Select(device => (device.Id, device.Name, Icon: NotchNoticeIcon.Bluetooth, device.Percent))
            .Concat((local ?? []).Select(device => (device.Id, device.Name, Icon: NotchNoticeIcon.Device, Percent: (int?)null)))
            .DistinctBy(device => device.Id, StringComparer.OrdinalIgnoreCase);
}
