using WinOptimizer.Core.Features.Extensions;

namespace WinOptimizer.Infrastructure.Features.Extensions;

/// <summary>Monotonic native counters before conversion to user-facing rates.</summary>
public sealed record SystemMonitorCounters(
    DateTimeOffset CollectedAt, TimeSpan Elapsed,
    ulong? IdleTime, ulong? KernelTime, ulong? UserTime,
    ulong? TotalMemoryBytes, ulong? AvailableMemoryBytes,
    IReadOnlyDictionary<string, (long Received, long Sent)> Network,
    IReadOnlyList<SystemMonitorSensor> Temperatures,
    IReadOnlyList<SystemMonitorSensor> Fans,
    IReadOnlyList<string> Notices)
{
    public IReadOnlyList<SystemMonitorMetric> HardwareMetrics { get; init; } = [];
}

/// <summary>Isolates Windows and hardware providers from rate calculations and deterministic tests.</summary>
public interface ISystemMonitorProbe
{
    SystemMonitorCounters Read(CancellationToken cancellationToken);
    void Close();
}
