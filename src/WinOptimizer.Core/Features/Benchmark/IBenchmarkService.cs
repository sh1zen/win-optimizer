using WinOptimizer.Core.Features.SystemInformation.Models;

namespace WinOptimizer.Core.Features.Benchmark;

public enum BenchmarkKind { CpuClock, CpuFlops, MemoryBandwidth, DiskWrite, GpuFlops }

public sealed record BenchmarkProcessor(int Family, int Model, int Stepping, IReadOnlyList<string> Instructions);
public sealed record BenchmarkGraphicsAdapter(string Name, ulong DedicatedVideoMemory, ulong Id);

public sealed record BenchmarkSystemInfo(IReadOnlyList<SystemInformationSection> Inventory,
    BenchmarkProcessor? Processor, IReadOnlyList<BenchmarkGraphicsAdapter> Graphics, IReadOnlyList<string> DriveRoots);

/// <summary>Measures MHz, FLOPS, or bytes per second according to the test kind.</summary>
public readonly record struct BenchmarkMeasurement(double Value, DateTimeOffset CompletedAt);

/// <summary>Carries the selected target for a disk or graphics workload.</summary>
public readonly record struct BenchmarkRequest(BenchmarkKind Kind, string? DriveRoot = null, ulong? GraphicsAdapterId = null);

public enum BenchmarkFailure { Busy, ClockUnavailable, DriveUnavailable, InsufficientSpace, DiskWriteFailed, GraphicsUnavailable }

public sealed class BenchmarkException(BenchmarkFailure failure, Exception? inner = null) : Exception(failure.ToString(), inner)
{
    public BenchmarkFailure Failure { get; } = failure;
}

/// <summary>Reads local system details and runs bounded, explicitly requested performance tests.</summary>
public interface IBenchmarkService
{
    Task<BenchmarkSystemInfo> ReadSystemAsync(CancellationToken token);
    Task<BenchmarkMeasurement> RunAsync(BenchmarkRequest request, CancellationToken token);
}
