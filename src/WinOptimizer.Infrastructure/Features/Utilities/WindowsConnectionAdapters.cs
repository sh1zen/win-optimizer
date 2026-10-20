using System.Net.NetworkInformation;
using WinOptimizer.Core.Features.Utilities;

namespace WinOptimizer.Infrastructure.Features.Utilities;

/// <summary>Isolates native adapter inventory for deterministic service tests.</summary>
internal interface IConnectionAdapterSource
{
    IReadOnlyList<ConnectionAdapter> Read();
}

/// <summary>Reads each adapter independently so unsupported counters do not hide other connections.</summary>
internal sealed class WindowsConnectionAdapters : IConnectionAdapterSource
{
    public IReadOnlyList<ConnectionAdapter> Read()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Select(ReadAdapter)
                .OrderBy(adapter => adapter.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        catch (NetworkInformationException ex)
        { throw new UtilityOperationException("Windows could not enumerate network adapters. " + ex.Message, ex); }
    }

    private static ConnectionAdapter ReadAdapter(NetworkInterface adapter)
    {
        var diagnostics = new List<string>();
        ConnectionCounters? counters = null;
        IReadOnlyList<string> dns = [];
        try
        {
            var stats = adapter.GetIPStatistics();
            counters = new(stats.BytesSent, stats.BytesReceived,
                checked(stats.UnicastPacketsSent + stats.NonUnicastPacketsSent),
                checked(stats.UnicastPacketsReceived + stats.NonUnicastPacketsReceived));
        }
        catch (Exception ex) when (ex is NetworkInformationException or NotSupportedException or OverflowException)
        { diagnostics.Add("Counters: " + ex.Message); }
        try { dns = adapter.GetIPProperties().DnsAddresses.Select(address => address.ToString()).ToArray(); }
        catch (Exception ex) when (ex is NetworkInformationException or NotSupportedException)
        { diagnostics.Add("DNS: " + ex.Message); }
        return new(adapter.Id, adapter.Name, adapter.Description, adapter.NetworkInterfaceType.ToString(),
            adapter.OperationalStatus.ToString(), dns, counters, string.Join(Environment.NewLine, diagnostics));
    }
}
