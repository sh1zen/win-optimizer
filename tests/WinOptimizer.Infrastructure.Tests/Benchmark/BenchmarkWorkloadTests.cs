using Moq;
using WinOptimizer.Core.Features.Benchmark;
using WinOptimizer.Core.Features.SystemInformation.Interfaces;
using WinOptimizer.Infrastructure.Features.Benchmark;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Benchmark;

/// <summary>Checks live workload accounting and the temporary-file boundary without benchmarking an actual user volume.</summary>
public sealed class BenchmarkWorkloadTests
{
    [Fact]
    public void CpuAndMemoryWorkloadsMeasureCompletedWork()
    {
        var duration = TimeSpan.FromMilliseconds(40);
        Assert.InRange(BenchmarkWorkloads.MeasureFlops(duration, default), double.Epsilon, double.MaxValue);
        Assert.InRange(BenchmarkWorkloads.MeasureMemory(duration, default), double.Epsilon, double.MaxValue);
    }

    [Fact]
    public async Task DiskWorkloadDeletesItsFileAndLeavesExistingFilesAlone()
    {
        var directory = Directory.CreateTempSubdirectory("win-optimizer-bench-");
        var existing = Path.Combine(directory.FullName, "existing.txt");
        const string contents = "Keep this content";
        try
        {
            await File.WriteAllTextAsync(existing, contents);
            var result = await BenchmarkWorkloads.MeasureDiskWriteAsync(directory.FullName, 1024 * 1024 + 13, default);
            Assert.InRange(result, double.Epsilon, double.MaxValue);
            Assert.Equal(existing, Assert.Single(Directory.EnumerateFileSystemEntries(directory.FullName)));
            Assert.Equal(contents, await File.ReadAllTextAsync(existing));
        }
        finally
        {
            File.Delete(existing);
            directory.Delete();
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("C:")]
    [InlineData(@"C:\Users")]
    [InlineData(@"\\server\share\")]
    public async Task DiskBenchmarkRejectsInputsOutsideLocalDriveRoots(string? path)
    {
        var service = new WindowsBenchmarkService(Mock.Of<ISystemInformationService>(), TimeProvider.System);
        var failure = await Assert.ThrowsAsync<BenchmarkException>(() => service.RunAsync(new(BenchmarkKind.DiskWrite, path), default));
        Assert.Equal(BenchmarkFailure.DriveUnavailable, failure.Failure);
    }

    [Fact]
    public async Task GpuBenchmarkRequiresAnExplicitAdapter()
    {
        var service = new WindowsBenchmarkService(Mock.Of<ISystemInformationService>(), TimeProvider.System);
        var failure = await Assert.ThrowsAsync<BenchmarkException>(() => service.RunAsync(new(BenchmarkKind.GpuFlops), default));
        Assert.Equal(BenchmarkFailure.GraphicsUnavailable, failure.Failure);
    }

    [Fact]
    public async Task CancelledRequestsDoNotStartWork()
    {
        var token = new CancellationToken(true);
        var service = new WindowsBenchmarkService(Mock.Of<ISystemInformationService>(), TimeProvider.System);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync(new(BenchmarkKind.DiskWrite), token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync(new(BenchmarkKind.GpuFlops, GraphicsAdapterId: 5), token));
        Assert.ThrowsAny<OperationCanceledException>(() => BenchmarkWorkloads.MeasureFlops(TimeSpan.Zero, token));
        Assert.ThrowsAny<OperationCanceledException>(() => BenchmarkWorkloads.MeasureMemory(TimeSpan.Zero, token));
    }
}
