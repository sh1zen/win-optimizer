using System.Net.NetworkInformation;
using WinOptimizer.Core.Features.Utilities;

namespace WinOptimizer.Infrastructure.Features.Utilities;

/// <summary>Isolates native adapter inventory for deterministic service tests.</summary>
internal interface IConnectionAdapterSource
{
    IReadOnlyList<ConnectionAdapter> Read();
    IReadOnlyList<ConnectionAdapterReading> ReadCounters();
}

/// <summary>Reads each adapter independently so unsupported counters do not hide other connections.</summary>
internal sealed class WindowsConnectionAdapters : IConnectionAdapterSource
{
    public IReadOnlyList<ConnectionAdapter> Read() => ReadInterfaces().Select(ReadAdapter)
        .OrderBy(adapter => adapter.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();

    /// <summary>Samples only the native counters and adapter identity used to match intervals.</summary>
    public IReadOnlyList<ConnectionAdapterReading> ReadCounters() => ReadInterfaces()
        .Select(static adapter => new ConnectionAdapterReading(adapter.Id, ReadCounters(adapter, out _))).ToArray();

    private static NetworkInterface[] ReadInterfaces()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException ex)
        { throw new UtilityOperationException("Windows could not enumerate network adapters. " + ex.Message, ex); }
    }

    private static ConnectionAdapter ReadAdapter(NetworkInterface adapter)
    {
        var counters = ReadCounters(adapter, out var countersDiagnostic);
        var diagnostic = countersDiagnostic ?? "";
        IReadOnlyList<string> dns = [];
        try { dns = adapter.GetIPProperties().DnsAddresses.Select(address => address.ToString()).ToArray(); }
        catch (Exception ex) when (ex is NetworkInformationException or NotSupportedException)
        { diagnostic = diagnostic.Length == 0 ? "DNS: " + ex.Message : diagnostic + Environment.NewLine + "DNS: " + ex.Message; }
        return new(adapter.Id, adapter.Name, adapter.Description, adapter.NetworkInterfaceType.ToString(),
            adapter.OperationalStatus.ToString(), dns, counters, diagnostic);
    }

    private static ConnectionCounters? ReadCounters(NetworkInterface adapter, out string? diagnostic)
    {
        try
        {
            var stats = adapter.GetIPStatistics();
            diagnostic = null;
            return new(stats.BytesSent, stats.BytesReceived,
                checked(stats.UnicastPacketsSent + stats.NonUnicastPacketsSent),
                checked(stats.UnicastPacketsReceived + stats.NonUnicastPacketsReceived));
        }
        catch (Exception ex) when (ex is NetworkInformationException or NotSupportedException or OverflowException)
        { diagnostic = "Counters: " + ex.Message; return null; }
    }
}
