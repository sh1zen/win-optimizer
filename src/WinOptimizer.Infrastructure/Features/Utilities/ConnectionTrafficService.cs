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
        var days = Enumerable.Range(0, 30).Select(index =>
        {
            var date = today.AddDays(index - 29);
            DateTimeOffset Midnight(DateTime value) => new(TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(value, DateTimeKind.Unspecified), clock.LocalTimeZone));
            var start = Midnight(date);
            var end = index == 29 ? at : Midnight(date.AddDays(1));
            return new ConnectionDayRange(DateOnly.FromDateTime(date), start, end);
        }).ToArray();
        return history.ReadAsync(at, days, cancellationToken);
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
