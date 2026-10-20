namespace WinOptimizer.Core.Features.Drivers.Models;

/// <summary>Persisted driver scan policy; only a positively verified WHQL package passes the default filter.</summary>
public sealed record DriverScanOptions
{
    public bool OnlyWhqlUpdates { get; init; } = true;
}

public enum DriverCertification { Unverified, Whql }

/// <summary>One registered device, including registrations for hardware that is currently disconnected.</summary>
public sealed record DriverDevice(string InstanceId, string Name, string ClassName, string InfName,
    bool Present, string Version, string[] HardwareIds)
{
    public string Date { get; init; } = "";
}

/// <summary>An installed driver-store package, distinct from a device registration.</summary>
public sealed record DriverPackage(string PublishedName, string OriginalName, string Provider,
    string ClassName, string Version, bool Inbox, bool BootCritical)
{
    public string Date { get; init; } = "";
    public long? SizeBytes { get; init; }
}

public sealed record DriverInventory(IReadOnlyList<DriverDevice> Devices, IReadOnlyList<DriverPackage> Packages,
    bool BindingsComplete, IReadOnlyList<string> Notices);

/// <summary>An applicable Microsoft Windows Update offer and evidence obtained from its actual package.</summary>
public sealed record DriverUpdate(string Id, string Title, string Provider, string ClassName,
    string HardwareId, string Date, string Version, DriverCertification Certification)
{
    public long? SizeBytes { get; init; }
}

public sealed record DriverUpdateSearchResult(IReadOnlyList<DriverUpdate> Updates, IReadOnlyList<string> Notices);
public sealed record DriverUpdateMatch(DriverUpdate Update, IReadOnlyList<DriverDevice> Devices);

/// <summary>Read-only scan evidence. Update discovery failure is distinct from a successful empty search.</summary>
public sealed record DriverScanReport(DriverInventory Inventory, IReadOnlyList<DriverPackage> UnreferencedPackages,
    IReadOnlyList<DriverDevice> DisconnectedDevices, IReadOnlyList<DriverUpdateMatch> Updates,
    bool UpdatesChecked, int ExcludedUpdates, IReadOnlyList<string> Notices, DriverScanOptions Options);

public sealed record DriverScanProgress(string Stage, int Completed = 0, int Total = 0);

/// <summary>Contextual failures at the driver inventory, Windows Update or package-verification boundary.</summary>
public sealed class DriverScanException(string message, Exception? inner = null) : Exception(message, inner);
