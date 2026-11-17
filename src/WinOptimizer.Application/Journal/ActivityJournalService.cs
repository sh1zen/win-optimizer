using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Application.Journal;

/// <summary>Merges bounded newest-first reads across diagnostic sessions and change receipts.</summary>
public sealed class ActivityJournalService(ILogService logs, IChangeHistoryService history) : IActivityJournalService
{
    public async Task<IActivityJournalSession> OpenAsync(JournalQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!Enum.IsDefined(query.Filter) || query.Topic is { } topic && !Enum.IsDefined(topic))
            throw new ArgumentException("Unknown journal filter.", nameof(query));
        var diagnosticFiles = query.Filter == JournalFilter.Changes
            ? Task.FromResult<IReadOnlyList<LogFileInfo>>([]) : logs.GetFilesAsync(cancellationToken);
        var changeFiles = query.Filter is JournalFilter.Diagnostics or JournalFilter.Problems
            ? Task.FromResult<IReadOnlyList<LogFileInfo>>([]) : history.GetFilesAsync(cancellationToken);
        await Task.WhenAll(diagnosticFiles, changeFiles).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var sources = (await diagnosticFiles.ConfigureAwait(false)).Select(file => new Source(file, true))
            .Concat((await changeFiles.ConfigureAwait(false)).Select(file => new Source(file, false)))
            .OrderByDescending(source => source.File.LastWriteTime).ThenBy(source => source.File.FileName, StringComparer.Ordinal).ToArray();
        return new Session(logs, history, query, sources);
    }

    /// <summary>Holds only one source's bounded queue and reverse byte cursor, never an open file.</summary>
    private sealed class Source(LogFileInfo file, bool diagnostic)
    {
        public LogFileInfo File { get; } = file;
        public bool Diagnostic { get; } = diagnostic;
        public long? Offset { get; set; }
        public bool HasMore { get; set; } = true;
        public Queue<JournalRecord> Buffer { get; } = new();
    }

    /// <summary>Serializes query pagination and exposes incomplete sources instead of treating them as empty.</summary>
    private sealed class Session(ILogService logs, IChangeHistoryService history, JournalQuery query, Source[] sources) : IActivityJournalSession
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly List<string> _notices = [];

        public async Task<JournalPage> ReadNextAsync(CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                List<JournalRecord> result = [];
                var unreadable = 0;
                var scanned = 0;
                var ready = new PriorityQueue<int, (long Timestamp, int Order)>();
                // Bound both UI output and work for sparse filters; HasMore offers continuation.
                var allPrimed = true;
                for (var index = 0; index < sources.Length; index++)
                {
                    if (!await PrimeAsync(index).ConfigureAwait(false))
                    {
                        allPrimed = false;
                        break;
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                while (allPrimed && ready.TryDequeue(out var next, out _))
                {
                    var record = sources[next].Buffer.Dequeue();
                    if (record.Matches(query)) result.Add(record);
                    if (result.Count == 200 || scanned >= 2000) break;

                    // Only the source just consumed can need refilling or have a new head.
                    allPrimed = await PrimeAsync(next).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                return new(result, sources.Any(source => source.Buffer.Count > 0 || source.HasMore), unreadable, _notices.ToArray());

                async Task<bool> PrimeAsync(int index)
                {
                    var source = sources[index];
                    while (source.Buffer.Count == 0 && source.HasMore && scanned < 2000)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            var oldOffset = source.Offset;
                            int count;
                            int invalid;
                            if (source.Diagnostic)
                            {
                                var page = await logs.ReadRecentAsync(source.File.FileName, source.Offset, 32, cancellationToken).ConfigureAwait(false);
                                foreach (var entry in page.Entries) source.Buffer.Enqueue(JournalRecord.From(source.File.FileName, entry));
                                source.Offset = page.NextOffset; source.HasMore = page.HasMore;
                                count = page.Entries.Count; invalid = page.UnreadableEntries;
                            }
                            else
                            {
                                var page = await history.ReadRecentAsync(source.File.FileName, source.Offset, 32, cancellationToken).ConfigureAwait(false);
                                foreach (var entry in page.Entries) source.Buffer.Enqueue(JournalRecord.From(source.File.FileName, entry));
                                source.Offset = page.NextOffset; source.HasMore = page.HasMore;
                                count = page.Entries.Count; invalid = page.UnreadableEntries;
                            }
                            if (oldOffset.HasValue && source.HasMore && source.Offset >= oldOffset)
                                throw new InvalidOperationException("The journal cursor did not advance. Refresh the activity list.");
                            scanned += Math.Max(1, count + invalid);
                            unreadable += invalid;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                        {
                            source.HasMore = false;
                            _notices.Add($"{source.File.FileName}: {ex.Message}");
                        }
                    }
                    if (!source.Buffer.TryPeek(out var head)) return !source.HasMore;
                    // Retain the existing source order whenever timestamps are equal or absent.
                    ready.Enqueue(index, (-(head.Timestamp?.UtcTicks ?? 0), index));
                    return true;
                }
            }
            finally { _gate.Release(); }
        }
    }
}
