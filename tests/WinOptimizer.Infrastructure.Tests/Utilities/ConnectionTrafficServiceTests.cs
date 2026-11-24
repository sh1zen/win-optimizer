using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.Infrastructure.Features.Utilities;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Utilities;

public sealed class ConnectionTrafficServiceTests
{
    [Fact]
    public async Task TodayRequestsOnlyTheCurrentLocalDayAndRollsOverAtMidnight()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Plus two hours", TimeSpan.FromHours(2), "Fixture", "Fixture");
        var clock = new TestClock { Zone = zone, At = new(2026, 10, 23, 21, 59, 0, TimeSpan.Zero) };
        var source = new HistorySource();
        var service = new ConnectionTrafficService(new Adapters(), source, clock);

        var before = await service.ReadTodayAsync(CancellationToken.None);
        var range = Assert.Single(source.Days);
        Assert.Equal(new DateOnly(2026, 10, 23), before.Date);
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 22, 0, 0, TimeSpan.Zero), range.Start);
        Assert.Equal(clock.At, range.End);
        Assert.Equal(300m, before.BytesSent + before.BytesReceived);

        clock.At = clock.At.AddMinutes(2);
        var after = await service.ReadTodayAsync(CancellationToken.None);
        range = Assert.Single(source.Days);
        Assert.Equal(new DateOnly(2026, 10, 24), after.Date);
        Assert.Equal(TimeSpan.FromMinutes(1), range.End - range.Start);
    }

    [Theory]
    [InlineData(10, 23)]
    [InlineData(11, 25)]
    public async Task HistoryHandlesSkippedAndRepeatedLocalMidnight(int month, int hours)
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new(2020, 1, 1), new(2030, 12, 31),
            TimeSpan.FromHours(1), TimeZoneInfo.TransitionTime.CreateFixedDateRule(new(1, 1, 1, 0, 0, 0), 10, 1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new(1, 1, 1, 1, 0, 0), 11, 1));
        var zone = TimeZoneInfo.CreateCustomTimeZone("Midnight transition fixture", TimeSpan.Zero, "Fixture", "Standard", "Summer", [rule]);
        var clock = new TestClock { At = new(2026, month, 20, 12, 0, 0, TimeSpan.Zero), Zone = zone };
        var source = new HistorySource();

        var result = await new ConnectionTrafficService(new Adapters(), source, clock).ReadHistoryAsync(CancellationToken.None);

        Assert.Equal(30, result.Days.Count);
        var transition = Assert.Single(source.Days, range => range.Date == new DateOnly(2026, month, 1));
        Assert.Equal(TimeSpan.FromHours(hours), transition.End - transition.Start);
        Assert.Equal(month == 10 ? 1 : 0, TimeZoneInfo.ConvertTime(transition.Start, zone).Hour);
        for (var index = 1; index < source.Days.Count; index++) Assert.Equal(source.Days[index - 1].End, source.Days[index].Start);
    }
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
        var interval = (await service.SampleAsync(CancellationToken.None)).Intervals.Last();
        Assert.Equal(100m, interval.UploadBytesPerSecond);
        Assert.Equal(1, interval.MeasuredAdapters);
        Assert.Equal(2, interval.TotalAdapters);
        Assert.Equal(0, history.Calls);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SampleAsync(cancellation.Token));
        Assert.Equal(2, adapters.Calls);
    }

    [Fact]
    public async Task WindowsHistoryStartsAnotherProfileAsSoonAsOneOfFourSlotsFrees()
    {
        var start = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddHours(12);
        var fourStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOne = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOthers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lastStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var profiles = Enumerable.Range(0, 5).Select(index => (WindowsConnectionHistory.HistoryProfile)(async (_, ct) =>
        {
            if (Interlocked.Increment(ref started) == 4) fourStarted.TrySetResult();
            if (index < 3) await releaseOthers.Task.WaitAsync(ct);
            else if (index == 3) await releaseOne.Task.WaitAsync(ct);
            else lastStarted.TrySetResult();
            return new WindowsConnectionHistory.HistorySamples(1, _ => 10, _ => 20);
        })).ToArray();
        var history = new WindowsConnectionHistory(() => profiles);
        var operation = history.ReadAsync(end, [new(new(2026, 10, 20), start, end)], CancellationToken.None);
        try
        {
            await fourStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(lastStarted.Task.IsCompleted);
            releaseOne.SetResult();
            await lastStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(operation.IsCompleted);
        }
        finally
        {
            releaseOne.TrySetResult();
            releaseOthers.TrySetResult();
        }
        Assert.Equal(new ConnectionDailyUsage(new(2026, 10, 20), 50, 100, 5, 5), Assert.Single((await operation).Days));
    }

    [Fact]
    public async Task WindowsHistoryOverlapsProfilesAndCombinesSamplesAndDiagnosticsInProfileOrder()
    {
        var start = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddDays(1).AddHours(12);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unblock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<WindowsConnectionHistory.HistorySamples> First(WindowsConnectionHistory.HistoryQuery query, CancellationToken ct)
        {
            if (query.First == 0)
            {
                firstStarted.TrySetResult();
                await unblock.Task.WaitAsync(ct);
                throw new UnauthorizedAccessException("first profile blocked");
            }
            return new(1, _ => 30, _ => 40);
        }
        Task<WindowsConnectionHistory.HistorySamples> Second(WindowsConnectionHistory.HistoryQuery query, CancellationToken ct)
        {
            if (query.First != 0) return Task.FromResult(new WindowsConnectionHistory.HistorySamples(1, _ => 50, _ => 60));
            secondStarted.TrySetResult();
            return Task.FromException<WindowsConnectionHistory.HistorySamples>(new InvalidOperationException("second profile blocked"));
        }
        var history = new WindowsConnectionHistory(() => [First, Second]);

        var operation = history.ReadAsync(end,
            [new(new DateOnly(2026, 10, 20), start, start.AddDays(1)), new(new DateOnly(2026, 10, 21), start.AddDays(1), end)], default);
        try
        {
            await Task.WhenAll(firstStarted.Task, secondStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(operation.IsCompleted);
        }
        finally { unblock.TrySetResult(); }

        var result = await operation;
        Assert.Equal(new ConnectionDailyUsage(new DateOnly(2026, 10, 20), null, null, 0, 2), result.Days[0]);
        Assert.Equal(new ConnectionDailyUsage(new DateOnly(2026, 10, 21), 80, 100, 2, 2), result.Days[1]);
        Assert.Equal("Windows usage history: 1 day(s) unavailable for a connection profile. first profile blocked" + Environment.NewLine +
            "Windows usage history: 1 day(s) unavailable for a connection profile. second profile blocked", result.Diagnostic);
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
        public IReadOnlyList<ConnectionAdapter> Read() => throw new InvalidOperationException("Sampling should skip adapter inventory.");
        public IReadOnlyList<ConnectionAdapterReading> ReadCounters()
        { Calls++; return [new("a", new(Sent, 0, 0, 0)), new("unavailable", null)]; }
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
