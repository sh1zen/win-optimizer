using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Extensions;
using WinOptimizer.UI.Features.Extensions;
using WinOptimizer.UI.Features.Extensions.Notch;
using WinOptimizer.UI.Features.Extensions.ViewModels;
using WinOptimizer.UI.Tests.Helpers;
using Xunit;

namespace WinOptimizer.UI.Tests.Services;

public sealed class ExtensionApplicationLifetimeTests
{
    private readonly Mock<ISystemMonitorWidgetHost> _host = new();
    private readonly Mock<IUserPreferencesService> _preferences = new();
    private SystemMonitorState _state;
    private int _failures, _shutdown;
    private readonly Mock<ISystemMonitorStartupService> _startup = new();
    private readonly Mock<INotchStartupService> _notchStartup = new();

    public ExtensionApplicationLifetimeTests()
    {
        _startup.Setup(s => s.ApplyAsync(It.IsAny<SystemMonitorOptions>())).ReturnsAsync(OperationResult.Succeeded());
        _notchStartup.Setup(s => s.ApplyAsync(It.IsAny<bool>())).ReturnsAsync(OperationResult.Succeeded());
        SetSavedNotch();
        _host.SetupGet(h => h.State).Returns(() => _state);
        _host.SetupGet(h => h.IsRunning).Returns(() => _state == SystemMonitorState.Running);
        _host.Setup(h => h.ApplyOptionsAsync(It.IsAny<SystemMonitorOptions>())).Returns(Task.CompletedTask);
        _host.Setup(h => h.StopAsync()).Returns(Task.CompletedTask);
        _preferences.Setup(p => p.GetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>()))
            .ReturnsAsync(new SystemMonitorOptions());
        _preferences.Setup(p => p.SetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>()))
            .ReturnsAsync(OperationResult.Succeeded());
    }

    private ExtensionsViewModel Preferences() => new(_preferences.Object, Mock.Of<ILocalizationService>(),
        _host.Object, new ExtensionTestDispatcher(), _startup.Object);
    private ExtensionApplicationLifetime Lifetime(ExtensionsViewModel preferences, NotchExtension? notch = null) => new(_host.Object,
        preferences, notch ?? CreateNotch(),
        () => _failures++, () => _shutdown++, Mock.Of<ILogService>());

    private NotchExtension CreateNotch(TestNotchWindow? window = null) => new(_preferences.Object,
        Mock.Of<ILocalizationService>(), Mock.Of<ILogService>(), _notchStartup.Object, _ => window ?? new TestNotchWindow());

    private void SetSavedNotch(bool enabled = false, bool? startup = null)
    {
        var values = new Dictionary<string, object> { [NotchExtension.PreferenceKey] = enabled };
        if (startup.HasValue) values.Add(NotchExtension.StartupPreferenceKey, startup.Value);
        _preferences.Setup(p => p.ReadSnapshotAsync()).ReturnsAsync(new UserPreferencesSnapshot(values, Mock.Of<ILogService>()));
    }

    [Fact]
    public async Task ClosingShellStopsBothWidgetsAndWaitsForNotchReadings()
    {
        SetSavedNotch(enabled: true);
        var readingsStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new TestNotchWindow { ReadingsStopped = readingsStopped.Task };
        using var notch = CreateNotch(window);
        using var preferences = Preferences();
        using var lifetime = Lifetime(preferences, notch);
        await notch.LoadAsync();
        State(SystemMonitorState.Running);
        var closing = lifetime.CloseAsync();
        Assert.Equal(0, _failures);
        Assert.Equal(0, _shutdown);
        Assert.Equal(1, window.CloseCount);
        Assert.False(notch.IsRunning);
        Assert.True(notch.IsEnabled);
        _host.Verify(h => h.StopAsync(), Times.Once);

        readingsStopped.SetResult();
        await closing;
        Assert.Equal(1, _shutdown);
    }

    private sealed class TestNotchWindow : INotchWindow
    {
        public int CloseCount { get; private set; }
        public Task ReadingsStopped { get; init; } = Task.CompletedTask;
        public event EventHandler? WindowClosed;
        public event EventHandler? OpenOptimizerRequested { add { } remove { } }
        public void ApplyModules(NotchModule modules) { }
        public void ApplyContent(NotchContent content) { }
        public void ApplyPlacement(NotchPlacement placement) { }
        public Task StopReadingsAsync() => ReadingsStopped;
        public void Show() { }
        public void Close() { CloseCount++; WindowClosed?.Invoke(this, EventArgs.Empty); }
    }

    private void State(SystemMonitorState value)
    { _state = value; _host.Raise(h => h.StateChanged += null, EventArgs.Empty); }

    private void EnableMonitorSignIn()
    {
        _preferences.Setup(p => p.GetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>()))
            .ReturnsAsync(new SystemMonitorOptions { Enabled = true, StartWithWindows = true });
        _host.Setup(h => h.ApplyOptionsAsync(It.IsAny<SystemMonitorOptions>())).Callback(() => State(SystemMonitorState.Running)).Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task AutomaticLaunchKeepsTheCompanionAliveWhileSysMonitorRuns()
    {
        EnableMonitorSignIn();
        using var preferences = Preferences();
        using var lifetime = Lifetime(preferences);
        await lifetime.StartInBackgroundAsync();
        Assert.True(_host.Object.IsRunning);
        Assert.Equal(0, _failures);
        Assert.Equal(0, _shutdown);
    }

    [Fact]
    public async Task ManualCompanionLaunchRestoresBothEnabledWidgets()
    {
        SetSavedNotch(enabled: true, startup: false);
        _preferences.Setup(p => p.GetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>()))
            .ReturnsAsync(new SystemMonitorOptions { Enabled = true });
        _host.Setup(h => h.ApplyOptionsAsync(It.IsAny<SystemMonitorOptions>()))
            .Callback(() => State(SystemMonitorState.Running)).Returns(Task.CompletedTask);
        using var preferences = Preferences();
        using var notch = CreateNotch();
        using var lifetime = Lifetime(preferences, notch);

        await lifetime.StartInBackgroundAsync(automaticLaunch: false);

        Assert.True(notch.IsRunning);
        Assert.True(_host.Object.IsRunning);
        Assert.Equal(0, _failures);
        Assert.Equal(0, _shutdown);
        _notchStartup.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignInRestoresNotchAndOnlyAnOptedInSysMonitor(bool monitorStartup)
    {
        SetSavedNotch(enabled: true, startup: true);
        _preferences.Setup(p => p.GetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>()))
            .ReturnsAsync(new SystemMonitorOptions { Enabled = true, StartWithWindows = monitorStartup });
        _host.Setup(h => h.ApplyOptionsAsync(It.IsAny<SystemMonitorOptions>())).Callback(() => State(SystemMonitorState.Running)).Returns(Task.CompletedTask);
        using var preferences = Preferences();
        using var notch = CreateNotch();
        using var lifetime = Lifetime(preferences, notch);

        await lifetime.StartInBackgroundAsync();

        Assert.True(notch.IsRunning);
        Assert.Equal(monitorStartup, _host.Object.IsRunning);
        Assert.Equal(0, _failures);
        Assert.Equal(0, _shutdown);
        _notchStartup.Verify(s => s.ApplyAsync(true), Times.Once);
    }

    [Fact]
    public async Task ExistingNotchPreferenceStartsOnManualLaunchOnlyUntilOptedIn()
    {
        SetSavedNotch(enabled: true);
        using var notch = CreateNotch();

        await notch.LoadAsync(automaticLaunch: true);
        Assert.True(notch.IsEnabled);
        Assert.False(notch.StartWithWindows);
        Assert.False(notch.IsRunning);

        await notch.LoadAsync();
        Assert.True(notch.IsRunning);
        _notchStartup.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task NotchStartupChoicePersistsWhileDisablingNotchRemovesItsTask()
    {
        var calls = new List<bool>();
        _notchStartup.Setup(s => s.ApplyAsync(It.IsAny<bool>())).Callback((bool enabled) => calls.Add(enabled))
            .ReturnsAsync(OperationResult.Succeeded());
        _preferences.Setup(p => p.SetPreferenceAsync(NotchExtension.PreferenceKey, It.IsAny<bool>())).ReturnsAsync(OperationResult.Succeeded());
        _preferences.Setup(p => p.SetPreferenceAsync(NotchExtension.StartupPreferenceKey, true)).ReturnsAsync(OperationResult.Succeeded());
        using var notch = CreateNotch();
        await notch.LoadAsync();

        await notch.SetStartupAsync(true);
        Assert.True(notch.StartWithWindows);
        Assert.False(notch.IsRunning);
        Assert.Empty(calls);

        await notch.SetEnabledAsync(true);
        Assert.True(notch.IsRunning);
        await notch.SetEnabledAsync(false);
        Assert.False(notch.IsRunning);
        Assert.True(notch.StartWithWindows);
        Assert.Equal(new[] { true, false }, calls);
    }

    [Fact]
    public async Task FailedNotchStartupSaveRestoresThePriorRegistration()
    {
        SetSavedNotch(enabled: true);
        var calls = new List<bool>();
        _notchStartup.Setup(s => s.ApplyAsync(It.IsAny<bool>())).Callback((bool enabled) => calls.Add(enabled))
            .ReturnsAsync(OperationResult.Succeeded());
        _preferences.Setup(p => p.SetPreferenceAsync(NotchExtension.StartupPreferenceKey, true))
            .ReturnsAsync(OperationResult.Failed("Preferences could not be saved."));
        using var notch = CreateNotch();
        await notch.LoadAsync();

        await notch.SetStartupAsync(true);

        Assert.False(notch.StartWithWindows);
        Assert.True(notch.IsRunning);
        Assert.Equal("Preferences could not be saved.", notch.Error);
        Assert.Equal(new[] { true, false }, calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task StaleSignInLaunchExitsWithoutOpeningAWidget(bool enabled, bool startup)
    {
        _preferences.Setup(p => p.GetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>()))
            .ReturnsAsync(new SystemMonitorOptions { Enabled = enabled, StartWithWindows = startup });
        using var preferences = Preferences();
        using var lifetime = Lifetime(preferences);
        await lifetime.StartInBackgroundAsync();
        Assert.Equal(1, _shutdown);
        Assert.Equal(0, _failures);
        _host.Verify(h => h.ApplyOptionsAsync(It.IsAny<SystemMonitorOptions>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticLaunchReportsFailureAndExitsWhenNoWidgetStarts(bool invalidPreferences)
    {
        _preferences.Setup(p => p.GetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>()))
            .ReturnsAsync(new SystemMonitorOptions { Enabled = true, StartWithWindows = true, RefreshIntervalSeconds = invalidPreferences ? 0 : 2 });
        using var preferences = Preferences();
        using var lifetime = Lifetime(preferences);
        await lifetime.StartInBackgroundAsync();
        Assert.Equal(1, _failures);
        Assert.Equal(1, _shutdown);
    }

    [Fact]
    public async Task ConcurrentCompanionCloseRequestsWaitForTheSameSysMonitorShutdown()
    {
        var monitorStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.Setup(h => h.StopAsync()).Returns(monitorStopped.Task);
        using var preferences = Preferences();
        using var lifetime = Lifetime(preferences);
        State(SystemMonitorState.Running);
        var closing = lifetime.CloseAsync();
        Assert.Same(closing, lifetime.CloseAsync());
        Assert.Equal(0, _failures);
        Assert.Equal(0, _shutdown);
        monitorStopped.SetResult();
        await closing;
        Assert.Equal(1, _shutdown);
        _host.Verify(h => h.StopAsync(), Times.Once);
    }

    [Theory]
    [InlineData(SystemMonitorState.Disabled)]
    [InlineData(SystemMonitorState.Failed)]
    public async Task ClosingWithoutARunningWidgetStopsTheProcess(SystemMonitorState state)
    {
        using var preferences = Preferences();
        using var lifetime = Lifetime(preferences);
        State(state);
        await lifetime.CloseAsync();
        Assert.Equal(0, _failures);
        Assert.Equal(1, _shutdown);
        _host.Verify(h => h.StopAsync(), Times.Once);
    }

    [Fact]
    public async Task DisablingTheLastWidgetInASignInSessionExitsExactlyOnce()
    {
        EnableMonitorSignIn();
        using var preferences = Preferences();
        using var lifetime = Lifetime(preferences);
        await lifetime.StartInBackgroundAsync();
        State(SystemMonitorState.Disabled);
        Assert.Equal(1, _shutdown);
        await lifetime.CloseAsync();
        Assert.Equal(1, _shutdown);
        _host.Verify(h => h.StopAsync(), Times.Once);
    }

    [Fact]
    public async Task AnUnexpectedWidgetFailureReportsTheErrorAndDrainsTheCompanion()
    {
        EnableMonitorSignIn();
        using var preferences = Preferences();
        using var lifetime = Lifetime(preferences);
        await lifetime.StartInBackgroundAsync();
        State(SystemMonitorState.Failed);
        Assert.Equal(1, _failures);
        Assert.Equal(1, _shutdown);
    }

    [Fact]
    public async Task ClosingWaitsForPendingPreferenceAndWindowChanges()
    {
        var write = new TaskCompletionSource<OperationResult>();
        _preferences.Setup(p => p.SetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>())).Returns(write.Task);
        using var preferences = Preferences();
        await preferences.LoadAsync();
        using var lifetime = Lifetime(preferences);
        preferences.IsEnabled = true;
        var close = lifetime.CloseAsync();
        Assert.False(close.IsCompleted);
        State(SystemMonitorState.Running);
        write.SetResult(OperationResult.Succeeded());
        await close;
        Assert.Equal(0, _failures);
        Assert.Equal(1, _shutdown);
        _host.Verify(h => h.StopAsync(), Times.Once);
    }
}
