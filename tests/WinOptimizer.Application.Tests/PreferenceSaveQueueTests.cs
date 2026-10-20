using WinOptimizer.Application.Preferences;
using WinOptimizer.Core.Features.Common.Models;
using Xunit;

namespace WinOptimizer.Application.Tests;

public sealed class PreferenceSaveQueueTests
{
    [Fact]
    public async Task RapidEditsWriteInOrder_AndFlushIncludesEditsAddedWhileWaiting()
    {
        var first = new TaskCompletionSource<OperationResult>();
        List<int> writes = [], published = [];
        var queue = new PreferenceSaveQueue<int>(value =>
        {
            writes.Add(value);
            return value == 1 ? first.Task : Task.FromResult(OperationResult.Succeeded());
        }, published.Add, _ => { }, "failed");
        queue.Enqueue(1);
        var flush = queue.FlushAsync();
        queue.Enqueue(2);
        queue.Enqueue(3);
        Assert.Equal(new[] { 1 }, writes);
        Assert.False(flush.IsCompleted);
        first.SetResult(OperationResult.Succeeded());
        Assert.True(await flush);
        Assert.Equal(new[] { 1, 2, 3 }, writes);
        Assert.Equal(writes, published);
    }

    [Fact]
    public async Task EarlierWriteCannotClearNewerValidationFailure()
    {
        var write = new TaskCompletionSource<OperationResult>();
        string? error = null;
        var queue = new PreferenceSaveQueue<int>(_ => write.Task, _ => { }, value => error = value, "failed");
        queue.Enqueue(1);
        queue.Reject("invalid age");
        write.SetResult(OperationResult.Succeeded());
        Assert.False(await queue.FlushAsync());
        Assert.Equal("invalid age", error);
    }

    [Fact]
    public async Task LaterSuccessfulEditRecoversFromWriteFailure_AndPublishesOnlyPersistedValues()
    {
        List<int> published = [];
        string? error = null;
        var queue = new PreferenceSaveQueue<int>(value => Task.FromResult(value == 1
            ? OperationResult.Failed("disk full") : OperationResult.Succeeded()), published.Add, value => error = value, "failed");
        queue.Enqueue(1);
        Assert.False(await queue.FlushAsync());
        Assert.Equal("disk full", error);
        Assert.Empty(published);
        queue.Enqueue(2);
        Assert.True(await queue.FlushAsync());
        Assert.Null(error);
        Assert.Equal(new[] { 2 }, published);
    }

    [Fact]
    public async Task FilesystemFailureIsReported_AndCanBeRetried()
    {
        var attempts = 0;
        string? error = null;
        var queue = new PreferenceSaveQueue<int>(_ => ++attempts == 1
            ? Task.FromException<OperationResult>(new IOException("access failed"))
            : Task.FromResult(OperationResult.Succeeded()), _ => { }, value => error = value, "failed");
        queue.Enqueue(1);
        Assert.False(await queue.FlushAsync());
        Assert.Equal("access failed", error);
        queue.Enqueue(1);
        Assert.True(await queue.FlushAsync());
        Assert.Null(error);
    }
}
