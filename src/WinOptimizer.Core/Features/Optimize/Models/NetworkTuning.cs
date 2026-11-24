using System.Globalization;

namespace WinOptimizer.Core.Features.Optimize.Models;

/// <summary>Windows owner of an individually editable, on-demand network setting.</summary>
public enum NetworkTuningOwner { Tcp, Offload, IPv4, IPv6, NameResolution, DeliveryOptimization }
public enum NetworkTuningInput { Toggle, Selection, Number }

/// <summary>A known native value, including a known absence of a registry override.</summary>
public sealed record NetworkTuningValue(string? Value);

/// <summary>Defines the native field, accepted values and optional fixed suggestion. A missing suggestion keeps the original.</summary>
public sealed record NetworkTuningDefinition(string Id, NetworkTuningOwner Owner, string Property,
    string Parameter, NetworkTuningInput Input, IReadOnlyList<string> Options,
    uint Minimum = 0, uint Maximum = uint.MaxValue, uint Step = 1)
{
    public bool HasRegistryOverride => Owner is NetworkTuningOwner.NameResolution or NetworkTuningOwner.DeliveryOptimization;
    public NetworkTuningValue? Suggestion { get; init; }

    /// <summary>Validates requests before native writes; null clears an optional registry override.</summary>
    public void Validate(string? value)
    {
        if (value == null && HasRegistryOverride) return;
        if (Input != NetworkTuningInput.Number)
        {
            if (value != null && Options.Contains(value, StringComparer.Ordinal)) return;
        }
        else if (uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number >= Minimum && number <= Maximum && number % Step == 0) return;
        throw new ArgumentException($"Invalid value for network setting '{Id}'.", nameof(value));
    }
}

/// <summary>Pairs a live Windows read with the first available value saved on this machine.</summary>
public sealed record NetworkTuningState(string Id, string? Value, bool IsAvailable, string? ErrorMessage = null)
{
    public NetworkTuningValue? Original { get; init; }
}

/// <summary>Owns the allowlist shared by network reads, writes, presentation and validation.</summary>
public static class NetworkTuningCatalog
{
    public const string AutoTuningId = "tcp-auto-tuning";
    public const string RssId = "offload-rss";
    public const string NormalAutoTuning = "Normal";
    public const string Enabled = "Enabled";
    private const string Disabled = "Disabled";
    private static readonly NetworkTuningValue NoOverride = new(Value: null);

    public static IReadOnlyList<NetworkTuningDefinition> All { get; } = Array.AsReadOnly<NetworkTuningDefinition>(
    [
        new(AutoTuningId, NetworkTuningOwner.Tcp, "AutoTuningLevelLocal", "AutoTuningLevelLocal", NetworkTuningInput.Selection,
            Array.AsReadOnly(new[] { Disabled, "HighlyRestricted", "Restricted", NormalAutoTuning, "Experimental" })) { Suggestion = new(NormalAutoTuning) },
        Toggle("tcp-ecn", NetworkTuningOwner.Tcp, "EcnCapability", Disabled),
        new("tcp-timestamps", NetworkTuningOwner.Tcp, "Timestamps", "Timestamps", NetworkTuningInput.Selection,
            Array.AsReadOnly(new[] { Disabled, Enabled, "Allowed" })),
        Toggle("tcp-scaling-heuristics", NetworkTuningOwner.Tcp, "ScalingHeuristics"),
        Toggle("tcp-non-sack-resiliency", NetworkTuningOwner.Tcp, "NonSackRttResiliency", Disabled),
        Toggle("tcp-force-window-scaling", NetworkTuningOwner.Tcp, "ForceWS", Disabled),
        new("tcp-initial-rto", NetworkTuningOwner.Tcp, "InitialRto", "InitialRtoMs", NetworkTuningInput.Number, [], 300, 3000, 10),
        new("tcp-syn-retries", NetworkTuningOwner.Tcp, "MaxSynRetransmissions", "MaxSynRetransmissions", NetworkTuningInput.Number, [], 2, 8),
        Toggle(RssId, NetworkTuningOwner.Offload, "ReceiveSideScaling", Enabled),
        Toggle("offload-rsc", NetworkTuningOwner.Offload, "ReceiveSegmentCoalescing", Enabled),
        Toggle("offload-task", NetworkTuningOwner.Offload, "TaskOffload", Enabled),
        Number("ipv4-hop-limit", NetworkTuningOwner.IPv4, "DefaultHopLimit", "DefaultHopLimit", 1, 255, new("128")),
        Number("ipv4-neighbor-cache", NetworkTuningOwner.IPv4, "NeighborCacheLimit", "NeighborCacheLimitEntries", suggestion: new("256")),
        Number("ipv4-route-cache", NetworkTuningOwner.IPv4, "RouteCacheLimit", "RouteCacheLimitEntries"),
        Toggle("ipv4-media-sense", NetworkTuningOwner.IPv4, "DhcpMediaSense", Enabled),
        Toggle("ipv4-icmp-redirects", NetworkTuningOwner.IPv4, "IcmpRedirects"),
        Number("ipv6-hop-limit", NetworkTuningOwner.IPv6, "DefaultHopLimit", "DefaultHopLimit", 1, 255, new("128")),
        Number("ipv6-neighbor-cache", NetworkTuningOwner.IPv6, "NeighborCacheLimit", "NeighborCacheLimitEntries", suggestion: new("256")),
        Number("ipv6-route-cache", NetworkTuningOwner.IPv6, "RouteCacheLimit", "RouteCacheLimitEntries"),
        Toggle("ipv6-media-sense", NetworkTuningOwner.IPv6, "DhcpMediaSense", Enabled),
        Toggle("ipv6-icmp-redirects", NetworkTuningOwner.IPv6, "IcmpRedirects"),
        Number("priority-local", NetworkTuningOwner.NameResolution, "LocalPriority", "LocalPriority", 0, 65535, NoOverride),
        Number("priority-hosts", NetworkTuningOwner.NameResolution, "HostsPriority", "HostsPriority", 0, 65535, NoOverride),
        Number("priority-dns", NetworkTuningOwner.NameResolution, "DnsPriority", "DnsPriority", 0, 65535, NoOverride),
        Number("priority-netbt", NetworkTuningOwner.NameResolution, "NetbtPriority", "NetbtPriority", 0, 65535, NoOverride),
        Number("delivery-background-limit", NetworkTuningOwner.DeliveryOptimization,
            "DOMaxBackgroundDownloadBandwidth", "DOMaxBackgroundDownloadBandwidth", 0, suggestion: NoOverride)
    ]);

    public static NetworkTuningDefinition Get(string id) => All.FirstOrDefault(item => item.Id == id)
        ?? throw new ArgumentException($"Unknown network setting '{id}'.", nameof(id));

    private static NetworkTuningDefinition Toggle(string id, NetworkTuningOwner owner, string property, string? suggestedValue = null) =>
        new(id, owner, property, property, NetworkTuningInput.Toggle, Array.AsReadOnly(new[] { Disabled, Enabled }))
        { Suggestion = suggestedValue == null ? null : new(suggestedValue) };

    private static NetworkTuningDefinition Number(string id, NetworkTuningOwner owner, string property, string parameter,
        uint minimum = 1, uint maximum = uint.MaxValue, NetworkTuningValue? suggestion = null) =>
        new(id, owner, property, parameter, NetworkTuningInput.Number, [], minimum, maximum) { Suggestion = suggestion };
}
