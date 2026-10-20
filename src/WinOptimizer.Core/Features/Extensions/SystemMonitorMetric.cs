namespace WinOptimizer.Core.Features.Extensions;

/// <summary>Additional hardware quantities with fixed units: %, GB, MHz, W, V, % and B/s respectively.</summary>
public enum SystemMonitorMetricKind { GpuUsage, GpuMemory, ClockSpeed, Power, Voltage, StorageActivity, StorageThroughput }

/// <summary>A typed hardware reading. Memory uses binary GB and throughput uses bytes per second.</summary>
public sealed record SystemMonitorMetric(SystemMonitorMetricKind Kind, string Name, double Value);
