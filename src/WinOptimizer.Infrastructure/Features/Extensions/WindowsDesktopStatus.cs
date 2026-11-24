using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace WinOptimizer.Infrastructure.Features.Extensions;

public readonly record struct DesktopPower(bool OnAc, int Percent, bool IsCharging);
public sealed record DesktopBluetoothDevice(string Id, string Name, int? Percent = null, string? BatteryEndpointId = null);
public sealed record DesktopLocalDevice(string Id, string Name);

/// <summary>Reads Windows power, brightness and connected-device state for the desktop Notch.</summary>
public static class WindowsDesktopStatus
{
    private const string ContainerId = "System.Devices.ContainerId";
    private const string BluetoothContainerId = "System.Devices.Aep.ContainerId";
    private const string InterfaceClass = "System.Devices.InterfaceClassGuid";
    private static readonly Guid UsbDeviceInterface = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");
    private static readonly Guid SystemContainer = new("00000000-0000-0000-FFFF-FFFFFFFFFFFF");

    public static DesktopPower? ReadPower()
    {
        if (!GetSystemPowerStatus(out var status)) throw new Win32Exception(Marshal.GetLastWin32Error());
        const byte unknown = 255, noBattery = 128, charging = 8;
        return status.AcLineStatus == unknown || status.BatteryFlag == unknown ||
            (status.BatteryFlag & noBattery) != 0 || status.Percent > 100
            ? null
            : new DesktopPower(status.AcLineStatus == 1, status.Percent, (status.BatteryFlag & charging) != 0);
    }

    public static int? ReadBrightness()
    {
        using var query = new ManagementObjectSearcher(@"root\wmi",
            "SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active = TRUE");
        try
        {
            using var monitors = query.Get();
            foreach (ManagementBaseObject monitor in monitors)
            {
                using (monitor)
                    if (monitor["CurrentBrightness"] is byte value && value <= 100) return value;
            }
        }
        catch (ManagementException ex) when (ex.ErrorCode is ManagementStatus.InvalidClass or
            ManagementStatus.InvalidNamespace or ManagementStatus.NotSupported)
        {
            // Some display drivers and external monitors do not expose the WMI class.
            return null;
        }
        catch (ManagementException ex) { throw new IOException("Could not read Windows monitor brightness.", ex); }
        return null;
    }

    /// <summary>Changes the same active WMI display used for the brightness reading.</summary>
    public static void SetBrightness(int percent)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(percent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, 100);
        using var query = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active = TRUE");
        try
        {
            using var monitors = query.Get();
            foreach (ManagementObject monitor in monitors)
            {
                using (monitor)
                using (var input = monitor.GetMethodParameters("WmiSetBrightness"))
                {
                    input["Timeout"] = 0u;
                    input["Brightness"] = (byte)percent;
                    using var result = monitor.InvokeMethod("WmiSetBrightness", input, null);
                    if (result?["ReturnValue"] is not uint status || status != 0)
                        throw new IOException("The display driver rejected the brightness change.");
                    return;
                }
            }
        }
        catch (ManagementException ex) { throw new IOException("Could not change Windows monitor brightness.", ex); }
        throw new InvalidOperationException("There is no active WMI brightness control.");
    }

    public static async Task<IReadOnlyList<DesktopBluetoothDevice>> ReadConnectedDevicesAsync(CancellationToken token)
    {
        var classic = BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
        var lowEnergy = BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
        string[] properties = [ContainerId, BluetoothContainerId];
        var leDevices = DeviceInformation.FindAllAsync(lowEnergy, properties, DeviceInformationKind.AssociationEndpoint).AsTask(token);
        var classicDevices = DeviceInformation.FindAllAsync(classic, properties, DeviceInformationKind.AssociationEndpoint).AsTask(token);
        var results = await Task.WhenAll(leDevices, classicDevices);
        return Paired(results[0], supportsBattery: true).Concat(Paired(results[1], supportsBattery: false))
            .DistinctBy(device => device.Id, StringComparer.OrdinalIgnoreCase).ToArray();

        static IEnumerable<DesktopBluetoothDevice> Paired(DeviceInformationCollection devices, bool supportsBattery) =>
            devices.Where(device => device.Pairing.IsPaired && !string.IsNullOrWhiteSpace(device.Name))
                .Select(device => new DesktopBluetoothDevice(
                    ReadContainerId(device, ContainerId) ?? ReadContainerId(device, BluetoothContainerId) ?? device.Id,
                    device.Name, BatteryEndpointId: supportsBattery ? device.Id : null));
    }

    /// <summary>Reads enabled USB and audio interfaces and groups external interfaces by their physical device.</summary>
    public static async Task<IReadOnlyList<DesktopLocalDevice>> ReadConnectedLocalDevicesAsync(CancellationToken token)
    {
        var usb = $"{InterfaceClass}:=\"{UsbDeviceInterface:B}\"";
        var selector = $"(({MediaDevice.GetAudioRenderSelector()}) OR ({MediaDevice.GetAudioCaptureSelector()}) OR {usb})" +
            " AND System.Devices.InterfaceEnabled:=System.StructuredQueryType.Boolean#True";
        var devices = await DeviceInformation.FindAllAsync(selector, [ContainerId, InterfaceClass],
            DeviceInformationKind.DeviceInterface).AsTask(token);
        return devices.Where(device => !string.IsNullOrWhiteSpace(device.Name))
            // A headset's audio interface usually has a more useful name than its USB interface.
            .OrderBy(device => device.Properties.TryGetValue(InterfaceClass, out var value) && Equals(value, UsbDeviceInterface))
            .Select(device => new DesktopLocalDevice(ReadContainerId(device, ContainerId) ?? device.Id, device.Name))
            .DistinctBy(device => device.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // Built-in audio jacks can share the computer's container while having independent connection state.
    private static string? ReadContainerId(DeviceInformation device, string property) =>
        device.Properties.TryGetValue(property, out var container) && container is Guid id && id != Guid.Empty && id != SystemContainer
            ? id.ToString("D") : null;

    /// <summary>Bounds an optional GATT read so a connected device is announced even when it does not expose a battery level.</summary>
    public static async Task<int?> ReadConnectedBatteryAsync(string endpointId, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try { return await WindowsDesktopBatteries.ReadBatteryLevelAsync(endpointId, timeout.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && timeout.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLineStatus, BatteryFlag, Percent, SystemStatus;
        public uint RemainingSeconds, FullLifeSeconds;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out PowerStatus status);
}
