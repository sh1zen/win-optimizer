using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Drivers.Interfaces;
using WinOptimizer.Core.Features.Drivers.Models;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Drivers;
using WinOptimizer.UI.Features.Drivers.ViewModels;
using WinOptimizer.UI.Helpers;
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
    private readonly Mock<IDriverPackageService> _packages = new();
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
        _vm = new(_scanner.Object, _settings, _localization.Object, _tasks.Object, _dispatcher.Object, _removal.Object, _dialogs.Object, _mode.Object,
            _packages.Object);
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
        Assert.Equal("Scan_ReadyTitle", _vm.EmptyTitle);
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
    public async Task ChangingSourcesChannelsAndWhqlKeepsAllFieldsAcrossPendingWritesAndReload()
    {
        var original = new DriverScanOptions
        {
            Sources = DriverSources.WindowsUpdate | DriverSources.Nvidia | DriverSources.Asus,
            NvidiaChannels = NvidiaChannels.Studio, AutomaticVerificationLimit = 6
        };
        _preferences.Setup(x => x.GetPreferenceAsync("Drivers", It.IsAny<DriverScanOptions>())).ReturnsAsync(original);
        var pending = new TaskCompletionSource<OperationResult>();
        DriverScanOptions written = original;
        _preferences.Setup(x => x.SetPreferenceAsync("Drivers", It.IsAny<DriverScanOptions>()))
            .Callback<string, DriverScanOptions>((_, value) => written = value).Returns(pending.Task);
        await _settings.LoadAsync();
        Assert.False(_settings.GameReady);
        _settings.OnlyWhqlUpdates = false;
        _settings.GameReady = true;
        Assert.True(_settings.Options.OnlyWhqlUpdates);
        _settings.SourceChoices.Single(item => item.Source == DriverSources.Asus).Enabled = false;
        pending.SetResult(OperationResult.Succeeded());
        Assert.True(await _settings.FlushAsync());
        Assert.Equal(original with { OnlyWhqlUpdates = false, NvidiaChannels = NvidiaChannels.Both,
            Sources = DriverSources.WindowsUpdate | DriverSources.Nvidia }, written);
        _preferences.Setup(x => x.GetPreferenceAsync("Drivers", It.IsAny<DriverScanOptions>())).ReturnsAsync(written);
        using var reopened = new DriverSettingsViewModel(_preferences.Object, _localization.Object);
        Assert.True(await reopened.LoadAsync());
        Assert.Equal(written, reopened.Options);
        Assert.False(reopened.SourceChoices.Single(item => item.Source == DriverSources.Asus).Enabled);
    }

    [Fact]
    public async Task DownloadSelectionCannotSelectRemovalAndUsesDedicatedPackageOperation()
    {
        var report = Report(new() { OnlyWhqlUpdates = false });
        var match = Assert.Single(report.Updates);
        var payload = new Uri("https://download.windowsupdate.com/driver.cab");
        var update = match.Update with { Catalog = new([new(DriverSources.MicrosoftCatalog, "update", "1",
            DateTimeOffset.UnixEpoch, null, null, [payload])], [new(payload, [])]) };
        _scanner.Setup(scanner => scanner.ScanAsync(It.IsAny<DriverScanOptions>(), It.IsAny<IProgress<DriverScanProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(report with { Updates = [match with { Update = update }] });
        _packages.Setup(service => service.AcquireAsync(update, report.Inventory, true, It.IsAny<IProgress<DriverScanProgress>?>(),
            It.IsAny<CancellationToken>(), false)).ReturnsAsync(new DriverPackageResult(update, @"C:\Drivers\fixture"));
        await _vm.ScanCommand.ExecuteAsync(null);
        var removal = Assert.IsType<DriverResultItem>(Assert.Single(_vm.ResultGroups[0].Items));
        var download = Assert.IsType<DriverResultItem>(Assert.Single(_vm.ResultGroups[2].Items));
        removal.IsDownloadSelected = true;
        download.IsSelected = true;
        Assert.False(removal.IsDownloadSelected);
        Assert.False(download.IsSelected);
        removal.IsSelected = true;
        download.IsDownloadSelected = true;
        Assert.True(_vm.DownloadCommand.CanExecute(null));
        Assert.Equal(1, _vm.SelectedCount);
        await _vm.DownloadCommand.ExecuteAsync(null);
        Assert.Equal(1, _vm.SelectedCount);
        Assert.False(_vm.DownloadCommand.CanExecute(null));
        Assert.Contains(@"C:\Drivers\fixture", _vm.Status);
        _packages.VerifyAll();
        _removal.VerifyNoOtherCalls();
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
    public async Task WhqlFilteredEmptyUpdates_ExplainTheActiveFilter_WithoutChangingOtherCategories()
    {
        _scanner.Setup(x => x.ScanAsync(It.IsAny<DriverScanOptions>(), It.IsAny<IProgress<DriverScanProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Report(new()) with { Updates = [], ExcludedUpdates = 2 });
        await _vm.ScanCommand.ExecuteAsync(null);
        _vm.SelectedGroup = _vm.ResultGroups[2];
        Assert.True(_vm.HasEmptyCategory);
        Assert.Equal("Drivers_FilterWhql", _vm.CategoryEmptyDescription);
        _settings.OnlyWhqlUpdates = false;
        await _settings.FlushAsync();
        Assert.True(_vm.HasRescanHint);
        Assert.Equal("Drivers_FilterWhql", _vm.CategoryEmptyDescription);
        _vm.SelectedGroup = _vm.ResultGroups[1];
        Assert.Equal("Drivers_CategoryEmptyDescription", _vm.CategoryEmptyDescription);
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
    public async Task ResultsPageShowsEvidenceAndLimitsDeviceBulkSelectionToSearchMatches()
    {
        var strings = await UseEnglishAsync();
        var report = Report(new());
        var device = new DriverDevice("USB\\BLUETOOTH", "Bluetooth adapter", "Bluetooth", "oem2.inf", false, "1.0", []);
        var other = new DriverDevice("HID\\MOUSE", "USB mouse", "Mouse", "oem3.inf", false, "1.0", []);
        var publisher = new Uri("https://www.catalog.update.microsoft.com/");
        var payload = new Uri("https://download.windowsupdate.com/driver.cab");
        var catalog = new DriverCatalogRecord(
            [new(DriverSources.MicrosoftCatalog, "update", "1", DateTimeOffset.UnixEpoch, publisher, null, [payload])],
            [new(payload, [])]) { Verification = DriverVerification.Verified };
        var component = new DriverComponent("driver.inf", "Bluetooth", "OEM", "2.0", "2026-01-03",
            [], [], "", "", "", true, DriverCertification.Whql);
        var identifier = new DriverIdentifier("USB\\BLUETOOTH", DriverIdentifierKind.Hardware, 0);
        var match = report.Updates[0] with
        {
            Update = report.Updates[0].Update with { Catalog = catalog },
            Devices = [device],
            Comparisons = [new(device, component, identifier, identifier, DriverVersionRelation.Newer, false)]
        };
        const string coverageNotice = "Catalog query limit reached.";
        _scanner.Setup(scanner => scanner.ScanAsync(It.IsAny<DriverScanOptions>(), It.IsAny<IProgress<DriverScanProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(report with
            {
                DisconnectedDevices = [device, other],
                Updates = [match],
                Notices = [coverageNotice],
                Sources = [new(DriverSources.MicrosoftCatalog, DriverSourceState.Partial, 1, [coverageNotice], publisher)]
            });
        var page = new DriversPage(_vm) { RequestedTheme = ElementTheme.Dark, Width = 1120 };
        var surface = new Grid { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 24, 25, 28)) };
        surface.Children.Add(page);
        var window = new Window { Content = surface };
        try
        {
            window.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(1120, 720));
            window.Activate();
            await StartupUiCoordinator.RunAfterFirstRenderAsync(page, () => Task.CompletedTask);
            await _vm.ScanCommand.ExecuteAsync(null);
            var coverage = Assert.IsType<Border>(page.FindName("CoverageBanner"));
            Assert.Equal(Visibility.Visible, coverage.Visibility);
            Assert.Contains(VisibleLabels(coverage), label => label.Text == coverageNotice);
            var details = Assert.IsType<Border>(page.FindName("CoverageDetailsPanel"));
            Assert.Equal(Visibility.Collapsed, details.Visibility);
            var toggle = Assert.IsType<ToggleButton>(page.FindName("CoverageToggle"));
            toggle.IsChecked = true;
            Assert.Equal(Visibility.Visible, details.Visibility);
            await StartupUiCoordinator.RunAfterFirstRenderAsync(page, () => Task.CompletedTask);
            page.UpdateLayout();
            Assert.Contains(VisibleLabels(details), label => label.Text.Contains(coverageNotice, StringComparison.Ordinal));
            toggle.IsChecked = false;

            var results = Assert.IsType<DriverResultsView>(Assert.IsType<Border>(page.FindName("ResultsPanel")).Child);
            await StartupUiCoordinator.RunAfterFirstRenderAsync(page, () => Task.CompletedTask);
            var tabs = Assert.IsType<ListView>(results.FindName("CategoryTabs"));
            var columns = Assert.IsType<Grid>(results.FindName("CandidateColumns"));
            var rows = Assert.IsType<ListView>(results.FindName("DriverRows"));
            Assert.Equal(Visibility.Visible, tabs.Visibility);
            tabs.SelectedItem = _vm.ResultGroups[2];
            await StartupUiCoordinator.RunAfterFirstRenderAsync(page, () => Task.CompletedTask);
            page.UpdateLayout();
            Assert.Equal(Visibility.Visible, columns.Visibility);
            Assert.Contains(VisibleLabels(rows), label => label.Text == device.Name);
            Assert.Contains(VisibleLabels(rows), label => label.Text == "1.0");
            Assert.Contains(VisibleLabels(rows), label => label.Text == "2.0");
            Assert.Contains(VisibleLabels(rows), label => label.Text == strings["Drivers_Version_Newer"]);
            Assert.Contains(VisibleLabels(rows), label => label.Text == strings["Drivers_WhqlVerified"]);
            var filter = Assert.IsType<Button>(results.FindName("FilterSettingsButton"));
            var flyout = Assert.IsType<Flyout>(filter.Flyout);
            Assert.Same(_settings, Assert.IsType<DriverSettingsControl>(Assert.IsType<ScrollViewer>(flyout.Content).Content).ViewModel);
            var selected = Assert.IsType<ListViewItem>(tabs.ContainerFromItem(tabs.SelectedItem));
            var tabRoot = Assert.IsType<Grid>(VisualTreeHelper.GetChild(selected, 0));
            Assert.Equal(1, Assert.IsType<Border>(tabRoot.Children[1]).Opacity);
            var check = Assert.IsType<CheckBox>(results.FindName("VisibleSelectionCheck"));
            check.IsChecked = true;
            Assert.True(_vm.CanDownload);

            tabs.SelectedItem = _vm.ResultGroups[1];
            await StartupUiCoordinator.RunAfterFirstRenderAsync(page, () => Task.CompletedTask);
            var packageSize = Assert.IsType<TextBlock>(results.FindName("InventorySizeColumn"));
            Assert.Equal(Visibility.Visible, packageSize.Visibility);
            Assert.Equal(strings["Drivers_PackageSize"], packageSize.Text);
            var search = Assert.IsType<TextBox>(results.FindName("DriverDeviceSearch"));
            search.Text = "bluetooth";
            Assert.Equal(device.Name, Assert.IsType<DriverResultItem>(Assert.Single(rows.Items)).Name);
            check.IsChecked = true;
            Assert.Equal(1, _vm.SelectedCount);
            Assert.True(_vm.CanDownload);
            search.Text = "";
            Assert.Equal(2, rows.Items.Count);
            Assert.Null(check.IsChecked);
            Assert.False(Assert.IsType<DriverResultItem>(rows.Items[1]).IsSelected);

            page.Width = 520;
            window.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(520, 720));
            await StartupUiCoordinator.RunAfterFirstRenderAsync(page, () => Task.CompletedTask);
            page.UpdateLayout();
            var picker = Assert.IsType<ComboBox>(results.FindName("CategoryPicker"));
            Assert.Equal(Visibility.Visible, picker.Visibility);
            picker.SelectedItem = _vm.ResultGroups[2];
            Assert.Equal(Visibility.Collapsed, columns.Visibility);
            Assert.Single(rows.Items);
            Assert.True(_vm.CanDownload);
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task ScanPageShowsLiveMilestonesAndActivityAndReturnsToResults()
    {
        var strings = await UseEnglishAsync();
        var completion = new TaskCompletionSource<DriverScanReport>();
        IProgress<DriverScanProgress>? reporter = null;
        _scanner.Setup(scanner => scanner.ScanAsync(It.IsAny<DriverScanOptions>(), It.IsAny<IProgress<DriverScanProgress>?>(), It.IsAny<CancellationToken>()))
            .Returns((DriverScanOptions _, IProgress<DriverScanProgress>? progress, CancellationToken _) =>
            {
                reporter = progress;
                return completion.Task;
            });
        var page = new DriversPage(_vm) { RequestedTheme = ElementTheme.Dark, Width = 1000, HorizontalAlignment = HorizontalAlignment.Left };
        var surface = new Grid { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 24, 25, 28)) };
        surface.Children.Add(page);
        var window = new Window { Content = surface };
        Task scan = Task.CompletedTask;
        try
        {
            window.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(1000, 580));
            window.Activate();
            await StartupUiCoordinator.RunAfterFirstRenderAsync(page, () => Task.CompletedTask);
            var viewport = Assert.IsType<ScrollViewer>(page.FindName("ScanProgressViewport"));
            var title = Assert.IsType<TextBlock>(page.FindName("ScanPhaseTitle"));
            var activity = Assert.IsType<ItemsControl>(page.FindName("ScanActivityList"));
            var card = Assert.IsType<Border>(page.FindName("ScanProgressCard"));
            var results = Assert.IsType<Border>(page.FindName("ResultsPanel"));

            Assert.Equal(Visibility.Collapsed, viewport.Visibility);
            scan = Assert.IsAssignableFrom<CommunityToolkit.Mvvm.Input.IAsyncRelayCommand>(
                Assert.IsType<Button>(page.FindName("ScanButton")).Command).ExecuteAsync(null);
            Assert.Equal(Visibility.Visible, viewport.Visibility);
            Assert.Equal(strings["Drivers_DetectingDevices"], title.Text);
            Assert.Single(activity.Items);

            reporter!.Report(new("Drivers_SearchingUpdates") { Phase = DriverScanPhase.CheckSources });
            Assert.Equal(strings["Drivers_CheckingSourcesTitle"], title.Text);
            Assert.Collection(activity.Items.Cast<DriverScanActivity>(),
                item => Assert.True(item.IsComplete), item => Assert.True(item.IsComplete), item => Assert.True(item.IsActive));
            Assert.True(_vm.SourcesMilestone.IsCurrent);
            Assert.True(Assert.IsType<Button>(page.FindName("CancelScanButton")).IsEnabled);
            Assert.True(Assert.IsType<Button>(page.FindName("ScanDetailsButton")).IsEnabled);

            foreach (var width in new[] { 1000, 520 })
            {
                page.Width = width;
                window.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(width, 580));
                await StartupUiCoordinator.RunAfterFirstRenderAsync(page, () => Task.CompletedTask);
                page.UpdateLayout();
                Assert.InRange(card.ActualWidth, 300, Math.Min(page.ActualWidth, 846));
                Assert.True(card.ActualHeight > 300);
                var milestone = Assert.Single(VisibleLabels(card), label => label.Text == strings["Drivers_CheckSourcesStep"]);
                Assert.Equal(Microsoft.UI.Text.FontWeights.SemiBold.Weight, milestone.FontWeight.Weight);
                Assert.Contains(VisibleLabels(card), label => label.Text == strings["Drivers_DriverVersionsLoaded"]);
                var active = Assert.Single(VisibleLabels(card), label => label.Text == strings["Drivers_SearchingUpdates"]);
                Assert.Equal(Windows.UI.Color.FromArgb(255, 255, 179, 107), Assert.IsType<SolidColorBrush>(active.Foreground).Color);
                var left = card.TransformToVisual(page).TransformPoint(default).X;
                Assert.InRange(Math.Abs(left - (page.ActualWidth - card.ActualWidth) / 2), 0, 1);
            }

            reporter.Report(new("Drivers_PreparingResults") { Phase = DriverScanPhase.PrepareResults });
            Assert.Equal(strings["Drivers_PreparingResults"], title.Text);
            Assert.True(_vm.ResultsMilestone.IsCurrent);
            Assert.Equal(Visibility.Collapsed, results.Visibility);
            completion.SetResult(Report(new()));
            await scan;
            Assert.Equal(Visibility.Collapsed, viewport.Visibility);
            Assert.Equal(Visibility.Visible, results.Visibility);
        }
        finally
        {
            completion.TrySetCanceled();
            await scan;
            window.Close();
        }
    }

    private async Task<Dictionary<string, string>> UseEnglishAsync()
    {
        var strings = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Localization", "en.json")))!;
        _localization.Setup(service => service.GetString(It.IsAny<string>())).Returns((string key) => strings[key]);
        return strings;
    }

    /// <summary>Reads the text rendered by the page and its active row templates.</summary>
    private static IEnumerable<TextBlock> VisibleLabels(DependencyObject element)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            if (child is TextBlock { Visibility: Visibility.Visible } label) yield return label;
            if (child is UIElement { Visibility: Visibility.Collapsed }) continue;
            foreach (var descendant in VisibleLabels(child)) yield return descendant;
        }
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
        reporter!.Report(new("Drivers_SearchingUpdates") { Phase = DriverScanPhase.CheckSources });
        reporter.Report(new("Drivers_VerifyingPackages", 2, 8));
        Assert.Equal(25, shell.ProgressValue);
        Assert.False(shell.IsIndeterminate);
        Assert.Equal("Drivers_VerifyingPackages (2/8)", shell.LastTerminalLine);
        Assert.False(page.HasStatus);
        Assert.False(shell.IsTaskFailed);
        Assert.True(shell.IsLoading);
        await page.ShowScanDetailsCommand.ExecuteAsync(null);
        _dialogs.Verify(dialogs => dialogs.ShowTaskOutputDialogAsync("Dialog_TerminalOutput_Title",
            It.Is<IReadOnlyList<string>>(lines => lines.SequenceEqual(new[] { "Drivers_SearchingUpdates", "Drivers_VerifyingPackages (2/8)" }))), Times.Once);
        page.CancelScanCommand.Execute(null);
        Assert.True(tasks.CurrentTaskCancellationSource!.IsCancellationRequested);
        Assert.True(page.IsScanCancelling);
        Assert.False(page.CancelScanCommand.CanExecute(null));
        completion.SetResult(Report(new()));
        await scan;
        Assert.False(tasks.IsTaskRunning);
        Assert.False(shell.IsLoading);
        Assert.False(page.IsBusy);
        Assert.False(page.HasReport);
        Assert.False(page.IsScanning);
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
    public async Task ConfirmedBatch_KeepsRestartPendingAndFailedRowsOutOfCompletedCount()
    {
        _localization.Setup(loc => loc.GetString("Drivers_RemovalComplete")).Returns("Removed {0}, failed {1}");
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
        var pending = Assert.IsType<DriverResultItem>(Assert.Single(_vm.ResultGroups[0].Items));
        Assert.Contains("Drivers_RemovalPending", pending.Name);
        Assert.False(pending.CanClean);
        Assert.False(pending.IsSelected);
        Assert.True(Assert.Single(_vm.ResultGroups[1].Items).IsSelected);
        Assert.Same(_vm.ResultGroups[1], _vm.SelectedGroup);
        Assert.Equal(1, _vm.SelectedCount);
        Assert.Contains("access denied", _vm.Notices);
        Assert.Contains("Removed 0, failed 1", _vm.Status);
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
