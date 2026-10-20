using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.Infrastructure.Features.Utilities;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Utilities;

public sealed class ConnectionTrafficServiceTests
{
    [Theory]
    [InlineData(3, 30, 23)]
    [InlineData(10, 30, 25)]
    public async Task HistoryRequestsThirtyLocalCalendarDaysIncludingDaylightSavingAndPartialToday(int month, int day, int transitionHours)
    {
        var daylight = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2020, 1, 1), new DateTime(2030, 12, 31),
            TimeSpan.FromHours(1), TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday));
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test European zone", TimeSpan.FromHours(1), "Test", "Standard", "Summer", [daylight]);
        var clock = new TestClock { At = new(2026, month, day, 12, 30, 0, TimeSpan.Zero), Zone = zone };
        var source = new HistorySource();
        var service = new ConnectionTrafficService(new Adapters(), source, clock);
        var result = await service.ReadHistoryAsync(CancellationToken.None);
        Assert.Equal(30, source.Days.Count); Assert.Equal(30, result.Days.Count);
        Assert.Equal(new DateOnly(2026, month, day).AddDays(-29), source.Days[0].Date);
        Assert.Equal(clock.At, source.Days[^1].End);
        Assert.Contains(source.Days, range => range.End - range.Start == TimeSpan.FromHours(transitionHours));
        for (var index = 1; index < source.Days.Count; index++) Assert.Equal(source.Days[index - 1].End, source.Days[index].Start);
        var queries = WindowsConnectionHistory.CreateQueries(source.Days);
        Assert.InRange(queries.Count, 3, 4);
        Assert.Equal(30, queries.Sum(query => query.Count));
        Assert.Contains(queries, query => query.Count == 1 && query.End - query.Start == TimeSpan.FromHours(transitionHours));
    }

    [Fact]
    public async Task SamplingUsesOnlyAdapterCountersAndHonorsCancellation()
    {
        var clock = new TestClock(); var adapters = new Adapters(); var history = new HistorySource();
        var service = new ConnectionTrafficService(adapters, history, clock);
        Assert.Null(Assert.Single((await service.SampleAsync(CancellationToken.None)).Intervals).Totals);
        clock.At = clock.At.AddSeconds(2); adapters.Sent = 200;
        Assert.Equal(100m, (await service.SampleAsync(CancellationToken.None)).Intervals.Last().UploadBytesPerSecond);
        Assert.Equal(0, history.Calls);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SampleAsync(cancellation.Token));
        Assert.Equal(2, adapters.Calls);
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset At { get; set; } = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        public TimeZoneInfo Zone { get; init; } = TimeZoneInfo.Utc;
        public override DateTimeOffset GetUtcNow() => At;
        public override TimeZoneInfo LocalTimeZone => Zone;
    }

    private sealed class Adapters : IConnectionAdapterSource
    {
        public int Calls { get; private set; }
        public long Sent { get; set; }
        public IReadOnlyList<ConnectionAdapter> Read()
        { Calls++; return [new("a", "Test", "", "Ethernet", "Up", [], new(Sent, 0, 0, 0), "")]; }
    }

    private sealed class HistorySource : IConnectionHistorySource
    {
        public int Calls { get; private set; }
        public IReadOnlyList<ConnectionDayRange> Days { get; private set; } = [];
        public Task<ConnectionUsageHistory> ReadAsync(DateTimeOffset at, IReadOnlyList<ConnectionDayRange> days, CancellationToken cancellationToken)
        {
            Calls++; Days = days;
            return Task.FromResult(new ConnectionUsageHistory(at, days.Select(range =>
                new ConnectionDailyUsage(range.Date, 100, 200, 1, 1)).ToArray(), ""));
        }
    }
}
