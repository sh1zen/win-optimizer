using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using WinOptimizer.Core.Features.Utilities;

namespace WinOptimizer.Infrastructure.Features.Utilities;

internal interface IAsusChargeDevice
{
    int? ReadLimit();
    void SetLimit(int percentage);
}

/// <summary>Uses only the charge-limit endpoint of the existing ASUS System Control Interface driver.</summary>
internal sealed class AsusChargeDevice : IAsusChargeDevice
{
    private const uint ChargeEndpoint = 0x00120057;

    public int? ReadLimit()
    {
        var raw = Call(0x53545344, 0);
        var value = raw - 0x10000;
        return value is >= 40 and <= 100 ? value : null;
    }

    public void SetLimit(int percentage)
    {
        if (percentage is < 40 or > 100) throw new ArgumentOutOfRangeException(nameof(percentage));
        if (Call(0x53564544, percentage) != 1) throw new UtilityOperationException("The ASUS driver rejected the charging limit.");
    }

    private static int Call(uint method, int value)
    {
        using var handle = CreateFile(@"\\.\ATKACPI", 0xc0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "The ASUS System Control Interface driver is unavailable or access is denied.");
        var request = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(request, method);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8), ChargeEndpoint);
        BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(12), value);
        var response = new byte[16];
        if (!DeviceIoControl(handle, 0x0022240c, request, (uint)request.Length, response, (uint)response.Length, out var count, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The ASUS charging request failed.");
        if (count < 4) throw new UtilityOperationException("The ASUS driver returned an incomplete charging response.");
        return BinaryPrimitives.ReadInt32LittleEndian(response);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint inputLength,
        byte[] output, uint outputLength, out uint returned, IntPtr overlapped);
}

internal sealed class AsusBatteryFirmware(IAsusChargeDevice device) : IBatteryFirmwareProvider
{
    public bool Matches(string manufacturer) => manufacturer.Contains("ASUS", StringComparison.OrdinalIgnoreCase) ||
        manufacturer.Contains("ASUSTeK", StringComparison.OrdinalIgnoreCase);
    public FirmwareChargeState Read()
    {
        try
        {
            var limit = device.ReadLimit();
            return new("ASUS System Control Interface", limit.HasValue ? Enumerable.Range(40, 61).ToArray() : [], limit,
                limit.HasValue ? BatteryControlIssue.None : BatteryControlIssue.FirmwareUnsupported);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 2 or 3)
        { return new("ASUS System Control Interface", [], null, BatteryControlIssue.InterfaceUnavailable); }
    }
    public void SetLimit(int percentage) => device.SetLimit(percentage);
}
