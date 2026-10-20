using System.Runtime.InteropServices;
using Windows.Networking.Connectivity;
using WinOptimizer.Core.Features.Utilities;

namespace WinOptimizer.Infrastructure.Features.Utilities;

/// <summary>Reads local Windows-retained traffic without estimating past usage from lifetime counters.</summary>
internal sealed class WindowsConnectionHistory : IConnectionHistorySource
{
    public Task<ConnectionUsageHistory> ReadAsync(DateTimeOffset at, IReadOnlyList<ConnectionDayRange> days,
        CancellationToken cancellationToken) => Task.Run(async () =>
    {
        var diagnostics = new List<string>();
        var sent = new decimal[days.Count];
        var received = new decimal[days.Count];
        var reporting = new int[days.Count];
        IReadOnlyList<ConnectionProfile> profiles;
        try { profiles = NetworkInformation.GetConnectionProfiles(); }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new ConnectionUsageHistory(at, days.Select(day => new ConnectionDailyUsage(day.Date, null, null, 0, 0)).ToArray(),
                "Windows usage history could not be read. " + ex.Message);
        }
        foreach (var profile in profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var failedDays = 0;
            string? failure = null;
            foreach (var query in CreateQueries(days))
            {
                try
                {
                    // Group regular days in one query; isolate partial and daylight-saving days.
                    var usage = await profile.GetNetworkUsageAsync(query.Start, query.End,
                        query.Count > 1 ? DataUsageGranularity.PerDay : DataUsageGranularity.Total,
                        new NetworkUsageStates { Roaming = TriStates.DoNotCare, Shared = TriStates.DoNotCare })
                        .AsTask(cancellationToken).ConfigureAwait(false);
                    for (var offset = 0; offset < query.Count; offset++)
                    {
                        if (offset >= usage.Count) { failedDays++; continue; }
                        var index = query.First + offset;
                        sent[index] += usage[offset].BytesSent;
                        received[index] += usage[offset].BytesReceived;
                        reporting[index]++;
                    }
                }
                catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                { failedDays += query.Count; failure = ex.Message; }
            }
            if (failedDays > 0) diagnostics.Add($"Windows usage history: {failedDays} day(s) unavailable for a connection profile. {failure}");
        }
        if (profiles.Count == 0) diagnostics.Add("Windows reported no connection profiles with usage history.");
        return new ConnectionUsageHistory(at, days.Select((day, index) => new ConnectionDailyUsage(day.Date,
            reporting[index] == 0 ? null : sent[index], reporting[index] == 0 ? null : received[index],
            reporting[index], profiles.Count)).ToArray(), string.Join(Environment.NewLine, diagnostics.Distinct()));
    }, cancellationToken);

    /// <summary>Maintains exact local-day boundaries with a bounded number of native history queries.</summary>
    internal static IReadOnlyList<HistoryQuery> CreateQueries(IReadOnlyList<ConnectionDayRange> days)
    {
        var queries = new List<HistoryQuery>();
        for (var index = 0; index < days.Count;)
        {
            var first = index++;
            if (days[first].End <= days[first].Start) continue;
            if (days[first].End - days[first].Start == TimeSpan.FromDays(1))
                while (index < days.Count && days[index].End - days[index].Start == TimeSpan.FromDays(1) &&
                    days[index - 1].End == days[index].Start) index++;
            queries.Add(new(first, index - first, days[first].Start, days[index - 1].End));
        }
        return queries;
    }

    internal sealed record HistoryQuery(int First, int Count, DateTimeOffset Start, DateTimeOffset End);
}
