using System.Globalization;

namespace WinOptimizer.Core.Features.Optimize.Models;

/// <summary>Windows owner of an individually editable, on-demand network setting.</summary>
public enum NetworkTuningOwner { Tcp, Offload, IPv4, IPv6, NameResolution }
public enum NetworkTuningInput { Toggle, Selection, Number }

/// <summary>Defines the native property, write parameter and accepted values for a network control.</summary>
public sealed record NetworkTuningDefinition(string Id, NetworkTuningOwner Owner, string Property,
    string Parameter, NetworkTuningInput Input, IReadOnlyList<string> Options,
    uint Minimum = 0, uint Maximum = uint.MaxValue, uint Step = 1)
{
    /// <summary>Validates requests before constructing native commands; null clears a legacy registry override.</summary>
    public void Validate(string? value)
    {
        if (value == null && Owner == NetworkTuningOwner.NameResolution) return;
        if (Input != NetworkTuningInput.Number)
        {
            if (value != null && Options.Contains(value, StringComparer.Ordinal)) return;
        }
        else if (uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number >= Minimum && number <= Maximum && number % Step == 0) return;
        throw new ArgumentException($"Invalid value for network setting '{Id}'.", nameof(value));
    }
}

/// <summary>A native read result. An available null value means a legacy override is absent.</summary>
public sealed record NetworkTuningState(string Id, string? Value, bool IsAvailable, string? ErrorMessage = null);

/// <summary>Owns the allowlist shared by network reads, writes, presentation and validation.</summary>
public static class NetworkTuningCatalog
{
    public static IReadOnlyList<NetworkTuningDefinition> All { get; } = Array.AsReadOnly<NetworkTuningDefinition>(
    [
        new("tcp-auto-tuning", NetworkTuningOwner.Tcp, "AutoTuningLevelLocal", "AutoTuningLevelLocal", NetworkTuningInput.Selection,
            Array.AsReadOnly(new[] { "Disabled", "HighlyRestricted", "Restricted", "Normal", "Experimental" })),
        Toggle("tcp-ecn", NetworkTuningOwner.Tcp, "EcnCapability"),
        Toggle("tcp-timestamps", NetworkTuningOwner.Tcp, "Timestamps"),
        Toggle("tcp-scaling-heuristics", NetworkTuningOwner.Tcp, "ScalingHeuristics"),
        Toggle("tcp-non-sack-resiliency", NetworkTuningOwner.Tcp, "NonSackRttResiliency"),
        Toggle("tcp-force-window-scaling", NetworkTuningOwner.Tcp, "ForceWS"),
        new("tcp-initial-rto", NetworkTuningOwner.Tcp, "InitialRto", "InitialRtoMs", NetworkTuningInput.Number, [], 300, 3000, 10),
        new("tcp-syn-retries", NetworkTuningOwner.Tcp, "MaxSynRetransmissions", "MaxSynRetransmissions", NetworkTuningInput.Number, [], 2, 8),
        Toggle("offload-rss", NetworkTuningOwner.Offload, "ReceiveSideScaling"),
        Toggle("offload-rsc", NetworkTuningOwner.Offload, "ReceiveSegmentCoalescing"),
        Toggle("offload-task", NetworkTuningOwner.Offload, "TaskOffload"),
        Number("ipv4-hop-limit", NetworkTuningOwner.IPv4, "DefaultHopLimit", "DefaultHopLimit", 1, 255),
        Number("ipv4-neighbor-cache", NetworkTuningOwner.IPv4, "NeighborCacheLimit", "NeighborCacheLimitEntries"),
        Number("ipv4-route-cache", NetworkTuningOwner.IPv4, "RouteCacheLimit", "RouteCacheLimitEntries"),
        Toggle("ipv4-media-sense", NetworkTuningOwner.IPv4, "DhcpMediaSense"),
        Toggle("ipv4-icmp-redirects", NetworkTuningOwner.IPv4, "IcmpRedirects"),
        Number("ipv6-hop-limit", NetworkTuningOwner.IPv6, "DefaultHopLimit", "DefaultHopLimit", 1, 255),
        Number("ipv6-neighbor-cache", NetworkTuningOwner.IPv6, "NeighborCacheLimit", "NeighborCacheLimitEntries"),
        Number("ipv6-route-cache", NetworkTuningOwner.IPv6, "RouteCacheLimit", "RouteCacheLimitEntries"),
        Toggle("ipv6-media-sense", NetworkTuningOwner.IPv6, "DhcpMediaSense"),
        Toggle("ipv6-icmp-redirects", NetworkTuningOwner.IPv6, "IcmpRedirects"),
        Number("priority-local", NetworkTuningOwner.NameResolution, "LocalPriority", "LocalPriority", 0, 65535),
        Number("priority-hosts", NetworkTuningOwner.NameResolution, "HostsPriority", "HostsPriority", 0, 65535),
        Number("priority-dns", NetworkTuningOwner.NameResolution, "DnsPriority", "DnsPriority", 0, 65535),
        Number("priority-netbt", NetworkTuningOwner.NameResolution, "NetbtPriority", "NetbtPriority", 0, 65535)
    ]);

    public static NetworkTuningDefinition Get(string id) => All.FirstOrDefault(item => item.Id == id)
        ?? throw new ArgumentException($"Unknown network setting '{id}'.", nameof(id));

    private static NetworkTuningDefinition Toggle(string id, NetworkTuningOwner owner, string property) =>
        new(id, owner, property, property, NetworkTuningInput.Toggle, Array.AsReadOnly(new[] { "Disabled", "Enabled" }));

    private static NetworkTuningDefinition Number(string id, NetworkTuningOwner owner, string property, string parameter,
        uint minimum = 1, uint maximum = uint.MaxValue) => new(id, owner, property, parameter, NetworkTuningInput.Number, [], minimum, maximum);
}
