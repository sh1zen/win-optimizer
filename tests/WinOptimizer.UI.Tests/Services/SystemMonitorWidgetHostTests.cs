using System.Runtime.InteropServices;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Extensions;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Extensions;
using WinOptimizer.UI.Features.Extensions.ViewModels;
using WinOptimizer.UI.Tests.Helpers;
using Xunit;

namespace WinOptimizer.UI.Tests.Services;

public sealed class SystemMonitorWidgetHostTests
{
    private readonly Mock<ISystemMonitorService> _monitor = new();
    private readonly Mock<ISystemMonitorWindow> _window = new();
    private readonly Mock<ILogService> _log = new();

    public SystemMonitorWidgetHostTests()
    {
        // A pending read makes cancellation and late-publication behavior deterministic.
        _monitor.Setup(m => m.ReadAsync(It.IsAny<CancellationToken>())).Returns(async (CancellationToken ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("Cancelled reads cannot produce a sample.");
        });
    }

    private SystemMonitorWidgetHost Create(Func<WinOptimizer.UI.Features.Extensions.ViewModels.SystemMonitorReadingsViewModel,
        ISystemMonitorWindow>? factory = null) => new(_monitor.Object, Mock.Of<ILocalizationService>(),
        Mock.Of<IThemeService>(), new ExtensionTestDispatcher(), _log.Object, factory ?? (_ => _window.Object));

    [Fact]
    public async Task RunningStateFollowsSuccessfulWindowCreationAndShowReusesIt()
    {
        var creates = 0;
        using var host = Create(_ => { creates++; return _window.Object; });
        Assert.False(host.IsRunning);
        await host.ApplyOptionsAsync(new() { Enabled = true });
        Assert.True(host.IsRunning);
        await host.ShowAsync();
        Assert.Equal(1, creates);
        _window.Verify(w => w.Show(), Times.Exactly(2));
        _window.Verify(w => w.PlaceInitially(), Times.Exactly(2));
        await host.StopAsync();
        Assert.Equal(SystemMonitorState.Disabled, host.State);
        _monitor.Verify(m => m.StopAsync(), Times.Once);
    }

    [Fact]
    public async Task XamlLoadFailureReportsTheErrorAndRetryDoesNotPoisonLaterChanges()
    {
        var creates = 0;
        using var host = Create(_ => ++creates == 1
            ? throw new COMException("Cannot find MonitorSurface") : _window.Object);
        await host.ApplyOptionsAsync(new() { Enabled = true });
        Assert.False(host.IsRunning);
        Assert.Equal(SystemMonitorState.Failed, host.State);
        Assert.Equal("Cannot find MonitorSurface", host.LastError);
        _log.Verify(l => l.LogError(It.Is<string>(message => message.Contains("SysMonitor")), It.IsAny<Exception>()), Times.Once);
        await host.ShowAsync();
        Assert.True(host.IsRunning);
        Assert.Null(host.LastError);
        await host.ApplyOptionsAsync(new());
        Assert.Equal(SystemMonitorState.Disabled, host.State);
        await host.StopAsync();
    }

    [Fact]
    public async Task FailedShowReleasesThePartialWindowAndNeverClaimsToBeRunning()
    {
        _window.Setup(w => w.Show()).Throws(new InvalidOperationException("window unavailable"));
        using var host = Create();
        await host.ApplyOptionsAsync(new() { Enabled = true });
        Assert.False(host.IsRunning);
        _window.Verify(w => w.CloseFromHost(), Times.Once);
        _monitor.Verify(m => m.ReadAsync(It.IsAny<CancellationToken>()), Times.Never);
        await host.StopAsync();
    }

    [Fact]
    public async Task UnexpectedWindowClosureChangesRuntimeStateAndCanBeRecovered()
    {
        using var host = Create();
        await host.ApplyOptionsAsync(new() { Enabled = true });
        _window.Raise(w => w.ClosedBySystem += null, EventArgs.Empty);
        Assert.Equal(SystemMonitorState.Failed, host.State);
        Assert.False(host.IsRunning);
        await host.ShowAsync();
        Assert.True(host.IsRunning);
        await host.StopAsync();
    }

    [Fact]
    public async Task DisablingARealBackgroundHostWaitsForPersistenceAndEndsTheSession()
    {
        using var host = Create();
        var preferences = new Mock<IUserPreferencesService>();
        preferences.Setup(p => p.GetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>()))
            .ReturnsAsync(new SystemMonitorOptions { Enabled = true });
        var write = new TaskCompletionSource<OperationResult>();
        preferences.Setup(p => p.SetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>())).Returns(write.Task);
        using var vm = new ExtensionsViewModel(preferences.Object, Mock.Of<ILocalizationService>(), host, new ExtensionTestDispatcher(),
            Mock.Of<ISystemMonitorStartupService>(s => s.ApplyAsync(It.IsAny<SystemMonitorOptions>()) == Task.FromResult(OperationResult.Succeeded())));
        Assert.True(await vm.LoadAsync());
        var hidden = false;
        var shutdown = false;
        using var lifetime = new ExtensionApplicationLifetime(host, vm, () => hidden = true, () => hidden = false,
            () => shutdown = true, _log.Object);
        await lifetime.CloseAsync();
        Assert.True(hidden);
        Assert.True(host.IsRunning);
        vm.IsEnabled = false;
        Assert.False(shutdown);
        Assert.True(host.IsRunning);
        write.SetResult(OperationResult.Succeeded());
        await vm.FlushAsync();
        await lifetime.CloseAsync();
        Assert.True(shutdown);
        Assert.False(host.IsRunning);
        Assert.False(vm.Options.Enabled);
    }

    [Fact]
    public async Task WindowCreationWaitsForTheUiDispatcher()
    {
        var dispatcher = new Mock<IDispatcherService>();
        Func<Task>? pending = null;
        var completion = new TaskCompletionSource();
        dispatcher.Setup(d => d.RunOnUIThreadWithContextAsync(It.IsAny<Func<Task>>()))
            .Returns((Func<Task> action) => { pending = action; return completion.Task; });
        var creates = 0;
        using var host = new SystemMonitorWidgetHost(_monitor.Object, Mock.Of<ILocalizationService>(),
            Mock.Of<IThemeService>(), dispatcher.Object, _log.Object, _ => { creates++; return _window.Object; });
        var apply = host.ApplyOptionsAsync(new() { Enabled = true });
        Assert.Equal(0, creates);
        Assert.NotNull(pending);
        await pending();
        completion.SetResult();
        await apply;
        Assert.Equal(1, creates);
        Assert.True(host.IsRunning);
        // Dispose cancels the in-flight read; it does not require another dispatcher callback.
    }
}
