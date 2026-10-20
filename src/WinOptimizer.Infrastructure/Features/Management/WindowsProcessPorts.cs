using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using WinOptimizer.Core.Features.Management;

namespace WinOptimizer.Infrastructure.Features.Management;

/// <summary>Reads owning-PID TCP and UDP endpoint tables for both IP families; UDP has no remote-peer state.</summary>
internal sealed class WindowsProcessPorts
{
    public IReadOnlyList<ProcessPort> Read(int pid, List<string> warnings)
    {
        var ports = new List<ProcessPort>();
        foreach (var family in new[] { 2, 23 })
        foreach (var tcp in new[] { true, false })
        {
            try { ports.AddRange(Parse(ReadTable(family, tcp), family == 23, tcp, pid)); }
            catch (Exception ex) when (ex is Win32Exception or InvalidDataException or OverflowException)
            { warnings.Add($"{(tcp ? "TCP" : "UDP")} IPv{(family == 2 ? 4 : 6)}: {ex.Message}"); }
        }
        return ports.OrderBy(port => port.Protocol).ThenBy(port => port.LocalAddress).ThenBy(port => port.LocalPort).ToArray();
    }

    /// <summary>Validates native buffer lengths before reading rows; exposed internally for binary-fixture tests.</summary>
    internal static IReadOnlyList<ProcessPort> Parse(byte[] table, bool ipv6, bool tcp, int pid)
    {
        if (table.Length < 4) throw new InvalidDataException("The endpoint table header is incomplete.");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(table);
        var stride = ipv6 ? tcp ? 56 : 28 : tcp ? 24 : 12;
        if ((ulong)count * (uint)stride > (ulong)(table.Length - 4)) throw new InvalidDataException("The endpoint table rows are incomplete.");
        var result = new List<ProcessPort>();
        for (var index = 0; index < count; index++)
        {
            var row = table.AsSpan(checked(4 + (int)index * stride), stride);
            var ownerOffset = ipv6 ? tcp ? 52 : 24 : tcp ? 20 : 8;
            if (BinaryPrimitives.ReadUInt32LittleEndian(row[ownerOffset..]) != (uint)pid) continue;
            var addressOffset = ipv6 || !tcp ? 0 : 4;
            var portOffset = ipv6 ? 20 : tcp ? 8 : 4;
            var local = ipv6 ? new IPAddress(row[..16], BinaryPrimitives.ReadUInt32LittleEndian(row[16..])).ToString()
                : new IPAddress(row.Slice(addressOffset, 4)).ToString();
            var localPort = BinaryPrimitives.ReadUInt16BigEndian(row[portOffset..]);
            var remote = !tcp ? "" : ipv6 ? new IPAddress(row.Slice(24, 16), BinaryPrimitives.ReadUInt32LittleEndian(row[40..])).ToString()
                : new IPAddress(row.Slice(12, 4)).ToString();
            int? remotePort = tcp ? BinaryPrimitives.ReadUInt16BigEndian(row[(ipv6 ? 44 : 16)..]) : null;
            var state = tcp ? ((TcpState)BinaryPrimitives.ReadUInt32LittleEndian(row[(ipv6 ? 48 : 0)..])).ToString() : "Bound";
            result.Add(new((tcp ? "TCP" : "UDP") + (ipv6 ? "6" : "4"), local, localPort, remote, remotePort, state));
        }
        return result;
    }

    private static byte[] ReadTable(int family, bool tcp)
    {
        var size = 0;
        uint Query(IntPtr buffer) => tcp ? GetExtendedTcpTable(buffer, ref size, true, family, 5, 0)
            : GetExtendedUdpTable(buffer, ref size, true, family, 1, 0);
        var error = Query(IntPtr.Zero);
        for (var retry = 0; retry < 4; retry++)
        {
            if (error != 122 && error != 0) throw new Win32Exception(checked((int)error));
            if (size < 4 || size > 64 * 1024 * 1024) throw new InvalidDataException("Windows returned an invalid endpoint table size.");
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                error = Query(buffer);
                if (error == 122) continue;
                if (error != 0) throw new Win32Exception(checked((int)error));
                var bytes = new byte[size]; Marshal.Copy(buffer, bytes, 0, size); return bytes;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new InvalidDataException("The endpoint table kept changing. Refresh to retry.");
    }

    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedUdpTable(IntPtr table, ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);
}
