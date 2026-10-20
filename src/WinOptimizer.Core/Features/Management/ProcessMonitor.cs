namespace WinOptimizer.Core.Features.Management;

/// <summary>Creation time is part of process identity; PID alone is not a lifetime identifier.</summary>
public sealed record ProcessIdentity(int Pid, long StartedAtUtcTicks)
{
    public bool CanInspect => Pid > 0 && StartedAtUtcTicks > 0;
}

/// <summary>One native counter reading. Unavailable measurements remain nullable.</summary>
public sealed record ProcessReading(ProcessIdentity Identity, int ParentPid, string Name, string ImagePath,
    long? CpuTicks, long? WorkingSetBytes, long? PrivateBytes, ulong? ReadBytes, ulong? WriteBytes,
    int? Threads, int? Handles, string Diagnostic);
public sealed record ProcessMetric(ProcessReading Reading, double? CpuPercent, double? ReadBytesPerSecond, double? WriteBytesPerSecond);
public sealed record ProcessSnapshot(DateTimeOffset CapturedAt, IReadOnlyList<ProcessMetric> Processes);
public sealed record ProcessPort(string Protocol, string LocalAddress, int LocalPort, string RemoteAddress, int? RemotePort, string State);
public sealed record ProcessPortSnapshot(ProcessIdentity Identity, DateTimeOffset CapturedAt,
    IReadOnlyList<ProcessPort> Ports, IReadOnlyList<string> Warnings);
public sealed record ProcessInspection(ProcessIdentity Identity, DateTimeOffset CapturedAt, string CommandLine,
    string Owner, string Description, string Publisher, IReadOnlyList<ProcessPort> Ports, IReadOnlyList<string> Warnings);
public sealed record ProcessOpenFile(string Handle, string ObjectType, string Path);
public sealed record ProcessFileSnapshot(ProcessIdentity Identity, DateTimeOffset CapturedAt,
    IReadOnlyList<ProcessOpenFile> Files, string Diagnostic);
public sealed record ProcessIpTraffic(string Protocol, string RemoteAddress, ulong SentBytes, ulong ReceivedBytes,
    long Events, DateTimeOffset LastSeen);
public enum ProcessNetworkState { Running, Stopped, Failed, ProcessEnded }
public sealed record ProcessNetworkSnapshot(ProcessIdentity Identity, DateTimeOffset StartedAt, DateTimeOffset CapturedAt,
    ProcessNetworkState State, IReadOnlyList<ProcessIpTraffic> Hosts, long? LostEvents,
    long UnattributedEvents, bool CapacityReached, string Diagnostic);

/// <summary>Read-only process observation; ETW capture and optional Handle acquisition are explicit actions.</summary>
public interface IProcessMonitorService
{
    Task<ProcessSnapshot> SampleAsync(CancellationToken cancellationToken);
    Task<ProcessInspection> InspectAsync(ProcessIdentity identity, CancellationToken cancellationToken);
    Task<ProcessPortSnapshot> ReadPortsAsync(ProcessIdentity identity, CancellationToken cancellationToken);
    Task<ProcessFileSnapshot> ReadOpenFilesAsync(ProcessIdentity identity, bool acceptSysinternalsLicense, CancellationToken cancellationToken);
    Task StartNetworkAsync(ProcessIdentity identity, CancellationToken cancellationToken);
    Task<ProcessNetworkSnapshot?> ReadNetworkAsync(CancellationToken cancellationToken);
    Task<ProcessNetworkSnapshot?> StopNetworkAsync();
}
