using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Extensions;
using WinOptimizer.UI.Features.Extensions;
using WinOptimizer.UI.Features.Extensions.ViewModels;
using WinOptimizer.UI.Tests.Helpers;
using Xunit;

namespace WinOptimizer.UI.Tests.Services;

public sealed class ExtensionApplicationLifetimeTests
{
    private readonly Mock<ISystemMonitorWidgetHost> _host = new();
    private readonly Mock<IUserPreferencesService> _preferences = new();
    private SystemMonitorState _state;
    private int _hidden, _shown, _shutdown;
    private readonly Mock<ISystemMonitorStartupService> _startup = new();

    public ExtensionApplicationLifetimeTests()
    {
        _startup.Setup(s => s.ApplyAsync(It.IsAny<SystemMonitorOptions>())).ReturnsAsync(OperationResult.Succeeded());
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
    private ExtensionApplicationLifetime Lifetime(ExtensionsViewModel preferences) => new(_host.Object,
        preferences, () => _hidden++, () => _shown++, () => _shutdown++, Mock.Of<ILogService>());

    private void State(SystemMonitorState value)
    { _state = value; _host.Raise(h => h.StateChanged += null, EventArgs.Empty); }

    [Fact]
    public async Task AutomaticLaunchKeepsShellHiddenAndGearCanRestoreIt()
    {
        _preferences.Setup(p => p.GetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>()))
            .ReturnsAsync(new SystemMonitorOptions { Enabled = true, StartWithWindows = true });
        _host.Setup(h => h.ApplyOptionsAsync(It.IsAny<SystemMonitorOptions>())).Callback(() => State(SystemMonitorState.Running)).Returns(Task.CompletedTask);
        using var preferences = Preferences();
        using var lifetime = Lifetime(preferences);
        await lifetime.StartInBackgroundAsync();
        Assert.Equal(1, _hidden);
        Assert.Equal(0, _shown);
        Assert.Equal(0, _shutdown);
        lifetime.ShowMainWindow();
        Assert.Equal(1, _shown);
    }

    [Fact]
    public async Task ManualActivationDuringAutomaticLoadingKeepsTheShellVisible()
    {
        var loading = new TaskCompletionSource<SystemMonitorOptions>();
        _preferences.Setup(p => p.GetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>())).Returns(loading.Task);
        using var preferences = Preferences();
        using var lifetime = Lifetime(preferences);
        var starting = lifetime.StartInBackgroundAsync();
        lifetime.ShowMainWindow();
        loading.SetResult(new() { Enabled = true, StartWithWindows = true });
        await starting;
        Assert.Equal(1, _shown);
        Assert.Equal(0, _hidden);
        Assert.Equal(0, _shutdown);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task StaleAutomaticLaunchExitsWithoutOpeningWidgetOrShell(bool enabled, bool startup)
    {
        _preferences.Setup(p => p.GetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>()))
            .ReturnsAsync(new SystemMonitorOptions { Enabled = enabled, StartWithWindows = startup });
        using var preferences = Preferences();
        using var lifetime = Lifetime(preferences);
        await lifetime.StartInBackgroundAsync();
        Assert.Equal(1, _shutdown);
        Assert.Equal(0, _shown);
        _host.Verify(h => h.ApplyOptionsAsync(It.IsAny<SystemMonitorOptions>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticLaunchShowsShellWhenLoadingOrWidgetStartupFails(bool invalidPreferences)
    {
        _preferences.Setup(p => p.GetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>()))
            .ReturnsAsync(new SystemMonitorOptions { Enabled = true, StartWithWindows = true, RefreshIntervalSeconds = invalidPreferences ? 0 : 2 });
        using var preferences = Preferences();
        using var lifetime = Lifetime(preferences);
        await lifetime.StartInBackgroundAsync();
        Assert.Equal(1, _shown);
        Assert.Equal(0, _hidden);
        Assert.Equal(0, _shutdown);
    }

    [Fact]
    public async Task ClosingShellKeepsTheRunningWidgetAndReopeningRestoresTheSameSession()
    {
        using var preferences = Preferences();
        using var lifetime = Lifetime(preferences);
        State(SystemMonitorState.Running);
        await lifetime.CloseAsync();
        Assert.Equal(1, _hidden);
        Assert.Equal(0, _shutdown);
        _host.Verify(h => h.StopAsync(), Times.Never);
        lifetime.ShowMainWindow();
        Assert.Equal(1, _shown);
        State(SystemMonitorState.Disabled);
        Assert.Equal(0, _shutdown);
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
        Assert.Equal(0, _hidden);
        Assert.Equal(1, _shutdown);
        _host.Verify(h => h.StopAsync(), Times.Once);
    }

    [Fact]
    public async Task DisablingTheLastWidgetAfterClosingTheShellExitsExactlyOnce()
    {
        using var preferences = Preferences();
        using var lifetime = Lifetime(preferences);
        State(SystemMonitorState.Running);
        await lifetime.CloseAsync();
        State(SystemMonitorState.Disabled);
        await lifetime.CloseAsync();
        Assert.Equal(1, _shutdown);
        _host.Verify(h => h.StopAsync(), Times.Once);
    }

    [Fact]
    public async Task AnUnexpectedWidgetFailureReopensTheShellForItsError()
    {
        using var preferences = Preferences();
        using var lifetime = Lifetime(preferences);
        State(SystemMonitorState.Running);
        await lifetime.CloseAsync();
        State(SystemMonitorState.Failed);
        Assert.Equal(1, _shown);
        Assert.Equal(0, _shutdown);
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
        Assert.Equal(1, _hidden);
        Assert.Equal(0, _shutdown);
    }
}
