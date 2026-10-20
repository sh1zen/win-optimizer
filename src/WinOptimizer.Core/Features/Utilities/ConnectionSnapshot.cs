namespace WinOptimizer.Core.Features.Utilities;

/// <summary>Cumulative adapter counters reported by Windows since the adapter last reset.</summary>
public sealed record ConnectionCounters(long BytesSent, long BytesReceived, long PacketsSent, long PacketsReceived)
{
    public decimal TotalBytes => (decimal)BytesSent + BytesReceived;
    public decimal TotalPackets => (decimal)PacketsSent + PacketsReceived;
}

/// <summary>One adapter, including disconnected and virtual interfaces; unavailable counters stay null.</summary>
public sealed record ConnectionAdapter(string Id, string Name, string Description, string Kind, string Status,
    IReadOnlyList<string> DnsServers, ConnectionCounters? Counters, string Diagnostic);

/// <summary>A Windows VPN profile in the interactive user's or all-users phone book.</summary>
public sealed record ConnectionVpn(string Name, string Server, string Status, bool AllUsers, bool SplitTunneling);

/// <summary>A dated snapshot. Totals sum available adapter readings and may include the same traffic at multiple layers.</summary>
public sealed record ConnectionSnapshot(DateTimeOffset CapturedAt, IReadOnlyList<ConnectionAdapter> Adapters,
    IReadOnlyList<ConnectionVpn> VpnProfiles, string VpnDiagnostic)
{
    public int MeasuredAdapters => Adapters.Count(adapter => adapter.Counters != null);
    public decimal BytesSent => Adapters.Sum(adapter => (decimal)(adapter.Counters?.BytesSent ?? 0));
    public decimal BytesReceived => Adapters.Sum(adapter => (decimal)(adapter.Counters?.BytesReceived ?? 0));
    public decimal PacketsSent => Adapters.Sum(adapter => (decimal)(adapter.Counters?.PacketsSent ?? 0));
    public decimal PacketsReceived => Adapters.Sum(adapter => (decimal)(adapter.Counters?.PacketsReceived ?? 0));
}

/// <summary>Reads local network state and opens the interactive user's Windows VPN manager on request.</summary>
public interface IConnectionService
{
    Task<ConnectionSnapshot> ReadAsync();
    Task OpenVpnSettingsAsync();
}
