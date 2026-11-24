namespace WinOptimizer.Core.Features.Drivers.Models;

/// <summary>Persisted driver scan policy; only a positively verified WHQL package passes the default filter.</summary>
public sealed record DriverScanOptions
{
    public bool OnlyWhqlUpdates { get; init; } = true;
    public DriverSources Sources { get; init; } = DriverSources.All;
    public NvidiaChannels NvidiaChannels { get; init; } = NvidiaChannels.Both;
    public int AutomaticVerificationLimit { get; init; } = 4;
}

public enum DriverCertification { Unverified, Whql }

/// <summary>One registered device, including registrations for hardware that is currently disconnected.</summary>
public sealed record DriverDevice(string InstanceId, string Name, string ClassName, string InfName,
    bool Present, string Version, string[] HardwareIds)
{
    public string Date { get; init; } = "";
    public string[] CompatibleIds { get; init; } = [];
    public string Manufacturer { get; init; } = "";
    public string MatchingDeviceId { get; init; } = "";
    public string DriverProvider { get; init; } = "";
}

/// <summary>An installed driver-store package, distinct from a device registration.</summary>
public sealed record DriverPackage(string PublishedName, string OriginalName, string Provider,
    string ClassName, string Version, bool Inbox, bool BootCritical)
{
    public string Date { get; init; } = "";
    public long? SizeBytes { get; init; }
}

public sealed record DriverInventory(IReadOnlyList<DriverDevice> Devices, IReadOnlyList<DriverPackage> Packages,
    bool BindingsComplete, IReadOnlyList<string> Notices)
{
    public DriverSystemContext System { get; init; } = new();
}

/// <summary>An applicable Microsoft Windows Update offer and evidence obtained from its actual package.</summary>
public sealed record DriverUpdate(string Id, string Title, string Provider, string ClassName,
    string HardwareId, string Date, string Version, DriverCertification Certification)
{
    public long? SizeBytes { get; init; }
    public DriverCatalogRecord? Catalog { get; init; }
}

public sealed record DriverUpdateSearchResult(IReadOnlyList<DriverUpdate> Updates, IReadOnlyList<string> Notices)
{
    public DriverSourceState State { get; init; } = DriverSourceState.Success;
    public Uri? SupportPage { get; init; }
}
public sealed record DriverUpdateMatch(DriverUpdate Update, IReadOnlyList<DriverDevice> Devices)
{
    public DriverApplicability Applicability { get; init; }
    public IReadOnlyList<DriverDeviceMatch> Comparisons { get; init; } = [];
}

/// <summary>Read-only scan evidence. Update discovery failure is distinct from a successful empty search.</summary>
public sealed record DriverScanReport(DriverInventory Inventory, IReadOnlyList<DriverPackage> UnreferencedPackages,
    IReadOnlyList<DriverDevice> DisconnectedDevices, IReadOnlyList<DriverUpdateMatch> Updates,
    bool UpdatesChecked, int ExcludedUpdates, IReadOnlyList<string> Notices, DriverScanOptions Options)
{
    public IReadOnlyList<DriverSourceOutcome> Sources { get; init; } = [];
    public IReadOnlyList<DriverUpdateMatch> Filtered { get; init; } = [];
}

/// <summary>Milestones in the scan workflow. Within-stage updates leave the phase unspecified.</summary>
public enum DriverScanPhase { None, DetectDevices, CheckSources, PrepareResults }

public sealed record DriverScanProgress(string Stage, int Completed = 0, int Total = 0)
{
    public DriverScanPhase Phase { get; init; }
}

/// <summary>Contextual failures at the driver inventory, Windows Update or package-verification boundary.</summary>
public sealed class DriverScanException(string message, Exception? inner = null) : Exception(message, inner);
