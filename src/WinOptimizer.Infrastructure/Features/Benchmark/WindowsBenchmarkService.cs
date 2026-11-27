using WinOptimizer.Core.Features.Benchmark;
using WinOptimizer.Core.Features.SystemInformation.Interfaces;
using WinOptimizer.Core.Features.SystemInformation.Models;

namespace WinOptimizer.Infrastructure.Features.Benchmark;

/// <summary>Runs one measurement at a time and defers hardware and workload access until requested.</summary>
internal sealed class WindowsBenchmarkService(ISystemInformationService inventory, TimeProvider clock) : IBenchmarkService
{
    private static readonly SystemInformationCategory[] InventoryCategories =
    [
        SystemInformationCategory.Machine, SystemInformationCategory.Windows,
        SystemInformationCategory.Processors, SystemInformationCategory.Graphics
    ];
    private static readonly TimeSpan WorkloadDuration = TimeSpan.FromSeconds(1.25);
    private const int DiskTestBytes = 64 * 1024 * 1024;
    private int _running;

    public async Task<BenchmarkSystemInfo> ReadSystemAsync(CancellationToken token)
    {
        var sections = inventory.ReadSectionsAsync(InventoryCategories, token);
        var hardware = Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var processor = WindowsBenchmarkHardware.ReadProcessor();
            var graphics = WindowsBenchmarkHardware.ReadGraphics();
            var drives = DriveInfo.GetDrives().Where(IsFixedDrive).Select(drive => drive.Name).ToArray();
            return (processor, graphics, drives);
        }, token);
        await Task.WhenAll(sections, hardware).ConfigureAwait(false);
        var (processor, graphics, drives) = await hardware.ConfigureAwait(false);
        return new(await sections.ConfigureAwait(false), processor, graphics, drives);
    }

    public async Task<BenchmarkMeasurement> RunAsync(BenchmarkRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _running, 1) != 0)
            throw new BenchmarkException(BenchmarkFailure.Busy);
        try
        {
            var value = await Task.Run(async () => request.Kind switch
            {
                BenchmarkKind.CpuClock => await WindowsBenchmarkHardware.SampleClockAsync(token).ConfigureAwait(false),
                BenchmarkKind.CpuFlops => BenchmarkWorkloads.MeasureFlops(WorkloadDuration, token),
                BenchmarkKind.MemoryBandwidth => BenchmarkWorkloads.MeasureMemory(WorkloadDuration, token),
                BenchmarkKind.DiskWrite => await MeasureDriveAsync(request.DriveRoot, token).ConfigureAwait(false),
                BenchmarkKind.GpuFlops => await WindowsGpuBenchmark.MeasureAsync(request.GraphicsAdapterId, token).ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(request))
            }, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return new(value, clock.GetUtcNow());
        }
        finally { Volatile.Write(ref _running, 0); }
    }

    private static bool IsFixedDrive(DriveInfo drive) => drive.DriveType == DriveType.Fixed && drive.IsReady;

    /// <summary>Resolves only a ready local fixed volume and writes an exclusive temporary file at its root.</summary>
    private static async Task<double> MeasureDriveAsync(string? root, CancellationToken token)
    {
        if (root is not { Length: 3 } || !char.IsAsciiLetter(root[0]) || root[1] != ':' || root[2] != '\\')
            throw new BenchmarkException(BenchmarkFailure.DriveUnavailable);
        try
        {
            var drive = DriveInfo.GetDrives().FirstOrDefault(drive => drive.Name.Equals(root, StringComparison.OrdinalIgnoreCase));
            if (drive is null || !IsFixedDrive(drive))
                throw new BenchmarkException(BenchmarkFailure.DriveUnavailable);
            if (drive.AvailableFreeSpace < DiskTestBytes)
                throw new BenchmarkException(BenchmarkFailure.InsufficientSpace);
            return await BenchmarkWorkloads.MeasureDiskWriteAsync(drive.RootDirectory.FullName, DiskTestBytes, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new BenchmarkException(BenchmarkFailure.DiskWriteFailed, error);
        }
    }
}
