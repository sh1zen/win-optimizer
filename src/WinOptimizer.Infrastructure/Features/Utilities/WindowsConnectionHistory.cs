using System.Runtime.InteropServices;
using Windows.Networking.Connectivity;
using WinOptimizer.Core.Features.Utilities;

namespace WinOptimizer.Infrastructure.Features.Utilities;

/// <summary>Reads local Windows-retained traffic without estimating past usage from lifetime counters.</summary>
internal sealed class WindowsConnectionHistory : IConnectionHistorySource
{
    private readonly Func<IReadOnlyList<HistoryProfile>> _readProfiles;

    public WindowsConnectionHistory() : this(ReadWindowsProfiles) { }

    internal WindowsConnectionHistory(Func<IReadOnlyList<HistoryProfile>> readProfiles) => _readProfiles = readProfiles;

    internal delegate Task<HistorySamples> HistoryProfile(HistoryQuery query, CancellationToken cancellationToken);

    internal readonly record struct HistorySamples(int Count, Func<int, ulong> Sent, Func<int, ulong> Received);

    private struct ProfileDay
    {
        public ulong Sent;
        public ulong Received;
        public bool Reports;
    }

    private sealed record ProfileHistory(ProfileDay[] Days, string? Diagnostic);

    public Task<ConnectionUsageHistory> ReadAsync(DateTimeOffset at, IReadOnlyList<ConnectionDayRange> days,
        CancellationToken cancellationToken) => Task.Run(async () =>
    {
        var diagnostics = new List<string>();
        var sent = new decimal[days.Count];
        var received = new decimal[days.Count];
        var reporting = new int[days.Count];
        IReadOnlyList<HistoryProfile> profiles;
        try { profiles = _readProfiles(); }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new ConnectionUsageHistory(at, days.Select(day => new ConnectionDailyUsage(day.Date, null, null, 0, 0)).ToArray(),
                "Windows usage history could not be read. " + ex.Message);
        }
        var queries = CreateQueries(days);
        foreach (var batch in profiles.Chunk(4))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var histories = await Task.WhenAll(batch.Select(profile =>
                ReadProfileAsync(profile, queries, days.Count, cancellationToken))).ConfigureAwait(false);
            foreach (var history in histories)
            {
                if (history.Diagnostic is { } diagnostic) diagnostics.Add(diagnostic);
                for (var index = 0; index < days.Count; index++)
                {
                    sent[index] += history.Days[index].Sent;
                    received[index] += history.Days[index].Received;
                    if (history.Days[index].Reports) reporting[index]++;
                }
            }
        }

        if (profiles.Count == 0) diagnostics.Add("Windows reported no connection profiles with usage history.");
        return new ConnectionUsageHistory(at, days.Select((day, index) => new ConnectionDailyUsage(day.Date,
            reporting[index] == 0 ? null : sent[index], reporting[index] == 0 ? null : received[index],
            reporting[index], profiles.Count)).ToArray(), string.Join(Environment.NewLine, diagnostics.Distinct()));
    }, cancellationToken);

    /// <summary>Reads one profile in order and merges its evidence after all independent profile reads complete.</summary>
    private static async Task<ProfileHistory> ReadProfileAsync(HistoryProfile profile, IReadOnlyList<HistoryQuery> queries,
        int dayCount, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var days = new ProfileDay[dayCount];
        var failedDays = 0;
        string? failure = null;
        foreach (var query in queries)
        {
            try
            {
                var usage = await profile(query, cancellationToken).ConfigureAwait(false);
                for (var offset = 0; offset < query.Count; offset++)
                {
                    if (offset >= usage.Count) { failedDays++; continue; }
                    var index = query.First + offset;
                    days[index].Sent = usage.Sent(offset);
                    days[index].Received = usage.Received(offset);
                    days[index].Reports = true;
                }
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            { failedDays += query.Count; failure = ex.Message; }
        }
        return new(days, failedDays > 0 ? $"Windows usage history: {failedDays} day(s) unavailable for a connection profile. {failure}" : null);
    }

    /// <summary>Captures connection identities once and defers Windows usage reads to the corresponding profile.</summary>
    private static HistoryProfile[] ReadWindowsProfiles() => NetworkInformation.GetConnectionProfiles().Select(profile =>
        (HistoryProfile)(async (query, cancellationToken) =>
        {
            var usage = await profile.GetNetworkUsageAsync(query.Start, query.End,
                query.Count > 1 ? DataUsageGranularity.PerDay : DataUsageGranularity.Total,
                new NetworkUsageStates { Roaming = TriStates.DoNotCare, Shared = TriStates.DoNotCare })
                .AsTask(cancellationToken).ConfigureAwait(false);
            return new(usage.Count, index => usage[index].BytesSent, index => usage[index].BytesReceived);
        })).ToArray();

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
