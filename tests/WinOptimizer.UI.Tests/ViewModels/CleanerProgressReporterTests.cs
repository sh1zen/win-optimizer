using System.Collections.Concurrent;
using WinOptimizer.Core.Features.Cleaner.Models;
using WinOptimizer.UI.Features.Cleaner.ViewModels;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

/// <summary>Checks bounded progress delivery without clocks, delays or a live cleanup operation.</summary>
public sealed class CleanerProgressReporterTests
{
    [Fact]
    public void ConcurrentFileReportsQueueOnlyTheLatestDisplayUpdate()
    {
        var context = new QueuedContext();
        List<CleanerProgress> published = [];
        var reporter = new CleanerProgressReporter(published.Add, context);
        Parallel.For(0, 10000, index => reporter.Report(new("file", index, 10000)));
        var final = new CleanerProgress("last file", 10000, 10000);
        reporter.Report(final);
        Assert.Equal(1, context.PendingCount);
        Assert.Empty(published);
        context.RunNext();
        Assert.Same(final, Assert.Single(published));
        Assert.Equal(0, context.PendingCount);
    }

    [Fact]
    public void ReportsArrivingDuringPublicationReceiveTheNextCallback()
    {
        var context = new QueuedContext();
        List<CleanerProgress> published = [];
        CleanerProgressReporter? reporter = null;
        var first = new CleanerProgress("discovery", 0, 0);
        var next = new CleanerProgress("inspection", 1, 2);
        reporter = new CleanerProgressReporter(value =>
        {
            published.Add(value);
            if (value == first) reporter!.Report(next);
        }, context);
        reporter.Report(first);
        context.RunNext();
        Assert.Equal(1, context.PendingCount);
        context.RunNext();
        Assert.Equal(new[] { first, next }, published);
        Assert.Equal(0, context.PendingCount);
    }

    /// <summary>Captures posted work so delivery and concurrency remain deterministic.</summary>
    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = new();
        public int PendingCount => _callbacks.Count;
        public override void Post(SendOrPostCallback callback, object? state) => _callbacks.Enqueue((callback, state));
        public void RunNext()
        {
            Assert.True(_callbacks.TryDequeue(out var work));
            work.Callback(work.State);
        }
    }
}
