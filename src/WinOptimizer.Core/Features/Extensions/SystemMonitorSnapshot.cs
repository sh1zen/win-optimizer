namespace WinOptimizer.Core.Features.Extensions;

/// <summary>A measured hardware sensor; its provider name is preserved for identification.</summary>
public sealed record SystemMonitorSensor(string Name, double Value);

/// <summary>One local sample. Null quantities are unavailable, including rates before a second sample.</summary>
public sealed record SystemMonitorSnapshot(
    DateTimeOffset CollectedAt,
    double? CpuUsagePercent,
    ulong? TotalMemoryBytes,
    ulong? AvailableMemoryBytes,
    double? DownloadBytesPerSecond,
    double? UploadBytesPerSecond,
    IReadOnlyList<SystemMonitorSensor> Temperatures,
    IReadOnlyList<SystemMonitorSensor> Fans,
    IReadOnlyList<string> Notices)
{
    public IReadOnlyList<SystemMonitorMetric> HardwareMetrics { get; init; } = [];
}
