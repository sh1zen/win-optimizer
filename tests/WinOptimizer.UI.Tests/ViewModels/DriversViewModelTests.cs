using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Drivers.Interfaces;
using WinOptimizer.Core.Features.Drivers.Models;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Drivers.ViewModels;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

public sealed class DriversViewModelTests : IDisposable
{
    private readonly Mock<IUserPreferencesService> _preferences = new();
    private readonly Mock<ILocalizationService> _localization = new();
    private readonly Mock<IDriverScanService> _scanner = new();
    private readonly Mock<ITaskProgressService> _tasks = new();
    private readonly Mock<IDispatcherService> _dispatcher = new();
    private readonly Mock<IDriverRemovalService> _removal = new();
    private readonly Mock<IDialogService> _dialogs = new();
    private readonly Mock<IApplicationModeService> _mode = new();
    private readonly DriverSettingsViewModel _settings;
    private readonly DriversViewModel _vm;
    private CancellationTokenSource? _owner;
    public DriversViewModelTests()
    {
        _localization.Setup(x => x.GetString(It.IsAny<string>())).Returns((string key) => key);
        _preferences.Setup(x => x.GetPreferenceAsync("Drivers", It.IsAny<DriverScanOptions>())).ReturnsAsync(new DriverScanOptions());
        _preferences.Setup(x => x.SetPreferenceAsync("Drivers", It.IsAny<DriverScanOptions>())).ReturnsAsync(OperationResult.Succeeded());
        _tasks.Setup(x => x.StartTask(It.IsAny<string>(), true)).Returns(() => _owner = new CancellationTokenSource());
        _tasks.SetupGet(x => x.CurrentTaskCancellationSource).Returns(() => _owner);
        _dispatcher.Setup(x => x.RunOnUIThread(It.IsAny<Action>())).Callback<Action>(action => action());
        _settings = new(_preferences.Object, _localization.Object);
        _vm = new(_scanner.Object, _settings, _localization.Object, _tasks.Object, _dispatcher.Object, _removal.Object, _dialogs.Object, _mode.Object);
        _scanner.Setup(x => x.ScanAsync(It.IsAny<DriverScanOptions>(), It.IsAny<IProgress<DriverScanProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Report(new()));
    }
    private static DriverScanReport Report(DriverScanOptions options, bool online = true) => new(new([], [], true, []),
        [new("oem1.inf", "original.inf", "OEM", "Net", "1.0", false, false) { Date = "2026-01-02", SizeBytes = 1024 }],
        [new("device", "Disconnected device", "Net", "oem2.inf", false, "1.0", [])],
        [new(new("update", "Official update", "OEM", "Net", "PCI\\TEST", "2026-01-03", "2.0", DriverCertification.Whql), [])],
        online, 0, online ? [] : ["offline"], options);

    [Fact]
    public void Construction_HasWhqlEnabled_AndDoesNotLoadOrScan()
    {
        Assert.True(_settings.OnlyWhqlUpdates);
        Assert.True(_settings.Options.OnlyWhqlUpdates);
        Assert.Empty(_vm.ResultGroups);
        Assert.False(_vm.HasReport);
        Assert.True(_vm.ShowEmptyState);
        Assert.True(_vm.ShowScanInformation);
        Assert.False(_vm.HasStatus);
        Assert.False(_vm.HasNotices);
        Assert.Equal("Drivers_ReadyTitle", _vm.EmptyTitle);
        _preferences.VerifyNoOtherCalls(); _scanner.VerifyNoOtherCalls();
    }
    [Fact]
    public void ScanCategoryLabels_UseTheExistingLocalizedDriverCategories()
    {
        Assert.Equal("Drivers_Unreferenced", _vm.UnusedPackagesLabel);
        Assert.Equal("Drivers_Disconnected", _vm.DisconnectedDevicesLabel);
        Assert.Equal("Drivers_Updates", _vm.OfficialUpdatesLabel);
        _scanner.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SavedFilter_IsRestoredByNextSettingsInstance()
    {
        DriverScanOptions saved = new();
        _preferences.Setup(x => x.SetPreferenceAsync("Drivers", It.IsAny<DriverScanOptions>()))
            .Callback<string, DriverScanOptions>((_, value) => saved = value).ReturnsAsync(OperationResult.Succeeded());
        await _settings.LoadAsync();
        _settings.OnlyWhqlUpdates = false;
        await _settings.FlushAsync();
        Assert.False(_settings.Options.OnlyWhqlUpdates);
        _preferences.Setup(x => x.GetPreferenceAsync("Drivers", It.IsAny<DriverScanOptions>())).ReturnsAsync(saved);
        using var restarted = new DriverSettingsViewModel(_preferences.Object, _localization.Object);
        Assert.True(await restarted.LoadAsync());
        Assert.False(restarted.OnlyWhqlUpdates);
    }
    [Fact]
    public async Task FailedSave_PreservesPublishedPolicy_AndExposesFailure()
    {
        await _settings.LoadAsync();
        var old = _settings.Options;
        _preferences.Setup(x => x.SetPreferenceAsync("Drivers", It.IsAny<DriverScanOptions>())).ReturnsAsync(OperationResult.Failed("disk full"));
        _settings.OnlyWhqlUpdates = false;
        await _settings.FlushAsync();
        Assert.Same(old, _settings.Options);
        Assert.Equal("disk full", _settings.Status);
    }

    [Fact]
    public async Task ScanWaitsForAutomaticWrite_AndFailedWriteBlocksScanUntilReset()
    {
        await _settings.LoadAsync();
        _preferences.Verify(x => x.SetPreferenceAsync("Drivers", It.IsAny<DriverScanOptions>()), Times.Never);
        var write = new TaskCompletionSource<OperationResult>();
        _preferences.Setup(x => x.SetPreferenceAsync("Drivers", It.IsAny<DriverScanOptions>())).Returns(write.Task);
        _settings.OnlyWhqlUpdates = false;
        var scan = _vm.ScanCommand.ExecuteAsync(null);
        Assert.False(scan.IsCompleted);
        _scanner.Verify(x => x.ScanAsync(It.IsAny<DriverScanOptions>(), It.IsAny<IProgress<DriverScanProgress>?>(), It.IsAny<CancellationToken>()), Times.Never);
        write.SetResult(OperationResult.Failed("write failed"));
        await scan;
        Assert.Equal("write failed", _vm.Status);
        _scanner.Verify(x => x.ScanAsync(It.IsAny<DriverScanOptions>(), It.IsAny<IProgress<DriverScanProgress>?>(), It.IsAny<CancellationToken>()), Times.Never);
        _preferences.Setup(x => x.SetPreferenceAsync("Drivers", It.IsAny<DriverScanOptions>())).ReturnsAsync(OperationResult.Succeeded());
        Assert.True(await _settings.ResetAsync());
        await _vm.ScanCommand.ExecuteAsync(null);
        _scanner.Verify(x => x.ScanAsync(It.Is<DriverScanOptions>(options => options.OnlyWhqlUpdates), It.IsAny<IProgress<DriverScanProgress>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }
    [Fact]
    public async Task PreferenceLoadFailure_BlocksScan_AndCanBeRetried()
    {
        _preferences.SetupSequence(x => x.GetPreferenceAsync("Drivers", It.IsAny<DriverScanOptions>()))
            .ThrowsAsync(new IOException("unreadable preferences")).ReturnsAsync(new DriverScanOptions());
        await _vm.ScanCommand.ExecuteAsync(null);
        Assert.Equal("unreadable preferences", _vm.Status);
        Assert.True(_vm.HasStatus);
        Assert.True(_vm.ShowEmptyState);
        Assert.False(_vm.HasReport);
        Assert.True(_vm.ShowScanInformation);
        _scanner.Verify(x => x.ScanAsync(It.IsAny<DriverScanOptions>(), It.IsAny<IProgress<DriverScanProgress>?>(), It.IsAny<CancellationToken>()), Times.Never);
        await _vm.ScanCommand.ExecuteAsync(null);
        Assert.Equal(3, _vm.ResultGroups.Count);
    }
    [Fact]
    public async Task Scan_PublishesSelectableCategories_AndReadOnlyUpdates()
    {
        await _vm.ScanCommand.ExecuteAsync(null);
        Assert.Equal(3, _vm.ResultGroups.Count);
        Assert.Same(_vm.ResultGroups[0], _vm.SelectedGroup);
        var group = _vm.ResultGroups[0];
        Assert.True(group.ShowSelection);
        Assert.True(group.CanClean);
        var row = Assert.IsType<DriverResultItem>(Assert.Single(group.Items));
        Assert.Equal("original.inf", row.Name);
        Assert.DoesNotContain("OEM", row.Detail);
        Assert.Contains("oem1.inf", row.Details);
        Assert.Contains("KB", row.Size);
        Assert.NotEqual("Drivers_Unknown", row.Date);
        var disconnected = Assert.IsType<DriverResultItem>(_vm.ResultGroups[1].Items[0]);
        Assert.Equal("Drivers_Unknown", disconnected.Date);
        Assert.Equal("Drivers_Unknown", disconnected.Size);
        Assert.False(_vm.ResultGroups[2].ShowSelection);
        Assert.False(_vm.ResultGroups[2].CanClean);
        Assert.False(_vm.IsBusy);
        Assert.True(_vm.HasReport);
        Assert.False(_vm.ShowEmptyState);
        Assert.False(_vm.ShowScanInformation);
        Assert.Empty(_vm.Status);
        Assert.False(_vm.HasStatus);
        Assert.False(_vm.HasNotices);
        _tasks.Verify(x => x.CompleteTask(), Times.Once);
    }

    [Fact]
    public async Task Presentation_TracksThePendingScan_AndNotifiesWhenAReportArrives()
    {
        var pending = new TaskCompletionSource<DriverScanReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        _scanner.Setup(x => x.ScanAsync(It.IsAny<DriverScanOptions>(), It.IsAny<IProgress<DriverScanProgress>?>(), It.IsAny<CancellationToken>()))
            .Returns(pending.Task);
        var changed = new List<string?>();
        _vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        var operation = _vm.ScanCommand.ExecuteAsync(null);
        Assert.True(_vm.IsBusy);
        Assert.False(_vm.ShowScanInformation);
        Assert.False(_vm.ShowEmptyState);
        Assert.False(_vm.HasReport);
        Assert.Contains(nameof(_vm.ShowEmptyState), changed);
        Assert.Contains(nameof(_vm.HasStatus), changed);
        Assert.Contains(nameof(_vm.ShowScanInformation), changed);

        pending.SetResult(Report(new()));
        await operation;
        Assert.True(_vm.HasReport);
        Assert.False(_vm.ShowEmptyState);
        Assert.False(_vm.ShowScanInformation);
        Assert.False(_vm.HasStatus);
        Assert.Contains(nameof(_vm.HasReport), changed);
        Assert.Contains(nameof(_vm.ShowEmptyState), changed);
    }
    [Fact]
    public async Task OnlineFailure_IsShownAsIncomplete_AndKeepsLocalEvidence()
    {
        _scanner.Setup(x => x.ScanAsync(It.IsAny<DriverScanOptions>(), It.IsAny<IProgress<DriverScanProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Report(new(), false));
        await _vm.ScanCommand.ExecuteAsync(null);
        _vm.SelectedGroup = _vm.ResultGroups[2];
        Assert.Equal("Drivers_UpdatesUnavailable", _vm.CategoryEmptyDescription);
        Assert.Equal("offline", _vm.Notices);
        Assert.True(_vm.HasNotices);
        Assert.Single(_vm.ResultGroups[0].Items);
    }
    [Fact]
    public async Task SavedFilterChange_RequestsRescan_AndKeepsPreviousReportSnapshot()
    {
        await _vm.ScanCommand.ExecuteAsync(null);
        var group = _vm.ResultGroups[0];
        var changed = new List<string?>();
        _vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        _settings.OnlyWhqlUpdates = false;
        await _settings.FlushAsync();
        Assert.Equal("Drivers_Rescan", _vm.RescanHint);
        Assert.True(_vm.HasRescanHint);
        Assert.Contains(nameof(_vm.HasRescanHint), changed);
        Assert.Same(group, _vm.ResultGroups[0]);
        Assert.Equal("Drivers_FilterAll", _vm.FilterSummary);
    }
    [Fact]
    public async Task NavigationAway_CancelsScan_AndIgnoresLateProgressAndResults()
    {
        var gate = new TaskCompletionSource<DriverScanReport>();
        IProgress<DriverScanProgress>? reporter = null;
        CancellationToken token = default;
        _scanner.Setup(x => x.ScanAsync(It.IsAny<DriverScanOptions>(), It.IsAny<IProgress<DriverScanProgress>?>(), It.IsAny<CancellationToken>()))
            .Returns((DriverScanOptions _, IProgress<DriverScanProgress>? progress, CancellationToken ct) => { reporter = progress; token = ct; return gate.Task; });
        var operation = _vm.ScanCommand.ExecuteAsync(null);
        _vm.Dispose();
        Assert.True(token.IsCancellationRequested);
        reporter!.Report(new("late progress"));
        gate.SetResult(Report(new()));
        await operation;
        Assert.Empty(_vm.ResultGroups);
        Assert.DoesNotContain("late progress", _vm.Status);
        _tasks.Verify(x => x.CompleteTask(), Times.Once);
    }
    [Fact]
    public async Task ExistingGlobalTask_BlocksScan()
    {
        _tasks.SetupGet(x => x.IsTaskRunning).Returns(true);
        await _vm.ScanCommand.ExecuteAsync(null);
        Assert.Equal("Cleaner_Wait", _vm.Status);
        _scanner.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RowClick_ShowsFullDetails_WithoutSelecting()
    {
        await _vm.ScanCommand.ExecuteAsync(null);
        var row = Assert.IsType<DriverResultItem>(_vm.ResultGroups[0].Items[0]);
        await row.ShowDetailsCommand.ExecuteAsync(null);
        Assert.False(row.IsSelected);
        Assert.Equal(0, _vm.SelectedCount);
        _dialogs.Verify(dialogs => dialogs.ShowInformationAsync(It.Is<string>(text => text.Contains("oem1.inf") && text.Contains("OEM") && text.Contains("1.0")), "original.inf", "OK"), Times.Once);
    }

    [Fact]
    public async Task MultipleSelection_PersistsAcrossCategories_AndDoesNotSelectUpdates()
    {
        await _vm.ScanCommand.ExecuteAsync(null);
        _vm.ResultGroups[0].IsSelected = true;
        _vm.SelectedGroup = _vm.ResultGroups[1];
        _vm.ResultGroups[1].IsSelected = true;
        _vm.ResultGroups[2].IsSelected = true;
        Assert.Equal(2, _vm.SelectedCount);
        Assert.True(_vm.UninstallCommand.CanExecute(null));
        Assert.False(_vm.ResultGroups[2].Items[0].IsSelected);
        _vm.SelectedGroup = _vm.ResultGroups[0];
        Assert.True(Assert.Single(_vm.SelectedGroup.Items).IsSelected);
        Assert.Equal(2, _vm.SelectedCount);
    }

    [Fact]
    public async Task Scan_UsesFooterProgress_WhileRetainingLifecycleAndCancellation()
    {
        var tasks = new WinOptimizer.Infrastructure.Features.Common.Services.TaskProgressService(Mock.Of<ILogService>());
        using var shell = new WinOptimizer.UI.ViewModels.TaskProgressViewModel(tasks, _dispatcher.Object, _dialogs.Object, _localization.Object, Mock.Of<ILogService>());
        using var page = new DriversViewModel(_scanner.Object, _settings, _localization.Object, tasks, _dispatcher.Object, _removal.Object, _dialogs.Object, _mode.Object);
        var completion = new TaskCompletionSource<DriverScanReport>();
        IProgress<DriverScanProgress>? reporter = null;
        _scanner.Setup(scanner => scanner.ScanAsync(It.IsAny<DriverScanOptions>(), It.IsAny<IProgress<DriverScanProgress>?>(), It.IsAny<CancellationToken>()))
            .Returns((DriverScanOptions _, IProgress<DriverScanProgress>? progress, CancellationToken _) => { reporter = progress; return completion.Task; });
        var scan = page.ScanCommand.ExecuteAsync(null);
        Assert.True(tasks.IsTaskRunning);
        Assert.True(shell.IsTaskRunning);
        Assert.True(shell.IsLoading);
        Assert.True(shell.IsIndeterminate);
        reporter!.Report(new("Drivers_VerifyingPackages", 2, 8));
        Assert.Equal(25, shell.ProgressValue);
        Assert.False(shell.IsIndeterminate);
        Assert.Equal("Drivers_VerifyingPackages (2/8)", shell.LastTerminalLine);
        Assert.False(page.HasStatus);
        Assert.False(shell.IsTaskFailed);
        Assert.True(shell.IsLoading);
        shell.CancelCommand.Execute(null);
        Assert.True(tasks.CurrentTaskCancellationSource!.IsCancellationRequested);
        completion.SetResult(Report(new()));
        await scan;
        Assert.False(tasks.IsTaskRunning);
        Assert.False(shell.IsLoading);
        Assert.False(page.IsBusy);
        Assert.False(page.HasReport);
    }

    [Fact]
    public async Task ScanFailure_RemainsVisibleInTheFooter_AndOnThePage()
    {
        var tasks = new WinOptimizer.Infrastructure.Features.Common.Services.TaskProgressService(Mock.Of<ILogService>());
        using var footer = new WinOptimizer.UI.ViewModels.TaskProgressViewModel(tasks, _dispatcher.Object, _dialogs.Object, _localization.Object, Mock.Of<ILogService>());
        using var page = new DriversViewModel(_scanner.Object, _settings, _localization.Object, tasks, _dispatcher.Object, _removal.Object, _dialogs.Object, _mode.Object);
        _scanner.Setup(scanner => scanner.ScanAsync(It.IsAny<DriverScanOptions>(), It.IsAny<IProgress<DriverScanProgress>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DriverScanException("Inventory unavailable"));
        await page.ScanCommand.ExecuteAsync(null);
        Assert.False(tasks.IsTaskRunning);
        Assert.True(footer.IsTaskFailed);
        Assert.True(footer.IsLoading);
        Assert.Contains("Inventory unavailable", tasks.GetTerminalOutputLines());
        Assert.Equal("Inventory unavailable", page.Status);
        Assert.True(page.HasStatus);
    }

    [Fact]
    public async Task EmptyUpdateCategory_DistinguishesUnavailableSearchFromSuccessfulEmptyResults()
    {
        _scanner.Setup(scanner => scanner.ScanAsync(It.IsAny<DriverScanOptions>(), It.IsAny<IProgress<DriverScanProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Report(new(), false) with { Updates = [] });
        await _vm.ScanCommand.ExecuteAsync(null);
        _vm.SelectedGroup = _vm.ResultGroups[2];
        Assert.True(_vm.HasEmptyCategory);
        Assert.Equal("Drivers_UpdatesUnavailableTitle", _vm.CategoryEmptyTitle);
        Assert.Equal("Drivers_UpdatesUnavailable", _vm.CategoryEmptyDescription);
        _scanner.Setup(scanner => scanner.ScanAsync(It.IsAny<DriverScanOptions>(), It.IsAny<IProgress<DriverScanProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Report(new()) with { Updates = [] });
        await _vm.ScanCommand.ExecuteAsync(null);
        _vm.SelectedGroup = _vm.ResultGroups[2];
        Assert.True(_vm.HasEmptyCategory);
        Assert.Equal("Drivers_CategoryEmpty", _vm.CategoryEmptyTitle);
    }

    [Fact]
    public async Task TestMode_DisablesUninstall_AndKeepsScanAndSelectionAvailable()
    {
        await _vm.ScanCommand.ExecuteAsync(null);
        _vm.ResultGroups[0].IsSelected = true;
        _mode.SetupGet(mode => mode.CurrentMode).Returns(WinOptimizerMode.Builder);
        _mode.Raise(mode => mode.ModeChanged += null, EventArgs.Empty);
        Assert.False(_vm.UninstallCommand.CanExecute(null));
        Assert.Equal("Drivers_TestMode", _vm.TestModeHint);
        await _vm.UninstallCommand.ExecuteAsync(null);
        _removal.VerifyNoOtherCalls(); _dialogs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DecliningConfirmation_PreservesSelection_AndDoesNotRemove()
    {
        await _vm.ScanCommand.ExecuteAsync(null);
        _vm.ResultGroups[0].IsSelected = true;
        _dialogs.Setup(dialogs => dialogs.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse());
        await _vm.UninstallCommand.ExecuteAsync(null);
        Assert.Equal(1, _vm.SelectedCount);
        Assert.False(_vm.IsBusy);
        _removal.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ConfirmedBatch_RemovesOnlySuccessfulRows_AndRetainsFailuresAndCategory()
    {
        await _vm.ScanCommand.ExecuteAsync(null);
        _vm.ResultGroups[0].IsSelected = true; _vm.ResultGroups[1].IsSelected = true;
        _vm.SelectedGroup = _vm.ResultGroups[1];
        _dialogs.Setup(dialogs => dialogs.ShowConfirmationAsync(It.Is<ConfirmationRequest>(request => request.Items!.Count == 2)))
            .ReturnsAsync(new ConfirmationResponse { Confirmed = true });
        _removal.Setup(removal => removal.RemoveAsync(It.IsAny<DriverScanReport>(), It.Is<IReadOnlyList<DriverRemovalTarget>>(targets => targets.Count == 2),
                It.IsAny<IProgress<DriverScanProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DriverRemovalReport([
                new(new(DriverRemovalKind.Package, "oem1.inf"), true, true),
                new(new(DriverRemovalKind.Device, "device"), false, false, "access denied")], true));
        await _vm.UninstallCommand.ExecuteAsync(null);
        Assert.Empty(_vm.ResultGroups[0].Items);
        Assert.True(Assert.Single(_vm.ResultGroups[1].Items).IsSelected);
        Assert.Same(_vm.ResultGroups[1], _vm.SelectedGroup);
        Assert.Equal(1, _vm.SelectedCount);
        Assert.Contains("access denied", _vm.Notices);
        Assert.Contains("Drivers_RestartRequired", _vm.Status);
        Assert.Contains("Drivers_RemovalCancelled", _vm.Status);
        Assert.False(_vm.IsBusy);
        _tasks.Verify(tasks => tasks.CompleteTask(), Times.Exactly(2));
    }

    [Fact]
    public async Task NavigationDuringConfirmation_PreventsRemoval()
    {
        await _vm.ScanCommand.ExecuteAsync(null);
        _vm.ResultGroups[0].IsSelected = true;
        var response = new TaskCompletionSource<ConfirmationResponse>();
        _dialogs.Setup(dialogs => dialogs.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).Returns(response.Task);
        var operation = _vm.UninstallCommand.ExecuteAsync(null);
        _vm.Dispose();
        response.SetResult(new ConfirmationResponse { Confirmed = true });
        await operation;
        _removal.VerifyNoOtherCalls();
    }
    public void Dispose() { _vm.Dispose(); _settings.Dispose(); _owner?.Dispose(); }
}
