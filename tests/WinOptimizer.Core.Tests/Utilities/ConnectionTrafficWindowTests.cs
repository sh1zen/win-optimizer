using WinOptimizer.Core.Features.Utilities;
using Xunit;

namespace WinOptimizer.Core.Tests.Utilities;

public sealed class ConnectionTrafficWindowTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);
    private static ConnectionAdapter Adapter(string id, long sent, long received, long packets = 0) =>
        new(id, id, "", "Ethernet", "Up", [], new(sent, received, packets, packets), "");

    [Fact]
    public void FirstReadingIsABaselineAndRatesUseActualElapsedTime()
    {
        var window = new ConnectionTrafficWindow();
        Assert.Null(Assert.Single(window.Add(Start, [Adapter("a", 5000, 8000)]).Intervals).Totals);
        var sample = window.Add(Start.AddSeconds(2), [Adapter("a", 5200, 8600, 3)]).Intervals.Last();
        Assert.Equal(100m, sample.UploadBytesPerSecond); Assert.Equal(300m, sample.DownloadBytesPerSecond);
        Assert.Equal(3m, sample.Totals?.PacketsReceived);
        Assert.Equal(1, sample.MeasuredAdapters);
    }

    [Fact]
    public void AdapterResetNewAndMissingAdaptersDoNotCreateSpikes()
    {
        var window = new ConnectionTrafficWindow();
        window.Add(Start, [Adapter("stable", 100, 100), Adapter("reset", 5000, 5000), Adapter("gone", 9000, 9000)]);
        var latest = window.Add(Start.AddSeconds(2), [Adapter("stable", 120, 140), Adapter("reset", 0, 0),
            Adapter("new", 100000, 200000)]).Intervals.Last();
        Assert.Equal(10m, latest.UploadBytesPerSecond); Assert.Equal(20m, latest.DownloadBytesPerSecond);
        Assert.Equal(1, latest.MeasuredAdapters); Assert.Equal(3, latest.TotalAdapters);
    }

    [Fact]
    public void MeasuredZeroIsDifferentFromMissingCountersAndMissingSamplesBreakTheBaseline()
    {
        var window = new ConnectionTrafficWindow();
        window.Add(Start, [Adapter("a", 0, 0)]);
        Assert.Equal(0m, window.Add(Start.AddSeconds(2), [Adapter("a", 0, 0)]).Intervals.Last().DownloadBytesPerSecond);
        var missing = Adapter("a", 0, 0) with { Counters = null };
        Assert.Null(window.Add(Start.AddSeconds(4), [missing]).Intervals.Last().Totals);
        Assert.Null(window.Add(Start.AddSeconds(6), [Adapter("a", 9000, 9000)]).Intervals.Last().Totals);
    }

    [Fact]
    public void SleepAndClockChangesEstablishFreshBaselines()
    {
        var window = new ConnectionTrafficWindow();
        window.Add(Start, [Adapter("a", 100, 100)]);
        Assert.Null(window.Add(Start.AddMinutes(2), [Adapter("a", 9000, 9000)]).Intervals.Last().Totals);
        var backwards = window.Add(Start, [Adapter("a", 10000, 10000)]);
        Assert.Null(Assert.Single(backwards.Intervals).Totals);
    }

    [Fact]
    public void RollingWindowRetainsOnlyMeasuredIntervalsWithinTheLatestFifteenMinutes()
    {
        var window = new ConnectionTrafficWindow();
        ConnectionLiveTraffic latest = window.Add(Start, [Adapter("a", 0, 0)]);
        for (var seconds = 2; seconds <= 1800; seconds += 2)
            latest = window.Add(Start.AddSeconds(seconds), [Adapter("a", seconds * 100, seconds * 200)]);
        Assert.Equal(450, latest.Intervals.Count);
        Assert.All(latest.Intervals, interval => Assert.True(interval.Start >= latest.CapturedAt.AddMinutes(-15)));
        Assert.Equal(90000m, latest.Intervals.Sum(interval => interval.Totals?.BytesSent ?? 0));
    }

    [Fact]
    public void AdapterAggregationPreservesLargeCounterDeltas()
    {
        var window = new ConnectionTrafficWindow();
        window.Add(Start, [Adapter("a", 0, 0), Adapter("b", 0, 0)]);
        var latest = window.Add(Start.AddSeconds(2), [Adapter("a", long.MaxValue, long.MaxValue),
            Adapter("b", long.MaxValue, long.MaxValue)]).Intervals.Last();
        Assert.Equal((decimal)long.MaxValue * 2, latest.Totals?.BytesSent);
    }
}
