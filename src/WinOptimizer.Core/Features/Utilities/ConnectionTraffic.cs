namespace WinOptimizer.Core.Features.Utilities;

/// <summary>Measured traffic across adapters; decimal sums preserve full native counter precision.</summary>
public sealed record ConnectionTrafficTotals(decimal BytesSent, decimal BytesReceived, decimal PacketsSent, decimal PacketsReceived);

/// <summary>A sampled interval. Null totals mark a baseline, a reset, or unavailable counters.</summary>
public sealed record ConnectionTrafficInterval(DateTimeOffset Start, DateTimeOffset End,
    ConnectionTrafficTotals? Totals, int MeasuredAdapters, int TotalAdapters)
{
    public decimal? UploadBytesPerSecond => Totals is { } totals && End > Start
        ? totals.BytesSent / (decimal)(End - Start).TotalSeconds : null;
    public decimal? DownloadBytesPerSecond => Totals is { } totals && End > Start
        ? totals.BytesReceived / (decimal)(End - Start).TotalSeconds : null;
}

/// <summary>One local calendar day requested from Windows, including the incomplete current day.</summary>
public sealed record ConnectionDayRange(DateOnly Date, DateTimeOffset Start, DateTimeOffset End);

/// <summary>Windows-retained usage. Null byte values mean no profile reported that day.</summary>
public sealed record ConnectionDailyUsage(DateOnly Date, decimal? BytesSent, decimal? BytesReceived,
    int ReportingProfiles, int TotalProfiles);

public sealed record ConnectionUsageHistory(DateTimeOffset CapturedAt, IReadOnlyList<ConnectionDailyUsage> Days, string Diagnostic);
public sealed record ConnectionLiveTraffic(DateTimeOffset CapturedAt, IReadOnlyList<ConnectionTrafficInterval> Intervals);

/// <summary>Reads retained Windows history independently of lightweight live adapter sampling.</summary>
public interface IConnectionTrafficService
{
    Task<ConnectionUsageHistory> ReadHistoryAsync(CancellationToken cancellationToken);
    Task<ConnectionLiveTraffic> SampleAsync(CancellationToken cancellationToken);
}

/// <summary>Owns a bounded fifteen-minute window and matches deltas by adapter identity.</summary>
public sealed class ConnectionTrafficWindow
{
    private readonly List<ConnectionTrafficInterval> _intervals = [];
    private Dictionary<string, ConnectionCounters> _previous = [];
    private DateTimeOffset? _previousAt;

    public ConnectionLiveTraffic Add(DateTimeOffset at, IReadOnlyList<ConnectionAdapter> adapters)
    {
        var current = new Dictionary<string, ConnectionCounters>();
        foreach (var adapter in adapters)
            if (adapter.Counters is { } counters) current.Add(adapter.Id, counters);
        ConnectionTrafficTotals? totals = null;
        var measured = 0;
        // Long pauses, clock adjustments and sleep resume establish a fresh baseline.
        if (_previousAt is { } previousAt && at > previousAt && at - previousAt <= TimeSpan.FromSeconds(10))
        {
            decimal sent = 0, received = 0, sentPackets = 0, receivedPackets = 0;
            foreach (var (id, counters) in current)
            {
                if (!_previous.TryGetValue(id, out var old) || counters.BytesSent < old.BytesSent ||
                    counters.BytesReceived < old.BytesReceived || counters.PacketsSent < old.PacketsSent ||
                    counters.PacketsReceived < old.PacketsReceived) continue;
                sent += (decimal)counters.BytesSent - old.BytesSent;
                received += (decimal)counters.BytesReceived - old.BytesReceived;
                sentPackets += (decimal)counters.PacketsSent - old.PacketsSent;
                receivedPackets += (decimal)counters.PacketsReceived - old.PacketsReceived;
                measured++;
            }
            if (measured > 0) totals = new(sent, received, sentPackets, receivedPackets);
        }
        if (_previousAt > at) _intervals.Clear();
        _intervals.Add(new(_previousAt ?? at, at, totals, measured, adapters.Count));
        _intervals.RemoveAll(interval => interval.Start < at.AddMinutes(-15));
        if (_intervals.Count > 901) _intervals.RemoveRange(0, _intervals.Count - 901);
        _previous = current;
        _previousAt = at;
        return new(at, _intervals.ToArray());
    }
}
