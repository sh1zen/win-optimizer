using System.Threading.Channels;
using Moq;
using WinOptimizer.Application.Updates;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using Xunit;

namespace WinOptimizer.Application.Tests;

/// <summary>Exercises daily scheduling with controlled time and isolated update/persistence boundaries.</summary>
public sealed class BackgroundUpdateCheckServiceTests : IAsyncLifetime
{
    private readonly Mock<IVersionService> _versions = new();
    private readonly Mock<IRemovalScriptUpdateService> _scripts = new();
    private readonly Mock<IUserPreferencesService> _preferences = new();
    private readonly Mock<ILogService> _log = new();
    private readonly Mock<IApplicationModeService> _mode = new();
    private readonly ControlledClock _clock = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<VersionInfo> _notifications = [];
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;
    private Task? _running;

    public BackgroundUpdateCheckServiceTests()
    {
        _preferences.Setup(service => service.GetPreferenceAsync(UserPreferenceKeys.LastAutomaticUpdateCheckUtc, DateTimeOffset.MinValue))
            .ReturnsAsync(() => _lastAttempt);
        _preferences.Setup(service => service.SetPreferenceAsync(UserPreferenceKeys.LastAutomaticUpdateCheckUtc, It.IsAny<DateTimeOffset>()))
            .Callback<string, DateTimeOffset>((_, value) => _lastAttempt = value).ReturnsAsync(OperationResult.Succeeded());
        _preferences.Setup(service => service.GetPreferenceAsync(UserPreferenceKeys.Updates, It.IsAny<UpdateOptions>()))
            .ReturnsAsync(new UpdateOptions());
        _scripts.Setup(service => service.CheckAndUpdateScriptsAsync()).Returns(Task.CompletedTask);
        _versions.Setup(service => service.CheckForUpdateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new VersionInfo());
        _mode.Setup(service => service.BeginSystemOperation()).Returns(Mock.Of<IDisposable>());
    }

