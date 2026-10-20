using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Extensions;
using WinOptimizer.UI.Features.Extensions;
using WinOptimizer.UI.Features.Extensions.ViewModels;
using WinOptimizer.UI.Tests.Helpers;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

public sealed class ExtensionsViewModelTests
{
    private readonly Mock<IUserPreferencesService> _preferences = new();
    private readonly Mock<ISystemMonitorWidgetHost> _host = new();
    private readonly Mock<ILocalizationService> _localization = new();
    private readonly Mock<ISystemMonitorStartupService> _startup = new();

    public ExtensionsViewModelTests()
    {
        _startup.Setup(s => s.ApplyAsync(It.IsAny<SystemMonitorOptions>())).ReturnsAsync(OperationResult.Succeeded());
        _host.Setup(h => h.ApplyOptionsAsync(It.IsAny<SystemMonitorOptions>())).Returns(Task.CompletedTask);
        _host.Setup(h => h.ShowAsync()).Returns(Task.CompletedTask);
        _preferences.Setup(p => p.GetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>()))
            .ReturnsAsync(new SystemMonitorOptions());
        _preferences.Setup(p => p.SetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>()))
            .ReturnsAsync(OperationResult.Succeeded());
        _localization.Setup(l => l.GetString(It.IsAny<string>())).Returns((string key) => key);
    }

    private ExtensionsViewModel Create() => new(_preferences.Object, _localization.Object, _host.Object, new ExtensionTestDispatcher(), _startup.Object);

    [Fact]
    public async Task ManualLaunchRestoresAnEnabledWidgetAfterAStaleAutomaticLaunch()
    {
        var options = new SystemMonitorOptions { Enabled = true };
        _preferences.Setup(p => p.GetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>())).ReturnsAsync(options);
        using var vm = Create();
        Assert.True(await vm.LoadAsync(automaticLaunch: true));
        _host.Verify(h => h.ApplyOptionsAsync(It.IsAny<SystemMonitorOptions>()), Times.Never);
        Assert.True(await vm.LoadAsync());
        Assert.True(await vm.LoadAsync());
        _host.Verify(h => h.ApplyOptionsAsync(options), Times.Once);
        _preferences.Verify(p => p.SetPreferenceAsync(It.IsAny<string>(), It.IsAny<SystemMonitorOptions>()), Times.Never);
    }

    [Fact]
    public async Task StartupChoiceRestoresAndDisablingWidgetRemovesRegistrationWithoutLosingChoice()
    {
        var saved = new SystemMonitorOptions { Enabled = true, StartWithWindows = true };
        _preferences.Setup(p => p.GetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>())).ReturnsAsync(saved);
        using var vm = Create();
        Assert.True(await vm.LoadAsync());
        Assert.True(vm.StartWithWindows);
        _startup.Verify(s => s.ApplyAsync(saved), Times.Once);
        _preferences.Verify(p => p.SetPreferenceAsync(It.IsAny<string>(), It.IsAny<SystemMonitorOptions>()), Times.Never);
        vm.IsEnabled = false;
        Assert.True(await vm.FlushAsync());
        Assert.True(vm.Options.StartWithWindows);
        Assert.False(SystemMonitorLaunch.ShouldStart(vm.Options));
        var disabled = saved with { Enabled = false };
        _startup.Verify(s => s.ApplyAsync(disabled), Times.Once);
        vm.IsEnabled = true;
        Assert.True(await vm.FlushAsync());
        _startup.Verify(s => s.ApplyAsync(saved), Times.Exactly(2));
        vm.StartWithWindows = false;
        Assert.True(await vm.FlushAsync());
        var manual = saved with { StartWithWindows = false };
        _startup.Verify(s => s.ApplyAsync(manual), Times.Once);
    }

    [Fact]
    public async Task StartupFailureDoesNotSaveOrPublishAndLaterEditRetries()
    {
        using var vm = Create();
        await vm.LoadAsync();
        vm.IsEnabled = true;
        await vm.FlushAsync();
        _startup.Setup(s => s.ApplyAsync(It.Is<SystemMonitorOptions>(o => o.StartWithWindows)))
            .ReturnsAsync(OperationResult.Failed("Task Scheduler unavailable"));
        vm.StartWithWindows = true;
        Assert.False(await vm.FlushAsync());
        Assert.False(vm.Options.StartWithWindows);
        Assert.Equal("Task Scheduler unavailable", vm.Status);
        _preferences.Verify(p => p.SetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.Is<SystemMonitorOptions>(o => o.StartWithWindows)), Times.Never);
        _startup.Setup(s => s.ApplyAsync(It.IsAny<SystemMonitorOptions>())).ReturnsAsync(OperationResult.Succeeded());
        vm.IsCompact = false;
        Assert.True(await vm.FlushAsync());
        Assert.True(vm.Options.StartWithWindows);
        Assert.Empty(vm.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedPersistenceRestoresPreviousStartupRegistration(bool throws)
    {
        using var vm = Create();
        await vm.LoadAsync();
        vm.IsEnabled = true;
        await vm.FlushAsync();
        if (throws)
            _preferences.Setup(p => p.SetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>())).ThrowsAsync(new IOException("disk unavailable"));
        else
            _preferences.Setup(p => p.SetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>())).ReturnsAsync(OperationResult.Failed("disk unavailable"));
        _startup.Invocations.Clear();
        vm.StartWithWindows = true;
        Assert.False(await vm.FlushAsync());
        Assert.False(vm.Options.StartWithWindows);
        Assert.Equal("disk unavailable", vm.Status);
        _startup.Verify(s => s.ApplyAsync(It.Is<SystemMonitorOptions>(o => o.StartWithWindows)), Times.Once);
        _startup.Verify(s => s.ApplyAsync(It.Is<SystemMonitorOptions>(o => !o.StartWithWindows)), Times.Once);
    }

    [Fact]
    public async Task StartupReconciliationErrorIsVisibleWithoutPreventingManualWidgetUse()
    {
        _startup.Setup(s => s.ApplyAsync(It.IsAny<SystemMonitorOptions>())).ReturnsAsync(OperationResult.Failed("access denied"));
        using var vm = Create();
        Assert.True(await vm.LoadAsync());
        Assert.True(vm.CanEdit);
        Assert.Equal("access denied", vm.Status);
        _host.Verify(h => h.ApplyOptionsAsync(It.IsAny<SystemMonitorOptions>()), Times.Once);
    }

    [Fact]
    public void SettingsDescriptionUsesTheCurrentLanguageWithoutLoadingPreferences()
    {
        _localization.Setup(service => service.GetString("SysMonitor_Description")).Returns("Monitor device activity");
        using var vm = Create();
        Assert.Equal("Monitor device activity", vm.MonitorDescription);
        var notified = false;
        vm.PropertyChanged += (_, _) => notified = true;
        _localization.Setup(service => service.GetString("SysMonitor_Description")).Returns("Monitora il dispositivo");
        _localization.Raise(service => service.LanguageChanged += null, EventArgs.Empty);
        Assert.Equal("Monitora il dispositivo", vm.MonitorDescription);
        Assert.True(notified);
        _preferences.Verify(service => service.GetPreferenceAsync(It.IsAny<string>(), It.IsAny<SystemMonitorOptions>()), Times.Never);
        _preferences.Verify(service => service.SetPreferenceAsync(It.IsAny<string>(), It.IsAny<SystemMonitorOptions>()), Times.Never);
    }

    [Fact]
    public async Task RuntimeStatusReflectsTheWindowRatherThanTheSavedSwitchAndShowCanRetry()
    {
        _preferences.Setup(p => p.GetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>()))
            .ReturnsAsync(new SystemMonitorOptions { Enabled = true });
        _host.SetupGet(h => h.State).Returns(SystemMonitorState.Failed);
        _host.SetupGet(h => h.LastError).Returns("XAML could not load");
        using var vm = Create();
        Assert.True(await vm.LoadAsync());
        Assert.True(vm.IsEnabled);
        Assert.True(vm.CanEdit);
        Assert.Equal("SysMonitor_Failed", vm.WidgetStateText);
        Assert.Equal("XAML could not load", vm.RuntimeError);
        Assert.True(vm.HasRuntimeError);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        await vm.ShowWidgetCommand.ExecuteAsync(null);
        _host.Verify(h => h.ShowAsync(), Times.Once);
        _preferences.Verify(p => p.SetPreferenceAsync(It.IsAny<string>(), It.IsAny<SystemMonitorOptions>()), Times.Never);
        _host.SetupGet(h => h.State).Returns(SystemMonitorState.Running);
        _host.SetupGet(h => h.LastError).Returns((string?)null);
        _host.Raise(h => h.StateChanged += null, EventArgs.Empty);
        Assert.Equal("SysMonitor_Running", vm.WidgetStateText);
        Assert.Empty(vm.RuntimeError);
        Assert.False(vm.HasRuntimeError);
        Assert.Contains(nameof(vm.HasRuntimeError), changed);
    }

    [Fact]
    public async Task ExplicitLoadRestoresSavedEnabledStateOnceWithoutWriting()
    {
        var saved = new SystemMonitorOptions { Enabled = true, IsCompact = false, AlwaysOnTop = false,
            Theme = SystemMonitorTheme.Dark, RefreshIntervalSeconds = 5 };
        _preferences.Setup(p => p.GetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>())).ReturnsAsync(saved);
        using var vm = Create();
        _host.Verify(h => h.ApplyOptionsAsync(It.IsAny<SystemMonitorOptions>()), Times.Never);
        Assert.True(await vm.LoadAsync());
        Assert.True(await vm.LoadAsync());
        Assert.True(vm.IsEnabled);
        Assert.False(vm.IsCompact);
        Assert.False(vm.AlwaysOnTop);
        Assert.Equal(2, vm.ThemeIndex);
        Assert.Equal(2, vm.RefreshIndex);
        _host.Verify(h => h.ApplyOptionsAsync(saved), Times.Once);
        _preferences.Verify(p => p.SetPreferenceAsync(It.IsAny<string>(), It.IsAny<SystemMonitorOptions>()), Times.Never);
    }

    [Fact]
    public async Task LiveOptionsApplyOnlyAfterSuccessfulSaveAndWidgetActionsUseSameQueue()
    {
        var saved = new TaskCompletionSource<OperationResult>();
        _preferences.Setup(p => p.SetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>())).Returns(saved.Task);
        using var vm = Create();
        await vm.LoadAsync();
        vm.IsEnabled = true;
        _host.Verify(h => h.ApplyOptionsAsync(It.Is<SystemMonitorOptions>(o => o.Enabled)), Times.Never);
        saved.SetResult(OperationResult.Succeeded());
        Assert.True(await vm.FlushAsync());
        _host.Verify(h => h.ApplyOptionsAsync(It.Is<SystemMonitorOptions>(o => o.Enabled)), Times.Once);
        _host.Raise(h => h.ToggleSizeRequested += null, EventArgs.Empty);
        _host.Raise(h => h.DisableRequested += null, EventArgs.Empty);
        Assert.True(await vm.FlushAsync());
        Assert.False(vm.Options.Enabled);
        Assert.False(vm.Options.IsCompact);
    }

    [Fact]
    public async Task ExpandedContentRestoresWithoutWritingAndEditsPersistCompleteSnapshots()
    {
        var saved = new SystemMonitorOptions { ShowTemperatures = false, ShowFans = false, ShowMemory = false,
            ShowCpu = false, ShowNetwork = false };
        _preferences.Setup(p => p.GetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>())).ReturnsAsync(saved);
        using var vm = Create();
        Assert.True(await vm.LoadAsync());
        Assert.False(vm.ShowTemperatures); Assert.False(vm.ShowFans); Assert.False(vm.ShowMemory);
        Assert.False(vm.ShowCpu); Assert.False(vm.ShowNetwork);
        _preferences.Verify(p => p.SetPreferenceAsync(It.IsAny<string>(), It.IsAny<SystemMonitorOptions>()), Times.Never);
        vm.ShowFans = true;
        vm.ShowNetwork = true;
        Assert.True(await vm.FlushAsync());
        Assert.Equal(saved with { ShowFans = true, ShowNetwork = true }, vm.Options);
        _host.Verify(h => h.ApplyOptionsAsync(vm.Options), Times.Once);
    }

    [Fact]
    public async Task AdditionalMetricChoicesRestoreAndPersistIndependently()
    {
        var saved = new SystemMonitorOptions { ShowGpuUsage = true, ShowGpuMemory = true, ShowClockSpeed = true,
            ShowPower = true, ShowVoltage = true, ShowStorageActivity = true, ShowStorageThroughput = true };
        _preferences.Setup(p => p.GetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>())).ReturnsAsync(saved);
        using var vm = Create();
        Assert.True(await vm.LoadAsync());
        Assert.True(vm.ShowGpuUsage); Assert.True(vm.ShowGpuMemory); Assert.True(vm.ShowClockSpeed);
        Assert.True(vm.ShowPower); Assert.True(vm.ShowVoltage); Assert.True(vm.ShowStorageActivity); Assert.True(vm.ShowStorageThroughput);
        _preferences.Verify(p => p.SetPreferenceAsync(It.IsAny<string>(), It.IsAny<SystemMonitorOptions>()), Times.Never);
        foreach (var edit in new Action<ExtensionsViewModel>[]
        {
            v => v.ShowGpuUsage = false, v => v.ShowGpuMemory = false, v => v.ShowClockSpeed = false,
            v => v.ShowPower = false, v => v.ShowVoltage = false, v => v.ShowStorageActivity = false, v => v.ShowStorageThroughput = false
        })
        {
            edit(vm);
            Assert.True(await vm.FlushAsync());
        }
        Assert.Equal(new SystemMonitorOptions(), vm.Options);
        _preferences.Verify(p => p.SetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>()), Times.Exactly(7));
    }

    [Fact]
    public async Task SaveFailureKeepsPublishedStateAndALaterEditCanRetry()
    {
        using var vm = Create();
        await vm.LoadAsync();
        _preferences.Setup(p => p.SetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>()))
            .ReturnsAsync(OperationResult.Failed("disk unavailable"));
        vm.IsEnabled = true;
        Assert.False(await vm.FlushAsync());
        Assert.False(vm.Options.Enabled);
        Assert.Equal("disk unavailable", vm.Status);
        _host.Verify(h => h.ApplyOptionsAsync(It.Is<SystemMonitorOptions>(o => o.Enabled)), Times.Never);
        _preferences.Setup(p => p.SetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>()))
            .ReturnsAsync(OperationResult.Succeeded());
        vm.AlwaysOnTop = false;
        Assert.True(await vm.FlushAsync());
        Assert.True(vm.Options.Enabled);
        Assert.False(vm.Options.AlwaysOnTop);
        Assert.Empty(vm.Status);
    }

    [Fact]
    public async Task ScaleRestoresWithoutWritingAndPersistsAcrossOtherWidgetEdits()
    {
        var saved = new SystemMonitorOptions { Enabled = true, ScalePercent = 75 };
        _preferences.Setup(p => p.GetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>())).ReturnsAsync(saved);
        using var vm = Create();
        Assert.True(await vm.LoadAsync());
        Assert.Equal(75, vm.ScalePercent);
        Assert.Equal("75%", vm.ScaleText);
        _preferences.Verify(p => p.SetPreferenceAsync(It.IsAny<string>(), It.IsAny<SystemMonitorOptions>()), Times.Never);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.ScalePercent = 150;
        Assert.True(await vm.FlushAsync());
        Assert.Equal("150%", vm.ScaleText);
        Assert.Contains(nameof(vm.ScaleText), changed);
        var enlarged = saved with { ScalePercent = 150 };
        _host.Verify(h => h.ApplyOptionsAsync(enlarged), Times.Once);
        _host.Raise(h => h.ToggleSizeRequested += null, EventArgs.Empty);
        vm.ThemeIndex = 1;
        Assert.True(await vm.FlushAsync());
        Assert.Equal(saved with { ScalePercent = 150, IsCompact = false, Theme = SystemMonitorTheme.Light }, vm.Options);
    }

    [Fact]
    public async Task ScaleAppliesOnlyAfterSuccessfulPersistenceAndFailedSavesRetainPublishedScale()
    {
        using var vm = Create();
        await vm.LoadAsync();
        var pending = new TaskCompletionSource<OperationResult>();
        _preferences.Setup(p => p.SetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>())).Returns(pending.Task);
        vm.ScalePercent = 125;
        _host.Verify(h => h.ApplyOptionsAsync(It.Is<SystemMonitorOptions>(o => o.ScalePercent == 125)), Times.Never);
        pending.SetResult(OperationResult.Succeeded());
        Assert.True(await vm.FlushAsync());
        _host.Verify(h => h.ApplyOptionsAsync(It.Is<SystemMonitorOptions>(o => o.ScalePercent == 125)), Times.Once);
        _preferences.Setup(p => p.SetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>()))
            .ReturnsAsync(OperationResult.Failed("disk unavailable"));
        vm.ScalePercent = 200;
        Assert.False(await vm.FlushAsync());
        Assert.Equal(125, vm.Options.ScalePercent);
        Assert.Equal("disk unavailable", vm.Status);
        _host.Verify(h => h.ApplyOptionsAsync(It.Is<SystemMonitorOptions>(o => o.ScalePercent == 200)), Times.Never);
    }

    [Fact]
    public async Task RapidEditsSaveInOrderAndApplyAllWidgetPreferences()
    {
        var first = new TaskCompletionSource<OperationResult>();
        var writes = new List<SystemMonitorOptions>();
        _preferences.Setup(p => p.SetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>()))
            .Returns((string key, SystemMonitorOptions options) =>
            { writes.Add(options); return writes.Count == 1 ? first.Task : Task.FromResult(OperationResult.Succeeded()); });
        using var vm = Create();
        await vm.LoadAsync();
        vm.IsEnabled = true;
        vm.IsCompact = false;
        vm.ThemeIndex = 1;
        vm.RefreshIndex = 0;
        vm.AlwaysOnTop = false;
        vm.ScalePercent = 150;
        vm.ScalePercent = 75;
        Assert.Single(writes);
        first.SetResult(OperationResult.Succeeded());
        Assert.True(await vm.FlushAsync());
        Assert.Equal(7, writes.Count);
        Assert.True(writes[0].IsCompact);
        Assert.Equal(new SystemMonitorOptions { Enabled = true, IsCompact = false, Theme = SystemMonitorTheme.Light,
            RefreshIntervalSeconds = 1, AlwaysOnTop = false, ScalePercent = 75 }, vm.Options);
        _host.Verify(h => h.ApplyOptionsAsync(vm.Options), Times.Once);
    }

    [Fact]
    public async Task InvalidSavedOptionsDoNotStartWidgetAndCanBeRetried()
    {
        _preferences.Setup(p => p.GetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>()))
            .ReturnsAsync(new SystemMonitorOptions { Enabled = true, RefreshIntervalSeconds = 0 });
        using var vm = Create();
        Assert.False(await vm.LoadAsync());
        Assert.False(vm.CanEdit);
        Assert.NotEmpty(vm.Status);
        _host.Verify(h => h.ApplyOptionsAsync(It.IsAny<SystemMonitorOptions>()), Times.Never);
        _preferences.Setup(p => p.GetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>()))
            .ReturnsAsync(new SystemMonitorOptions());
        Assert.True(await vm.LoadAsync());
    }

    [Fact]
    public void ReadingsBoundHistoryAndPreserveUnknownSensorsAndRates()
    {
        var vm = new SystemMonitorReadingsViewModel(_localization.Object);
        Assert.Equal("—", vm.CpuValue);
        Assert.Equal("—", vm.MemoryValue);
        Assert.True(vm.NoFans);
        var sample = new SystemMonitorSnapshot(DateTimeOffset.UnixEpoch, 25, 8UL << 30, 2UL << 30,
            2048, 1048576, [new("CPU", 45)], [], []);
        for (var i = 0; i < 65; i++) vm.Publish(sample);
        Assert.Equal(60, vm.CpuHistory.Count);
        Assert.Equal(75, vm.MemoryPercent);
        Assert.Contains("KB/s", vm.DownloadValue);
        Assert.Contains("MB/s", vm.UploadValue);
        Assert.Equal("45", Assert.Single(vm.Temperatures).Value);
        vm.IsCompact = false;
        Assert.True(vm.IsExpanded);
        vm.Publish(sample with { CpuUsagePercent = null, Temperatures = [], DownloadBytesPerSecond = null });
        Assert.Equal("—", vm.CpuValue);
        Assert.Equal("—", vm.DownloadValue);
        Assert.Empty(vm.Temperatures);
    }
}
