using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using WinOptimizer.Core.Features.Optimize.Interfaces;

namespace WinOptimizer.Infrastructure.Features.Optimize.Services;

/// <summary>Runs bounded HTTPS probes with synthetic data against Cloudflare's speed test endpoints.</summary>
internal sealed class CloudflareInternetSpeedTest(HttpClient client, TimeProvider clock) : IInternetSpeedTest, IDisposable
{
    private static readonly Uri Origin = new("https://speed.cloudflare.com");
    private static readonly int[] DownloadSizes = [250_000, 1_000_000, 5_000_000, 20_000_000, 25_000_000];
    private static readonly int[] UploadSizes = [250_000, 1_000_000, 5_000_000, 10_000_000];
    private static readonly TimeSpan SampleTarget = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    public async Task<InternetSpeedTestResult> MeasureAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        // Warm the connection so the initial DNS and TLS setup is outside the latency samples.
        await DownloadAsync(0, buffer, cancellationToken).ConfigureAwait(false);
        var latency = new double[5];
        for (var index = 0; index < latency.Length; index++)
            latency[index] = (await DownloadAsync(0, buffer, cancellationToken).ConfigureAwait(false)).TotalMilliseconds;
        Array.Sort(latency);

        var download = await MeasureRateAsync(DownloadSizes, bytes => DownloadAsync(bytes, buffer, cancellationToken)).ConfigureAwait(false);
        var upload = await MeasureRateAsync(UploadSizes, bytes => UploadAsync(bytes, cancellationToken)).ConfigureAwait(false);
        return new(download, upload, latency[latency.Length / 2]);
    }

    /// <summary>Increase the payload on fast links until the sample is long enough or reaches the transfer cap.</summary>
    private static async Task<double> MeasureRateAsync(int[] sizes, Func<int, Task<TimeSpan>> measure)
    {
        double rate = 0;
        foreach (var bytes in sizes)
        {
            var elapsed = await measure(bytes).ConfigureAwait(false);
            rate = bytes * 8d / (elapsed.TotalSeconds * 1_000_000);
            if (elapsed >= SampleTarget) break;
        }
        return rate;
    }

    private async Task<TimeSpan> DownloadAsync(int bytes, byte[] buffer, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get,
            $"/__down?bytes={bytes.ToString(CultureInfo.InvariantCulture)}&nonce={Guid.NewGuid():N}");
        using var deadline = CreateDeadline(cancellationToken);
        var started = clock.GetTimestamp();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length != bytes)
            throw new InvalidDataException("The speed test server returned an unexpected download size.");

        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        var remaining = bytes;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)), deadline.Token).ConfigureAwait(false);
            if (read == 0 && remaining == 0) return Elapsed(started);
            if (read == 0 || read > remaining)
                throw new InvalidDataException("The speed test server returned an unexpected download size.");
            remaining -= read;
        }
    }

    private async Task<TimeSpan> UploadAsync(int bytes, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, "/__up");
        request.Content = new ByteArrayContent(new byte[bytes]);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var deadline = CreateDeadline(cancellationToken);
        var started = clock.GetTimestamp();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return Elapsed(started);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path) => new(method, new Uri(Origin, path))
    {
        Version = HttpVersion.Version20,
        VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        Headers = { CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true } }
    };

    private CancellationTokenSource CreateDeadline(CancellationToken cancellationToken)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestTimeout);
        return deadline;
    }

    // Keep throughput finite when an operation completes within a single clock tick.
    private TimeSpan Elapsed(long started) => TimeSpan.FromTicks(Math.Max(1, clock.GetElapsedTime(started).Ticks));

    public void Dispose() => client.Dispose();
}