    private BackgroundUpdateCheckService CreateService() => new(_versions.Object, _scripts.Object, _preferences.Object, _clock, _log.Object, _mode.Object);
    private Task Start(BackgroundUpdateCheckService service) => _running = service.RunAsync(_notifications.Add, _stop.Token);
    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _stop.Cancel();
        if (_running is not null) await _running.WaitAsync(TimeSpan.FromSeconds(5));
        _stop.Dispose();
    }

    [Fact]
    public async Task ExplicitStart_DefersAllWork_AndRepeatedStartsShareOneLoop()
    {
        var service = CreateService();
        _preferences.VerifyNoOtherCalls(); _versions.VerifyNoOtherCalls(); _scripts.VerifyNoOtherCalls();
        var running = Start(service);
        Assert.Same(running, service.RunAsync(_notifications.Add, _stop.Token));
        var initial = await _clock.NextDelayAsync();
        Assert.Equal(TimeSpan.FromSeconds(30), initial.Due);
        _preferences.VerifyNoOtherCalls(); _versions.VerifyNoOtherCalls(); _scripts.VerifyNoOtherCalls();
        initial.Fire();
        Assert.Equal(TimeSpan.FromDays(1), (await _clock.NextDelayAsync()).Due);
        _scripts.Verify(service => service.CheckAndUpdateScriptsAsync(), Times.Once);
        _versions.Verify(service => service.CheckForUpdateAsync(_stop.Token), Times.Once);
        Assert.Equal(_clock.GetUtcNow(), _lastAttempt);
        Assert.Empty(_notifications);
    }

    [Fact]
    public async Task ChangedPreferencesAreReadAtTheNextScheduledCheck()
    {
        var options = new UpdateOptions { CheckApplicationUpdatesAutomatically = false, UpdateRemovalScriptsAutomatically = false };
        _preferences.Setup(service => service.GetPreferenceAsync(UserPreferenceKeys.Updates, It.IsAny<UpdateOptions>()))
            .ReturnsAsync(() => options);
        _ = Start(CreateService());
        (await _clock.NextDelayAsync()).Fire();
        var next = await _clock.NextDelayAsync();
        _versions.VerifyNoOtherCalls();
        _scripts.VerifyNoOtherCalls();
        _preferences.Verify(service => service.SetPreferenceAsync(UserPreferenceKeys.LastAutomaticUpdateCheckUtc, It.IsAny<DateTimeOffset>()), Times.Never);
        options = new UpdateOptions { UpdateRemovalScriptsAutomatically = false };
        next.Fire();
        await _clock.NextDelayAsync();
        _versions.Verify(service => service.CheckForUpdateAsync(It.IsAny<CancellationToken>()), Times.Once);
        _scripts.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task AutomaticChecksRespectEachSavedPreference(bool applicationUpdates, bool scriptUpdates)
    {
        _preferences.Setup(service => service.GetPreferenceAsync(UserPreferenceKeys.Updates, It.IsAny<UpdateOptions>()))
            .ReturnsAsync(new UpdateOptions { CheckApplicationUpdatesAutomatically = applicationUpdates, UpdateRemovalScriptsAutomatically = scriptUpdates });
        _ = Start(CreateService());
        (await _clock.NextDelayAsync()).Fire();
        await _clock.NextDelayAsync();
        _versions.Verify(service => service.CheckForUpdateAsync(It.IsAny<CancellationToken>()), applicationUpdates ? Times.Once() : Times.Never());
        _scripts.Verify(service => service.CheckAndUpdateScriptsAsync(), scriptUpdates ? Times.Once() : Times.Never());
        _mode.Verify(service => service.BeginSystemOperation(), scriptUpdates ? Times.Once() : Times.Never());
        Assert.Equal(_clock.GetUtcNow(), _lastAttempt);
    }

    [Fact]
    public async Task AttemptIsPersistedBeforeChecks_AndAvailableVersionIsPassedDirectly()
    {
        var update = new VersionInfo { Version = "v26.10.20", IsUpdateAvailable = true };
        _scripts.Setup(service => service.CheckAndUpdateScriptsAsync()).Callback(() => Assert.Equal(_clock.GetUtcNow(), _lastAttempt)).Returns(Task.CompletedTask);
        _versions.Setup(service => service.CheckForUpdateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(update);
        _ = Start(CreateService());
        (await _clock.NextDelayAsync()).Fire();
        await _clock.NextDelayAsync();
        Assert.Same(update, Assert.Single(_notifications));
    }

    [Fact]
    public async Task RecentPersistedAttempt_DefersBothChecksUntilDueAfterRestart()
    {
        _lastAttempt = _clock.GetUtcNow().AddHours(-6);
        _ = Start(CreateService());
        (await _clock.NextDelayAsync()).Fire();
        var remaining = await _clock.NextDelayAsync();
        Assert.Equal(TimeSpan.FromHours(18) - TimeSpan.FromSeconds(30), remaining.Due);
        _scripts.VerifyNoOtherCalls(); _versions.VerifyNoOtherCalls();
        remaining.Fire();
        await _clock.NextDelayAsync();
        _scripts.Verify(service => service.CheckAndUpdateScriptsAsync(), Times.Once);
        _versions.Verify(service => service.CheckForUpdateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OpenApplication_ChecksAgainOnTheNextDay()
    {
        _ = Start(CreateService());
        (await _clock.NextDelayAsync()).Fire();
        var firstAttempt = _clock.GetUtcNow();
        (await _clock.NextDelayAsync()).Fire();
        await _clock.NextDelayAsync();
        Assert.Equal(firstAttempt.AddDays(1), _lastAttempt);
        _scripts.Verify(service => service.CheckAndUpdateScriptsAsync(), Times.Exactly(2));
        _versions.Verify(service => service.CheckForUpdateAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedChecks_AreLoggedAndStillCountAsTheDailyAttempt(bool scriptsFail)
    {
        if (scriptsFail) _scripts.Setup(service => service.CheckAndUpdateScriptsAsync()).ThrowsAsync(new IOException("script failure"));
        else _versions.Setup(service => service.CheckForUpdateAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("offline"));
        _ = Start(CreateService());
        (await _clock.NextDelayAsync()).Fire();
        var next = await _clock.NextDelayAsync();
        Assert.Equal(TimeSpan.FromDays(1), next.Due);
        Assert.Equal(_clock.GetUtcNow(), _lastAttempt);
        _versions.Verify(service => service.CheckForUpdateAsync(It.IsAny<CancellationToken>()), Times.Once);
        _log.Verify(service => service.LogError(It.IsAny<string>(), It.IsAny<Exception>()), Times.Once);
        Assert.Empty(_notifications);
    }

    [Fact]
    public async Task FailedTimestampSave_SkipsChecksAndRetriesOnTheNextDay()
    {
        _preferences.Setup(service => service.SetPreferenceAsync(UserPreferenceKeys.LastAutomaticUpdateCheckUtc, It.IsAny<DateTimeOffset>()))
            .ReturnsAsync(OperationResult.Failed("read-only preferences"));
        _ = Start(CreateService());
        (await _clock.NextDelayAsync()).Fire();
        Assert.Equal(TimeSpan.FromDays(1), (await _clock.NextDelayAsync()).Due);
        _scripts.VerifyNoOtherCalls(); _versions.VerifyNoOtherCalls();
        _log.Verify(service => service.LogError("Cannot run the automatic update checks.", It.IsAny<IOException>()), Times.Once);
    }

    [Fact]
    public async Task CancelBeforeDelayExpires_StopsWithoutReadingPreferencesOrChecking()
    {
        var running = Start(CreateService());
        await _clock.NextDelayAsync();
        _stop.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        _preferences.VerifyNoOtherCalls(); _scripts.VerifyNoOtherCalls(); _versions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CancelDuringVersionRequest_CancelsRequestAndPublishesNothing()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<VersionInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        _versions.Setup(service => service.CheckForUpdateAsync(It.IsAny<CancellationToken>())).Returns((CancellationToken token) =>
        {
            started.SetResult();
            return response.Task.WaitAsync(token);
        });
        var running = Start(CreateService());
        (await _clock.NextDelayAsync()).Fire();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _stop.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(_notifications);
        _log.Verify(service => service.BeginTopic(LogTopic.Updates), Times.Once);
        _log.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FutureTimestamp_StartsANewIntervalAndReportsTheClockChange()
    {
        _lastAttempt = _clock.GetUtcNow().AddDays(10);
        _ = Start(CreateService());
        (await _clock.NextDelayAsync()).Fire();
        await _clock.NextDelayAsync();
        Assert.Equal(_clock.GetUtcNow(), _lastAttempt);
        _versions.Verify(service => service.CheckForUpdateAsync(It.IsAny<CancellationToken>()), Times.Once);
        _log.Verify(service => service.LogWarning(It.Is<string>(message => message.Contains("future"))), Times.Once);
    }

    [Fact]
    public async Task RestartAfterAnAttempt_UsesThePersistedDailyLimit()
    {
        var first = Start(CreateService());
        (await _clock.NextDelayAsync()).Fire();
        await _clock.NextDelayAsync();
        _stop.Cancel();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        using var nextStop = new CancellationTokenSource();
        var restarted = CreateService().RunAsync(_notifications.Add, nextStop.Token);
        try
        {
            (await _clock.NextDelayAsync()).Fire();
            var remaining = await _clock.NextDelayAsync();
            Assert.Equal(TimeSpan.FromDays(1) - TimeSpan.FromSeconds(30), remaining.Due);
            _versions.Verify(service => service.CheckForUpdateAsync(It.IsAny<CancellationToken>()), Times.Once);
            _scripts.Verify(service => service.CheckAndUpdateScriptsAsync(), Times.Once);
        }
        finally { nextStop.Cancel(); await restarted.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task TestModeGuard_ProtectsScriptWritesWhileVersionLookupContinues()
    {
        _mode.Setup(service => service.BeginSystemOperation()).Throws(new InvalidOperationException("Test mode is active."));
        _ = Start(CreateService());
        (await _clock.NextDelayAsync()).Fire();
        await _clock.NextDelayAsync();
        _scripts.VerifyNoOtherCalls();
        _versions.Verify(service => service.CheckForUpdateAsync(It.IsAny<CancellationToken>()), Times.Once);
        _log.Verify(service => service.LogError("Background script update check failed.", It.IsAny<InvalidOperationException>()), Times.Once);
    }

    /// <summary>Queues one-shot delays for explicit advancement without sleeping or network access.</summary>
    private sealed class ControlledClock : TimeProvider
    {
        private readonly Channel<Delay> _delays = Channel.CreateUnbounded<Delay>();
        private readonly object _sync = new();
        private DateTimeOffset _now = new(2026, 10, 19, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() { lock (_sync) return _now; }
        public async Task<Delay> NextDelayAsync() => await _delays.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Delay(this, callback, state);
            timer.Change(dueTime, period);
            _delays.Writer.TryWrite(timer);
            return timer;
        }

        internal sealed class Delay(ControlledClock owner, TimerCallback callback, object? state) : ITimer
        {
            private int _disposed;
            public TimeSpan Due { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("This clock controls one-shot delays.");
                Due = dueTime;
                return Volatile.Read(ref _disposed) == 0;
            }
            public void Fire()
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                lock (owner._sync) owner._now += Due;
                callback(state);
            }
            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
