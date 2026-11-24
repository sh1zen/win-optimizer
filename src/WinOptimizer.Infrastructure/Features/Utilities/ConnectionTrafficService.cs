using WinOptimizer.Core.Features.Utilities;

namespace WinOptimizer.Infrastructure.Features.Utilities;

/// <summary>Isolates Windows usage-history queries from calendar and sampling orchestration.</summary>
internal interface IConnectionHistorySource
{
    Task<ConnectionUsageHistory> ReadAsync(DateTimeOffset at, IReadOnlyList<ConnectionDayRange> days,
        CancellationToken cancellationToken);
}

/// <summary>Serializes counter sampling; history queries never launch VPN discovery or write files.</summary>
internal sealed class ConnectionTrafficService(IConnectionAdapterSource adapters, IConnectionHistorySource history,
    TimeProvider clock) : IConnectionTrafficService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConnectionTrafficWindow _window = new();

    public Task<ConnectionUsageHistory> ReadHistoryAsync(CancellationToken cancellationToken) =>
        ReadHistoryAsync(30, cancellationToken);

    /// <summary>Reads the current local calendar day without querying the previous month.</summary>
    public async Task<ConnectionDailyUsage> ReadTodayAsync(CancellationToken cancellationToken) =>
        (await ReadHistoryAsync(1, cancellationToken).ConfigureAwait(false)).Days.Single();

    private Task<ConnectionUsageHistory> ReadHistoryAsync(int dayCount, CancellationToken cancellationToken)
    {
        var at = clock.GetUtcNow();
        var zone = clock.LocalTimeZone;
        var today = TimeZoneInfo.ConvertTime(at, zone).Date;
        var date = today.AddDays(1 - dayCount);
        var start = StartOfDay(date, zone);
        var days = new ConnectionDayRange[dayCount];
        for (var index = 0; index < days.Length; index++)
        {
            var nextDate = date.AddDays(1);
            var end = index == days.Length - 1 ? at : StartOfDay(nextDate, zone);
            days[index] = new(DateOnly.FromDateTime(date), start, end);
            date = nextDate;
            start = end;
        }
        return history.ReadAsync(at, days, cancellationToken);
    }

    /// <summary>Uses the first real instant of a local date, including skipped and repeated midnights.</summary>
    private static DateTimeOffset StartOfDay(DateTime date, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            var invalid = local;
            var valid = local.AddDays(1);
            while (zone.IsInvalidTime(valid)) valid = valid.AddDays(1);
            while (valid.Ticks - invalid.Ticks > 1)
            {
                var middle = invalid.AddTicks((valid.Ticks - invalid.Ticks) / 2);
                if (zone.IsInvalidTime(middle)) invalid = middle;
                else valid = middle;
            }
            local = valid;
        }
        return zone.IsAmbiguousTime(local)
            ? new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max()).ToUniversalTime()
            : new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
    }

    public async Task<ConnectionLiveTraffic> SampleAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var readings = await Task.Run(adapters.ReadCounters, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return _window.AddCounters(clock.GetUtcNow(), readings);
        }
        finally { _gate.Release(); }
    }
}
