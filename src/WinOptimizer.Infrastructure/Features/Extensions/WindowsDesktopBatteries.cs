using System.Runtime.InteropServices;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Devices.Power;
using Windows.Storage.Streams;
using WinOptimizer.Core.Features.Common.Interfaces;

namespace WinOptimizer.Infrastructure.Features.Extensions;

public sealed record DesktopBattery(string? DeviceName, int? Percent, bool IsCharging = false);

/// <summary>Reads the PC battery and the standard battery characteristic of connected, paired Bluetooth LE devices.</summary>
public static class WindowsDesktopBatteries
{
    public static async Task<IReadOnlyList<DesktopBattery>> ReadAsync(ILogService log, CancellationToken token)
    {
        var batteries = new List<DesktopBattery>();
        try
        {
            var report = Battery.AggregateBattery.GetReport();
            if (report is { RemainingCapacityInMilliwattHours: int remaining, FullChargeCapacityInMilliwattHours: > 0 and int capacity })
                batteries.Add(new(null, (int)Math.Round(Math.Clamp(100d * remaining / capacity, 0, 100)),
                    report.Status == Windows.System.Power.BatteryStatus.Charging));
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        { log.LogDebug($"Could not read the PC battery: {ex.Message}"); }

        var selector = BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
        try
        {
            var devices = await DeviceInformation.FindAllAsync(selector, [], DeviceInformationKind.AssociationEndpoint).AsTask(token);
            foreach (var device in devices)
            {
                token.ThrowIfCancellationRequested();
                if (!device.Pairing.IsPaired) continue;
                int? percentage = null;
                try { percentage = await ReadBatteryLevelAsync(device.Id, token); }
                catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException)
                { log.LogDebug($"Could not read the Bluetooth battery: {ex.Message}"); }
                batteries.Add(new(device.Name, percentage));
            }
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            if (batteries.Count == 0) throw;
            log.LogDebug($"Could not enumerate Bluetooth batteries: {ex.Message}");
        }
        return batteries;
    }

    internal static async Task<int?> ReadBatteryLevelAsync(string id, CancellationToken token)
    {
        // FromIdAsync can require the UI apartment. Every device here is already paired and connected.
        using var device = await BluetoothLEDevice.FromIdAsync(id).AsTask(token);
        if (device is null || device.ConnectionStatus != BluetoothConnectionStatus.Connected) return null;
        var services = await device.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Cached).AsTask(token);
        if (services.Status != GattCommunicationStatus.Success) return null;
        try
        {
            foreach (var service in services.Services)
            {
                var characteristics = await service.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Cached).AsTask(token);
                if (characteristics.Status != GattCommunicationStatus.Success) continue;
                foreach (var characteristic in characteristics.Characteristics)
                {
                    var result = await characteristic.ReadValueAsync().AsTask(token);
                    if (result.Status != GattCommunicationStatus.Success || result.Value is not { Length: > 0 }) continue;
                    using var reader = DataReader.FromBuffer(result.Value);
                    byte value = reader.ReadByte();
                    if (value <= 100) return value;
                }
            }
            return null;
        }
        finally { foreach (var service in services.Services) service.Dispose(); }
    }
}
