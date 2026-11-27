using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace WinOptimizer.Infrastructure.Features.Benchmark;

/// <summary>Measures completed work with a monotonic clock and checks cancellation between bounded chunks.</summary>
internal static class BenchmarkWorkloads
{
    private const int FloatBatch = 8192;
    private const int MemoryBufferSize = 128 * 1024 * 1024;
    private const int DiskChunkSize = 1024 * 1024;

    internal static double MeasureFlops(TimeSpan duration, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _ = FloatWork(0.5); // Warm the kernel before timing it.
        long batches = 0;
        var stopwatch = Stopwatch.StartNew();
        Parallel.For(0, Environment.ProcessorCount, new ParallelOptions { CancellationToken = token }, worker =>
        {
            long localBatches = 0;
            var checksum = (worker + 1d) / Environment.ProcessorCount;
            do
            {
                token.ThrowIfCancellationRequested();
                checksum = FloatWork(checksum);
                localBatches++;
            } while (stopwatch.Elapsed < duration);
            if (!double.IsFinite(checksum))
                throw new ArithmeticException("The floating-point benchmark returned a non-finite checksum.");
            Interlocked.Add(ref batches, localBatches);
        });
        // Four multiply-adds per iteration, two operations per lane in each multiply-add.
        return batches * (double)FloatBatch * 8 * Vector<double>.Count / stopwatch.Elapsed.TotalSeconds;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static double FloatWork(double seed)
    {
        var a = new Vector<double>(seed);
        var b = new Vector<double>(seed * 0.5);
        var c = new Vector<double>(seed * 0.25);
        var d = new Vector<double>(seed * 0.125);
        var multiplier = new Vector<double>(0.99991);
        var offset = new Vector<double>(0.00001);
        for (int i = 0; i < FloatBatch; i++)
        {
            a = Vector.MultiplyAddEstimate(a, multiplier, offset);
            b = Vector.MultiplyAddEstimate(b, multiplier, offset);
            c = Vector.MultiplyAddEstimate(c, multiplier, offset);
            d = Vector.MultiplyAddEstimate(d, multiplier, offset);
        }
        return Vector.Sum(a + b + c + d) / (4 * Vector<double>.Count);
    }

    internal static double MeasureMemory(TimeSpan duration, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var source = GC.AllocateUninitializedArray<byte>(MemoryBufferSize);
        var destination = GC.AllocateUninitializedArray<byte>(MemoryBufferSize);
        source.AsSpan().Fill(0xA5);
        source.AsSpan().CopyTo(destination); // Commit both buffers before timing.
        long bytes = 0;
        var stopwatch = Stopwatch.StartNew();
        do
        {
            token.ThrowIfCancellationRequested();
            source.AsSpan().CopyTo(destination);
            (source, destination) = (destination, source);
            bytes += source.Length;
        } while (stopwatch.Elapsed < duration);
        return bytes / stopwatch.Elapsed.TotalSeconds;
    }

    internal static async Task<double> MeasureDiskWriteAsync(string directory, int bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var data = GC.AllocateUninitializedArray<byte>(bytes);
        RandomNumberGenerator.Fill(data); // Keep generation and compressibility out of the timed writes.
        token.ThrowIfCancellationRequested();
        var path = Path.Combine(directory, $".win-optimizer-bench-{Guid.NewGuid():N}.tmp");
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 1, FileOptions.Asynchronous | FileOptions.WriteThrough | FileOptions.DeleteOnClose);
        var stopwatch = Stopwatch.StartNew();
        for (int offset = 0; offset < data.Length; offset += DiskChunkSize)
        {
            await file.WriteAsync(data.AsMemory(offset, Math.Min(DiskChunkSize, data.Length - offset)), token).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();
        file.Flush(flushToDisk: true);
        return bytes / stopwatch.Elapsed.TotalSeconds;
    }
}
