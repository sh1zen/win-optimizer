using System.Net;
using WinOptimizer.Infrastructure.Features.Optimize.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

/// <summary>Exercises the production speed test with deterministic traffic and a monotonic clock.</summary>
public sealed class InternetSpeedTestTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MeasuresMedianLatencyAfterWarmupAndUsesCompletedAdaptiveTransfers(bool fastLink)
    {
        var clock = new TestClock();
        var probes = new Queue<int>([500, 20, 80, 40, 10, 30]);
        var downloadSizes = new List<int>();
        var uploadSizes = new List<long>();
        using var service = Create((request, _) =>
        {
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal("speed.cloudflare.com", request.RequestUri.Host);
            Assert.True(request.Headers.CacheControl?.NoCache);
            if (request.Method == HttpMethod.Get)
            {
                Assert.Equal("/__down", request.RequestUri.AbsolutePath);
                var bytes = int.Parse(request.RequestUri.Query.Split('&')[0][7..]);
                if (bytes != 0) downloadSizes.Add(bytes);
                clock.Advance(bytes switch { 0 => probes.Dequeue(), _ when fastLink => 100, 250_000 => 300, _ => 1000 });
                return Task.FromResult(Response(bytes));
            }

            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/__up", request.RequestUri.AbsolutePath);
            Assert.Equal("application/octet-stream", request.Content!.Headers.ContentType?.MediaType);
            uploadSizes.Add(request.Content.Headers.ContentLength!.Value);
            clock.Advance(fastLink ? 100 : 800);
            return Task.FromResult(Response(0));
        }, clock);

        var result = await service.MeasureAsync(CancellationToken.None);

        Assert.Empty(probes);
        Assert.Equal(fastLink ? [250_000, 1_000_000, 5_000_000, 20_000_000, 25_000_000] : [250_000, 1_000_000], downloadSizes);
        Assert.Equal(fastLink ? [250_000L, 1_000_000L, 5_000_000L, 10_000_000L] : [250_000L], uploadSizes);
        Assert.True(downloadSizes.Sum() + uploadSizes.Sum() < 70_000_000);
        Assert.Equal(fastLink ? 2000 : 8, result.DownloadMbps);
        Assert.Equal(fastLink ? 800 : 2.5, result.UploadMbps);
        Assert.Equal(30, result.LatencyMilliseconds);
    }

    [Fact]
    public async Task RejectsAResponseWithTheWrongByteCount()
    {
        using var service = Create((_, _) => Task.FromResult(Response(1)), new TestClock());
        await Assert.ThrowsAsync<InvalidDataException>(() => service.MeasureAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CancellationStopsTheActiveRequest()
    {
        using var cancel = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = Create(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response(0);
        }, new TestClock());

        var measurement = service.MeasureAsync(cancel.Token);
        await started.Task;
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => measurement);
    }

    private static CloudflareInternetSpeedTest Create(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, TimeProvider clock) =>
        new(new HttpClient(new TestHandler(send)), clock);

    private static HttpResponseMessage Response(int size) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[size]) };

    private sealed class TestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class TestClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(int milliseconds) => _timestamp += TimeSpan.FromMilliseconds(milliseconds).Ticks;
    }
}
