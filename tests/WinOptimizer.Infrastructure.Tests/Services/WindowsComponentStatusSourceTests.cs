using FluentAssertions;
using Moq;
using System.Collections.Concurrent;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Infrastructure.Features.Common.Utilities;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

public class WindowsComponentStatusSourceTests
{
    [Fact]
    public async Task GetStatusAsync_NoComponents_ReturnsEmptyCaseInsensitiveStatusWithoutNativeQueries()
    {
        var log = new Mock<ILogService>(MockBehavior.Strict);
        var result = await new WindowsComponentStatusSource(log.Object)
            .GetStatusAsync(Array.Empty<string>(), Array.Empty<string>());
        result.Capabilities.Should().BeEmpty();
        result.Features.Should().BeEmpty();
        result.Capabilities.Should().BeOfType<Dictionary<string, bool>>().Which.Comparer.Should().Be(StringComparer.OrdinalIgnoreCase);
        result.Features.Should().BeOfType<Dictionary<string, bool>>().Which.Comparer.Should().Be(StringComparer.OrdinalIgnoreCase);
        log.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReusesSessionAfterSuccessAndReopensAfterCapabilityFailure(bool failCapabilities)
    {
        var calls = new List<string>();
        var opened = 0;
        var sessions = new DismSessionManager(_ => new Session((uint)++opened, id => calls.Add($"close {id}")),
            TimeSpan.FromSeconds(30), TimeProvider.System);
        var source = new WindowsComponentStatusSource(Mock.Of<ILogService>(), sessions,
            handle =>
            {
                calls.Add($"capabilities {handle}");
                if (failCapabilities) throw new IOException("Cannot read capabilities");
                return new HashSet<string>(["Test.Capability~~~~0.0.1.0"], StringComparer.OrdinalIgnoreCase);
            },
            handle =>
            {
                calls.Add($"features {handle}");
                return new HashSet<string>(["Test.Feature"], StringComparer.OrdinalIgnoreCase);
            });

        var result = await source.GetStatusAsync(["test.capability", "missing"], ["test.feature", "test.feat"]);

        Assert.Equal(!failCapabilities, result.Capabilities["TEST.CAPABILITY"]);
        Assert.False(result.Capabilities["missing"]);
        Assert.True(result.Features["TEST.FEATURE"]);
        Assert.False(result.Features["test.feat"]);
        Assert.Equal(failCapabilities ? ["capabilities 1", "close 1", "features 2", "close 2"] :
            new[] { "capabilities 1", "features 1", "close 1" }, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EachQueryHasItsOwnDeadlineAndTimedOutWorkCannotRunLaterQueries(bool timeOutCapabilities)
    {
        var clock = new Mock<TimeProvider>();
        var timers = new ConcurrentQueue<Action>();
        clock.Setup(provider => provider.CreateTimer(It.IsAny<TimerCallback>(), It.IsAny<object?>(),
                TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan))
            .Returns((TimerCallback callback, object? state, TimeSpan _, TimeSpan _) =>
            {
                timers.Enqueue(() => callback(state));
                return Mock.Of<ITimer>();
            });
        var disposals = new ConcurrentQueue<uint>();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var originalClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var opened = 0;
        var calls = new ConcurrentQueue<(string Query, uint Session)>();
        var sessions = new DismSessionManager(_ => new Session((uint)Interlocked.Increment(ref opened), id =>
        {
            disposals.Enqueue(id);
            if (id == 1) originalClosed.TrySetResult();
        }), TimeSpan.FromSeconds(30), clock.Object);
        HashSet<string> Read(string query, uint handle, bool shouldWait)
        {
            calls.Enqueue((query, handle));
            if (shouldWait)
            {
                blocked.TrySetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
            return new HashSet<string>([query], StringComparer.OrdinalIgnoreCase);
        }
        var source = new WindowsComponentStatusSource(Mock.Of<ILogService>(), sessions,
            handle => Read("capability", handle, timeOutCapabilities),
            handle => Read("feature", handle, !timeOutCapabilities));

        var operation = source.GetStatusAsync(["capability"], ["feature"]);
        try
        {
            await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(timeOutCapabilities ? 1 : 2, timers.Count);
            timers.Last()();
            var result = await operation.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(!timeOutCapabilities, result.Capabilities["capability"]);
            Assert.Equal(timeOutCapabilities, result.Features["feature"]);
        }
        finally { release.Set(); }

        await originalClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { ("capability", 1u), ("feature", timeOutCapabilities ? 2u : 1u) }, calls);
        Assert.Equal(timeOutCapabilities ? 2 : 1, disposals.Count);
    }

    private sealed class Session(uint handle, Action<uint> onClose) : DismSessionManager.IOnlineSession
    {
        public uint Handle { get; } = handle;
        public void Dispose() => onClose(Handle);
    }
}
