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

    public Task<ConnectionUsageHistory> ReadHistoryAsync(CancellationToken cancellationToken)
    {
        var at = clock.GetUtcNow();
        var today = TimeZoneInfo.ConvertTime(at, clock.LocalTimeZone).Date;
        var boundaries = Enumerable.Range(0, 30).Select(index => StartOfDay(today.AddDays(index - 29), clock.LocalTimeZone)).ToArray();
        var days = Enumerable.Range(0, 30).Select(index =>
        {
            var date = today.AddDays(index - 29);
            var start = boundaries[index];
            var end = index == 29 ? at : boundaries[index + 1];
            return new ConnectionDayRange(DateOnly.FromDateTime(date), start, end);
        }).ToArray();
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
            var readings = await Task.Run(adapters.Read, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return _window.Add(clock.GetUtcNow(), readings);
        }
        finally { _gate.Release(); }
    }
}
