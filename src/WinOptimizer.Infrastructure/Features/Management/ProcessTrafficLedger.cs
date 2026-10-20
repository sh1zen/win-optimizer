using System.Net;
using WinOptimizer.Core.Features.Management;

namespace WinOptimizer.Infrastructure.Features.Management;

/// <summary>Counts ETW transfer metadata by remote IP, with a bounded set and explicit attribution gaps.</summary>
internal sealed class ProcessTrafficLedger(ProcessIdentity identity, DateTimeOffset startedAt, int capacity = 4096)
{
    private readonly object _sync = new();
    private readonly Dictionary<(string Protocol, string Address), ProcessIpTraffic> _hosts = [];
    private long _unattributed;
    private bool _capacityReached;

    public void Add(int pid, DateTimeOffset at, string protocol, bool sent, IPAddress source, IPAddress destination,
        int bytes, IReadOnlySet<string> localAddresses)
    {
        if (pid != identity.Pid || at < startedAt || at.UtcTicks < identity.StartedAtUtcTicks || bytes < 0) return;
        static string Normalize(IPAddress ip) => Convert.ToHexString((ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).GetAddressBytes());
        var sourceKey = Normalize(source); var destinationKey = Normalize(destination);
        var sourceLocal = localAddresses.Contains(sourceKey) || IPAddress.IsLoopback(source);
        var destinationLocal = localAddresses.Contains(destinationKey) || IPAddress.IsLoopback(destination);
        IPAddress? remote = sourceLocal && !destinationLocal ? destination : destinationLocal && !sourceLocal ? source
            : sourceLocal && destinationLocal && sourceKey == destinationKey ? destination : null;
        lock (_sync)
        {
            if (remote == null) { _unattributed++; return; }
            if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
            var key = (protocol, remote.ToString());
            if (!_hosts.TryGetValue(key, out var prior))
            {
                if (_hosts.Count >= capacity) { _capacityReached = true; _unattributed++; return; }
                prior = new(protocol, key.Item2, 0, 0, 0, at);
            }
            _hosts[key] = prior with
            {
                SentBytes = checked(prior.SentBytes + (sent ? (ulong)bytes : 0)),
                ReceivedBytes = checked(prior.ReceivedBytes + (sent ? 0 : (ulong)bytes)),
                Events = checked(prior.Events + 1), LastSeen = at > prior.LastSeen ? at : prior.LastSeen
            };
        }
    }

    public ProcessNetworkSnapshot Snapshot(DateTimeOffset at, long? lostEvents, string error)
    {
        lock (_sync)
            return new(identity, startedAt, at, error.Length == 0 ? ProcessNetworkState.Running : ProcessNetworkState.Failed,
                _hosts.Values.OrderByDescending(host => (decimal)host.SentBytes + host.ReceivedBytes).ToArray(),
                lostEvents.HasValue ? Math.Max(0, lostEvents.Value) : null, _unattributed, _capacityReached, error);
    }
}
