using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Cleaner.Interfaces;
using WinOptimizer.Core.Features.Cleaner.Models;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.Cleaner.ViewModels;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Models;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

public sealed class CleanerViewModelTests : IDisposable
{
    private readonly Mock<IUserPreferencesService> _preferences = new();
    private readonly Mock<ICleanerBackend> _backend = new();
    private readonly Mock<ICleanerService> _cleaner = new();
    private readonly Mock<ICleanerBackupService> _backups = new();
    private readonly Mock<ILocalizationService> _localization = new();
    private readonly Mock<IDialogService> _dialogs = new();
    private readonly Mock<ITaskProgressService> _tasks = new();
    private readonly Mock<IDispatcherService> _dispatcher = new();
    private readonly Mock<IApplicationModeService> _mode = new();
    private readonly CleanerSettingsViewModel _settings;
    private readonly CleanerViewModel _vm;
    public CleanerViewModelTests()
    {
        _localization.Setup(x => x.GetString(It.IsAny<string>())).Returns((string key) => key);
        _preferences.Setup(x => x.GetPreferenceAsync("Cleaner", It.IsAny<CleanerOptions>())).ReturnsAsync(new CleanerOptions());
        _preferences.Setup(x => x.SetPreferenceAsync("Cleaner", It.IsAny<CleanerOptions>())).ReturnsAsync(OperationResult.Succeeded());
        _preferences.Setup(x => x.GetPreferenceAsync("Cleaner.SelectedAreas", It.IsAny<CleanerArea[]>()))
            .Returns((string _, CleanerArea[] defaults) => Task.FromResult(defaults));
        _preferences.Setup(x => x.SetPreferenceAsync("Cleaner.SelectedAreas", It.IsAny<CleanerArea[]>())).ReturnsAsync(OperationResult.Succeeded());
        _backend.Setup(x => x.GetApplicationsAsync(It.IsAny<CleanerOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CleanerApplicationInventory([new("Installed.App_publisher", "Installed app")], []));
        _backend.Setup(x => x.GetRegistryAccountsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { new CleanerRegistryAccount("S-1-5-21-1-2-3-1001", "Current account", true), new CleanerRegistryAccount("S-1-5-21-1-2-3-1002", "Other account", false) });
        _backend.Setup(x => x.GetDrivesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { "C:\\" });
        _tasks.Setup(x => x.StartTask(It.IsAny<string>(), It.IsAny<bool>())).Returns(() => new CancellationTokenSource());
        _cleaner.Setup(x => x.ScanAsync(It.IsAny<CleanerOptions>(), It.IsAny<IReadOnlyCollection<CleanerArea>>(), It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CleanerScanResult([], []));
        _settings = new(_preferences.Object, _backend.Object, _localization.Object, _tasks.Object, Mock.Of<IExplorerWindowManager>(), _backups.Object, _dialogs.Object, _mode.Object);
        _dispatcher.Setup(x => x.RunOnUIThread(It.IsAny<Action>())).Callback<Action>(action => action());
        _vm = CreateViewModel();
    }
    private CleanerViewModel CreateViewModel(ITaskProgressService? tasks = null) => new(_cleaner.Object, _settings, _localization.Object,
        _dialogs.Object, tasks ?? _tasks.Object, Mock.Of<ILogService>(), _mode.Object, _preferences.Object);
    private static CleanerFinding Finding(CleanerArea area, bool actionable = true) => new(Guid.NewGuid(), area, $"Cleaner_{area}", "scan evidence", 50, actionable, new CleanerDiskTarget("C:\\"));

    [Fact]
    public void EventsCleanupLabelsRefreshWithLanguage()
    {
        Assert.Equal("EventsCleanup_Title", _vm.EventsCleanupDisplayName);
        Assert.Equal("EventsCleanup_TitleDescription", _vm.EventsCleanupDescription);
        _localization.Setup(service => service.GetString(It.IsAny<string>())).Returns((string key) => "it:" + key);
        var notifications = new List<string?>();
        _vm.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        _localization.Raise(service => service.LanguageChanged += null, EventArgs.Empty);
        Assert.Equal("it:EventsCleanup_Title", _vm.EventsCleanupDisplayName);
        Assert.Equal("it:EventsCleanup_TitleDescription", _vm.EventsCleanupDescription);
        Assert.Contains(string.Empty, notifications);
    }

    [Fact]
    public async Task TrialMode_AllowsScanButBlocksCleanupAndRecovery()
    {
        _mode.SetupGet(x => x.CurrentMode).Returns(WinOptimizer.Core.Features.Common.Enums.WinOptimizerMode.Builder);
        Results(Finding(CleanerArea.Privacy));
        await _vm.ScanCommand.ExecuteAsync(null);
        _vm.Results.Should().ContainSingle();
        _vm.ApplyCommand.CanExecute(null).Should().BeFalse();
        await _vm.ApplyCommand.ExecuteAsync(null);
        await _settings.RestoreBackupCommand.ExecuteAsync(null);
        _settings.Status.Should().Be("TrialMode_Blocked");
        _cleaner.Verify(x => x.ApplyAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
        _backups.Verify(x => x.RestoreAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
    private void Results(params CleanerFinding[] items) => _cleaner.Setup(x => x.ScanAsync(It.IsAny<CleanerOptions>(), It.IsAny<IReadOnlyCollection<CleanerArea>>(), It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(new CleanerScanResult(items, []));

    /// <summary>Disk reports remain readable when native output starts with blank lines or is empty.</summary>
    [Theory]
    [InlineData("\r\n\r\nVolume analysis\r\n\r\nOptimization recommended.\r\n", "D:\\\r\nVolume analysis\r\n\r\nOptimization recommended.")]
    [InlineData("\nVolume analysis\nOptimization recommended.\n", "D:\\\r\nVolume analysis\nOptimization recommended.")]
    [InlineData("", "D:\\")]
    public async Task DiskScan_ShowsDriveAndCompleteReport_WithoutChangingCleanupEvidence(string report, string expected)
    {
        var finding = new CleanerFinding(Guid.NewGuid(), CleanerArea.Disk, "Cleaner_Disk", report,
            0, true, new CleanerDiskTarget("D:\\"));
        Results(finding);

        await _vm.ScanCommand.ExecuteAsync(null);

        var item = Assert.Single(_vm.Results);
        Assert.Equal(expected, item.Detail);
        Assert.Equal(0, item.DetailMaxLines);
        Assert.NotNull(item.Report);
        Assert.Equal("D:\\", item.Report.Title);
        Assert.Same(finding, item.Finding);
        Assert.Same(item, Assert.Single(Assert.Single(_vm.ResultGroups).Items));
        Assert.True(item.IsSelected);
        Assert.True(_vm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public void DiskReport_PreservesLocalizedSectionsValuesAndWrappedNotes()
    {
        var report = "\r\nAnalisi completata.\r\n\r\nInformazioni sul volume:\r\n\tDimensioni volume\t = 749,99 GB\r\n\tDimensioni cluster = 4 KB\r\n\r\nFrammentazione:\r\n\tSpazio frammentato totale = 52%\r\n\r\nNota: i frammenti di file\r\nsuperiori a 64 MB non vengono inclusi.\r\n";
        var finding = new CleanerFinding(Guid.NewGuid(), CleanerArea.Disk, "Cleaner_Disk", report,
            0, true, new CleanerDiskTarget("C:\\"));
        var item = new CleanerResultItem(finding, _localization.Object);
        var formatted = Assert.IsType<ResultReport>(item.Report);
        Assert.Equal("C:\\", formatted.Title);
        Assert.Equal(3, formatted.Sections.Count);
        Assert.False(formatted.Sections[0].HasHeading);
        Assert.Equal(new ResultReportRow("Analisi completata."), Assert.Single(formatted.Sections[0].Rows));
        Assert.Equal("Informazioni sul volume", formatted.Sections[1].Heading);
        Assert.Equal(new[] { new ResultReportRow("Dimensioni volume", "749,99 GB"), new ResultReportRow("Dimensioni cluster", "4 KB") }, formatted.Sections[1].Rows);
        Assert.Equal("Frammentazione", formatted.Sections[2].Heading);
        Assert.Equal(new ResultReportRow("Spazio frammentato totale", "52%"), formatted.Sections[2].Rows[0]);
        var note = formatted.Sections[2].Rows[1];
        Assert.Equal("Nota: i frammenti di file superiori a 64 MB non vengono inclusi.", note.Label);
        Assert.Null(note.Value); Assert.Equal(2, note.LabelColumnSpan);
        Assert.Same(finding, item.Finding); Assert.Equal(report, item.Finding.Detail);
    }

    [Fact]
    public void DiskReport_PreservesUnknownOutputAndDiagnostics_WhileOtherFindingsStayCompact()
    {
        var finding = new CleanerFinding(Guid.NewGuid(), CleanerArea.Disk, "Cleaner_Disk",
            "Volume information:\nFlags = A = B\n\nUnrecognized native message.\n", 0, false, new CleanerDiskTarget("D:\\"))
            { UnavailableReason = "Volume unavailable" };
        var item = new CleanerResultItem(finding, _localization.Object);
        var section = Assert.Single(Assert.IsType<ResultReport>(item.Report).Sections);
        Assert.Equal(new ResultReportRow("Flags", "A = B"), section.Rows[0]);
        Assert.Equal(1, section.Rows[0].LabelColumnSpan);
        Assert.Equal(new ResultReportRow("Unrecognized native message."), section.Rows[1]);
        Assert.Equal(new ResultReportRow("Volume unavailable"), section.Rows[2]);
        Assert.False(item.IsSelected);
        Assert.Null(new CleanerResultItem(Finding(CleanerArea.JunkFiles), _localization.Object).Report);
    }

    [Fact]
    public void Construction_DoesNotLoadPreferencesOrWindowsData_AndHasExactlySixCategories()
    {
        _vm.Areas.Select(x => x.Value).Should().Equal(Enum.GetValues<CleanerArea>());
        _preferences.Verify(x => x.GetPreferenceAsync("Cleaner", It.IsAny<CleanerOptions>()), Times.Never);
        _preferences.Verify(x => x.GetPreferenceAsync("Cleaner.SelectedAreas", It.IsAny<CleanerArea[]>()), Times.Never);
        _backend.Verify(x => x.GetApplicationsAsync(It.IsAny<CleanerOptions>(), It.IsAny<CancellationToken>()), Times.Never);
        _settings.RegistryCategories.Should().HaveCount(18);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initialization_RestoresEmptyOrOptionalAreasOnce_WithoutSaving(bool optionalAreas)
    {
        CleanerArea[] selected = optionalAreas ? [CleanerArea.RegistryDefrag, CleanerArea.Disk] : [];
        _preferences.Setup(x => x.GetPreferenceAsync("Cleaner.SelectedAreas", It.IsAny<CleanerArea[]>())).ReturnsAsync(selected);
        await _vm.InitializeAsync();
        await _vm.InitializeAsync();
        _vm.Areas.Where(x => x.IsSelected).Select(x => x.Value).Should().Equal(selected);
        _vm.ScanCommand.CanExecute(null).Should().Be(optionalAreas);
        _preferences.Verify(x => x.GetPreferenceAsync("Cleaner.SelectedAreas", It.IsAny<CleanerArea[]>()), Times.Once);
        _preferences.Verify(x => x.SetPreferenceAsync("Cleaner.SelectedAreas", It.IsAny<CleanerArea[]>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Selection_IsSavedImmediately_AndUsedByANewViewModelOnTheNextStart(bool empty)
    {
        CleanerArea[] expected = empty ? [] : [CleanerArea.Privacy];
        CleanerArea[]? stored = null;
        _preferences.Setup(x => x.GetPreferenceAsync("Cleaner.SelectedAreas", It.IsAny<CleanerArea[]>()))
            .Returns((string _, CleanerArea[] defaults) => Task.FromResult(stored ?? defaults));
        _preferences.Setup(x => x.SetPreferenceAsync("Cleaner.SelectedAreas", It.IsAny<CleanerArea[]>()))
            .Callback<string, CleanerArea[]>((_, selected) => stored = selected)
            .ReturnsAsync(OperationResult.Succeeded());
        await _vm.InitializeAsync();
        foreach (var area in _vm.Areas) area.IsSelected = !empty && area.Value == CleanerArea.Privacy;
        await _vm.FlushAreaSelectionAsync();
        stored.Should().Equal(expected);

        using var restarted = CreateViewModel();
        await restarted.InitializeAsync();
        restarted.Areas.Where(x => x.IsSelected).Select(x => x.Value).Should().Equal(expected);
        restarted.ScanCommand.CanExecute(null).Should().Be(!empty);
        await restarted.ScanCommand.ExecuteAsync(null);
        _cleaner.Verify(x => x.ScanAsync(It.IsAny<CleanerOptions>(),
            It.Is<IReadOnlyCollection<CleanerArea>>(areas => areas.SequenceEqual(expected)),
            It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>()), Times.Exactly(empty ? 0 : 1));
    }

    [Fact]
    public async Task RapidEdits_AreSavedInOrder_AndShutdownFlushWaitsForTheLastWrite()
    {
        await _vm.InitializeAsync();
        var first = new TaskCompletionSource<OperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<OperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<CleanerArea[]> writes = [];
        _preferences.Setup(x => x.SetPreferenceAsync("Cleaner.SelectedAreas", It.IsAny<CleanerArea[]>()))
            .Returns((string _, CleanerArea[] selected) =>
            {
                writes.Add(selected);
                if (writes.Count == 1) return first.Task;
                secondStarted.SetResult();
                return second.Task;
            });
        _vm.Areas[0].IsSelected = false;
        _vm.Areas[1].IsSelected = false;
        writes.Should().ContainSingle();
        var flush = _vm.FlushAreaSelectionAsync();
        flush.IsCompleted.Should().BeFalse();
        first.SetResult(OperationResult.Succeeded());
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        writes.Should().HaveCount(2);
        writes[0].Should().Contain(CleanerArea.JunkFiles);
        writes[1].Should().NotContain(CleanerArea.Privacy).And.NotContain(CleanerArea.JunkFiles);
        flush.IsCompleted.Should().BeFalse();
        second.SetResult(OperationResult.Succeeded());
        await flush;
    }

    [Fact]
    public async Task Initialization_DoesNotOverwriteEditsMadeWhilePreferencesAreLoading()
    {
        var loaded = new TaskCompletionSource<CleanerArea[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _preferences.Setup(x => x.GetPreferenceAsync("Cleaner.SelectedAreas", It.IsAny<CleanerArea[]>())).Returns(loaded.Task);
        var initialization = _vm.InitializeAsync();
        var secondInitialization = _vm.InitializeAsync();
        _vm.CanChooseAreas.Should().BeFalse();
        _vm.ScanCommand.CanExecute(null).Should().BeFalse();
        _vm.Areas[0].IsSelected = false;
        loaded.SetResult([CleanerArea.Privacy]);
        await Task.WhenAll(initialization, secondInitialization);
        _vm.CanChooseAreas.Should().BeTrue();
        _vm.Areas[0].IsSelected.Should().BeFalse();
        _vm.Areas.Where(x => x.IsSelected).Should().HaveCount(3);
        _preferences.Verify(x => x.GetPreferenceAsync("Cleaner.SelectedAreas", It.IsAny<CleanerArea[]>()), Times.Once);
    }

    [Fact]
    public async Task SaveFailure_IsVisibleWithoutDiscardingTheSelection_AndALaterEditRecovers()
    {
        await _vm.InitializeAsync();
        _localization.Setup(x => x.GetString("Cleaner_SelectionSaveFailed")).Returns("Cannot save: {0}");
        _preferences.Setup(x => x.SetPreferenceAsync("Cleaner.SelectedAreas", It.IsAny<CleanerArea[]>())).ReturnsAsync(OperationResult.Failed("access denied"));
        _vm.Areas[0].IsSelected = false;
        await _vm.FlushAreaSelectionAsync();
        _vm.Areas[0].IsSelected.Should().BeFalse();
        _vm.AreaSelectionError.Should().Be("Cannot save: access denied");
        _vm.HasAreaSelectionError.Should().BeTrue();
        _preferences.Setup(x => x.SetPreferenceAsync("Cleaner.SelectedAreas", It.IsAny<CleanerArea[]>())).ReturnsAsync(OperationResult.Succeeded());
        _vm.Areas[0].IsSelected = true;
        await _vm.FlushAreaSelectionAsync();
        _vm.HasAreaSelectionError.Should().BeFalse();
    }

    [Fact]
    public async Task InvalidSavedSelection_IsReported_AndRequiresAnExplicitNewChoice()
    {
        _preferences.Setup(x => x.GetPreferenceAsync("Cleaner.SelectedAreas", It.IsAny<CleanerArea[]>())).ReturnsAsync([(CleanerArea)999]);
        await _vm.InitializeAsync();
        _vm.HasAreaSelectionError.Should().BeTrue();
        _vm.ScanCommand.CanExecute(null).Should().BeFalse();
        await _vm.ScanCommand.ExecuteAsync(null);
        _cleaner.Verify(x => x.ScanAsync(It.IsAny<CleanerOptions>(), It.IsAny<IReadOnlyCollection<CleanerArea>>(), It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
        _vm.Areas[0].IsSelected = false;
        await _vm.FlushAreaSelectionAsync();
        _vm.HasAreaSelectionError.Should().BeFalse();
        _vm.ScanCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task DashboardCounters_TrackSelectionAndResetForANewScan()
    {
        Results(Finding(CleanerArea.Privacy) with { Bytes = 1048576 },
            Finding(CleanerArea.Registry) with { Bytes = 0 }, Finding(CleanerArea.Registry, false));
        await _vm.ScanCommand.ExecuteAsync(null);
        _vm.ResultCount.Should().Be(3);
        _vm.HasResults.Should().BeTrue();
        _vm.SelectedCount.Should().Be(1);
        _vm.SelectedSize.Should().Be($"{1:N1} MB");
        _vm.SelectAllCommand.Execute(null);
        _vm.SelectedCount.Should().Be(2);
        _vm.ClearSelectionCommand.Execute(null);
        _vm.SelectedCount.Should().Be(0);
        _vm.SelectedSize.Should().Be("0 B");
        Results(Finding(CleanerArea.JunkFiles) with { Bytes = 2048 });
        await _vm.ScanCommand.ExecuteAsync(null);
        _vm.ResultCount.Should().Be(1);
        _vm.SelectedCount.Should().Be(1);
        _vm.SelectedSize.Should().Be($"{2:N1} KB");
    }

    [Fact]
    public async Task Scan_GroupsStableCategoryKeysInScanOrder_AndStartsCollapsed()
    {
        var mui = Finding(CleanerArea.Registry) with { LabelKey = "Cleaner_Registry_MuiCache", Bytes = 0 };
        var junk = Finding(CleanerArea.JunkFiles) with { Bytes = 1024 };
        var fonts = Finding(CleanerArea.Registry) with { LabelKey = "Cleaner_Registry_Fonts", Bytes = 0 };
        // Equal translated names must not merge distinct categories.
        _localization.Setup(x => x.GetString(It.IsAny<string>())).Returns("Category");
        Results(mui, junk, fonts, mui with { Id = Guid.NewGuid() }, junk with { Id = Guid.NewGuid() });
        await _vm.ScanCommand.ExecuteAsync(null);

        _vm.ResultGroups.Select(x => x.Items.Count).Should().Equal(2, 2, 1);
        _vm.ResultGroups.Select(x => x.Items[0]).Should().Equal(_vm.Results[0], _vm.Results[1], _vm.Results[2]);
        _vm.ResultGroups.SelectMany(x => x.Items).Should().OnlyContain(x => _vm.Results.Any(result => ReferenceEquals(result, x)));
        _vm.ResultGroups[0].Items[1].Should().BeSameAs(_vm.Results[3]);
        _vm.ResultGroups.Should().OnlyContain(x => !x.IsExpanded);
        _vm.ResultGroups[0].Header.Should().Be($"Category ({2:N0})");
        _vm.ResultGroups[0].Size.Should().BeEmpty();
        _vm.ResultGroups[1].Size.Should().Be($"{2:N1} KB");
    }

    /// <summary>Detected privacy totals include blocked files while selection and space include actionable files only.</summary>
    [Fact]
    public async Task BlockedPrivacyFiles_ShowTheirReason_AndStayOutOfSelectionAndCleanup()
    {
        var available = Finding(CleanerArea.Privacy);
        var blocked = Finding(CleanerArea.Privacy, false) with { Bytes = 1024, UnavailableReason = "Close chrome and scan again." };
        Results(available, blocked);
        await _vm.ScanCommand.ExecuteAsync(null);
        _vm.ResultCount.Should().Be(2);
        _vm.SelectedCount.Should().Be(1);
        _vm.SelectedSize.Should().Be("50 B");
        var item = _vm.Results[1];
        item.Detail.Should().Be($"{blocked.Detail}{Environment.NewLine}{blocked.UnavailableReason}");
        item.DetailMaxLines.Should().Be(2);
        _vm.Results[0].DetailMaxLines.Should().Be(1);
        _vm.SelectAllCommand.Execute(null);
        item.IsSelected.Should().BeFalse();
        _vm.SelectedCount.Should().Be(1);
        _vm.ResultGroups.Single().IsSelected = true;
        item.IsSelected.Should().BeFalse();
        _dialogs.Setup(dialog => dialog.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse { Confirmed = true });
        _cleaner.Setup(cleaner => cleaner.ApplyAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CleanerApplyResult(1, []));
        await _vm.ApplyCommand.ExecuteAsync(null);
        _cleaner.Verify(cleaner => cleaner.ApplyAsync(It.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(available.Id)),
            It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GroupCheckbox_TracksIndividualAndGlobalSelection_AndExcludesDiagnostics()
    {
        Results(Finding(CleanerArea.Privacy), Finding(CleanerArea.Privacy), Finding(CleanerArea.Privacy, false),
            Finding(CleanerArea.Registry), Finding(CleanerArea.Disk, false));
        await _vm.ScanCommand.ExecuteAsync(null);
        var privacy = _vm.ResultGroups[0];
        var registry = _vm.ResultGroups[1];
        var diagnostic = _vm.ResultGroups[2];
        privacy.IsSelected.Should().BeTrue();
        registry.IsSelected.Should().BeFalse();
        diagnostic.CanClean.Should().BeFalse();
        privacy.Items[0].IsSelected = false;
        privacy.IsSelected.Should().BeNull();
        _vm.SelectedCount.Should().Be(1);
        privacy.IsSelected = true;
        privacy.IsSelected.Should().BeTrue();
        _vm.SelectedCount.Should().Be(2);
        privacy.Items[2].IsSelected.Should().BeFalse();
        privacy.IsSelected = false;
        _vm.SelectedCount.Should().Be(0);
        _vm.SelectedSize.Should().Be("0 B");
        _vm.SelectAllCommand.Execute(null);
        privacy.IsSelected.Should().BeTrue();
        registry.IsSelected.Should().BeTrue();
        diagnostic.IsSelected.Should().BeFalse();
        _vm.SelectedCount.Should().Be(3);
        _vm.ClearSelectionCommand.Execute(null);
        _vm.ResultGroups.Should().OnlyContain(x => x.IsSelected == false);
        _vm.SelectedCount.Should().Be(0);
        diagnostic.IsSelected = true;
        diagnostic.IsSelected.Should().BeFalse();
    }

    [Fact]
    public async Task GroupExpansionAndSelection_SurviveReinitializationAndTranslation_AndResetOnRescan()
    {
        Results(Finding(CleanerArea.Privacy), Finding(CleanerArea.Privacy));
        await _vm.ScanCommand.ExecuteAsync(null);
        var group = _vm.ResultGroups.Single();
        group.IsExpanded = true;
        group.Items[0].IsSelected = false;
        group.IsExpanded = false;
        _vm.SelectedCount.Should().Be(1);
        group.IsExpanded = true;
        await _vm.InitializeAsync();
        _localization.Setup(x => x.GetString("Cleaner_Privacy")).Returns("Privacy");
        var refreshed = false;
        group.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SelectableResultGroup.Header)) refreshed = true; };
        _localization.Raise(x => x.LanguageChanged += null, EventArgs.Empty);
        refreshed.Should().BeTrue();
        group.Header.Should().Be($"Privacy ({2:N0})");
        _vm.ResultGroups.Single().Should().BeSameAs(group);
        group.IsExpanded.Should().BeTrue();
        group.IsSelected.Should().BeNull();

        await _vm.ScanCommand.ExecuteAsync(null);
        _vm.ResultGroups.Single().Should().NotBeSameAs(group);
        _vm.ResultGroups.Single().IsExpanded.Should().BeFalse();
        _vm.SelectedCount.Should().Be(2);
        var notifications = 0;
        group.PropertyChanged += (_, _) => notifications++;
        group.Items[1].IsSelected = false;
        notifications.Should().Be(0);
        _vm.SelectedCount.Should().Be(2);
        Results();
        await _vm.ScanCommand.ExecuteAsync(null);
        _vm.ResultGroups.Should().BeEmpty();
    }

    [Fact]
    public async Task CollapsedGroupSelection_AppliesTheOriginalFindingIds_AfterConfirmation()
    {
        var first = Finding(CleanerArea.Registry);
        var second = Finding(CleanerArea.Registry);
        Results(first, second, Finding(CleanerArea.Registry, false), Finding(CleanerArea.JunkFiles));
        await _vm.ScanCommand.ExecuteAsync(null);
        _vm.ClearSelectionCommand.Execute(null);
        var registry = _vm.ResultGroups[0];
        registry.IsSelected = true;
        registry.IsExpanded.Should().BeFalse();
        _vm.ApplyCommand.CanExecute(null).Should().BeTrue();
        _dialogs.Setup(x => x.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse { Confirmed = true });
        _cleaner.Setup(x => x.ApplyAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CleanerApplyResult(2, []));
        await _vm.ApplyCommand.ExecuteAsync(null);
        _cleaner.Verify(x => x.ApplyAsync(It.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { first.Id, second.Id })),
            It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FooterCancellation_RequestsTheActiveToken_AndWaitsForTheWorkerToStop()
    {
        var tasks = new WinOptimizer.Infrastructure.Features.Common.Services.TaskProgressService(Mock.Of<ILogService>());
        using var page = CreateViewModel(tasks);
        using var footer = new WinOptimizer.UI.ViewModels.TaskProgressViewModel(tasks, _dispatcher.Object, _dialogs.Object, _localization.Object, Mock.Of<ILogService>());
        var worker = new TaskCompletionSource<CleanerScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _cleaner.Setup(x => x.ScanAsync(It.IsAny<CleanerOptions>(), It.IsAny<IReadOnlyCollection<CleanerArea>>(), It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>()))
            .Returns(worker.Task);
        var scan = page.ScanCommand.ExecuteAsync(null);
        var active = tasks.CurrentTaskCancellationSource!;
        footer.IsLoading.Should().BeTrue();
        footer.CancelCommand.Execute(null);
        active.IsCancellationRequested.Should().BeTrue();
        page.IsBusy.Should().BeTrue();
        page.HasStatus.Should().BeFalse();
        footer.LastTerminalLine.Should().Be("Progress_Cancelling");
        tasks.IsTaskRunning.Should().BeTrue();
        worker.SetCanceled(active.Token);
        await scan;
        page.IsBusy.Should().BeFalse();
        page.Status.Should().Be("Cleaner_Cancelled");
        page.Results.Should().BeEmpty();
        tasks.IsTaskRunning.Should().BeFalse();
        footer.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task BackendMeasurements_DriveTheFooter_WithoutPageProgressText()
    {
        var worker = new TaskCompletionSource<CleanerScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource<TaskProgressDetail>(TaskCreationOptions.RunContinuationsAsynchronously);
        IProgress<CleanerProgress>? reporter = null;
        _cleaner.Setup(x => x.ScanAsync(It.IsAny<CleanerOptions>(), It.IsAny<IReadOnlyCollection<CleanerArea>>(), It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>()))
            .Callback<CleanerOptions, IReadOnlyCollection<CleanerArea>, IProgress<CleanerProgress>, CancellationToken>((_, _, progress, _) => reporter = progress)
            .Returns(worker.Task);
        _tasks.Setup(x => x.UpdateDetailedProgress(It.IsAny<TaskProgressDetail>())).Callback<TaskProgressDetail>(detail => observed.TrySetResult(detail));
        var scan = _vm.ScanCommand.ExecuteAsync(null);
        reporter!.Report(new("Cleaner_Registry", 25, 100, 2, 3));
        var detail = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        detail.Progress.Should().BeApproximately(100d * 1.25 / 3, 0.001);
        detail.IsIndeterminate.Should().BeFalse();
        detail.TerminalOutput.Should().Contain("Cleaner_MeasuredProgress");
        _vm.HasStatus.Should().BeFalse();
        _vm.HasScanStatus.Should().BeFalse();
        _vm.Status.Should().BeEmpty();
        worker.SetResult(new CleanerScanResult([], []));
        await scan;
        _tasks.Verify(x => x.UpdateProgress(It.IsAny<int>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CancellationDuringTheLastResultBatch_DoesNotPublishAnApplicableSelection()
    {
        using var active = new CancellationTokenSource();
        _tasks.Setup(x => x.StartTask(It.IsAny<string>(), true)).Returns(active);
        Results(Enumerable.Range(0, 128).Select(_ => Finding(CleanerArea.Privacy)).ToArray());
        _vm.Results.CollectionChanged += (_, _) => { if (_vm.Results.Count == 128) active.Cancel(); };
        await _vm.ScanCommand.ExecuteAsync(null);
        _vm.Results.Should().BeEmpty();
        _vm.HasScanned.Should().BeFalse();
        _vm.ResultGroups.Should().BeEmpty();
        _vm.ApplyCommand.CanExecute(null).Should().BeFalse();
        _vm.Status.Should().Be("Cleaner_Cancelled");
    }

    [Fact]
    public async Task EmptyScan_PublishesReviewOnlyAfterCompletion_WithNotices()
    {
        _vm.HasScanned.Should().BeFalse();
        _vm.HasStatus.Should().BeFalse();
        var reviewPublished = false;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(CleanerViewModel.HasScanned) || !_vm.HasScanned) return;
            reviewPublished = true;
            _vm.IsBusy.Should().BeFalse();
            _vm.Notices.Should().Be("Browser is open");
            _tasks.Verify(x => x.CompleteTask(), Times.Once);
        };
        var completion = new TaskCompletionSource<CleanerScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _cleaner.Setup(x => x.ScanAsync(It.IsAny<CleanerOptions>(), It.IsAny<IReadOnlyCollection<CleanerArea>>(), It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>()))
            .Returns(completion.Task);
        var scan = _vm.ScanCommand.ExecuteAsync(null);
        _vm.HasScanned.Should().BeFalse();
        reviewPublished.Should().BeFalse();
        completion.SetResult(new CleanerScanResult([], ["Browser is open"]));
        await scan;
        _vm.HasScanned.Should().BeTrue();
        _vm.HasResults.Should().BeFalse();
        _vm.EmptyTitle.Should().Be("Cleaner_NoResults");
        _vm.HasNotices.Should().BeTrue();
        _vm.Notices.Should().Contain("Browser is open");
        _vm.EmptyDescription.Should().Be("Cleaner_NoResultsDescription");
        _vm.HasScanStatus.Should().BeFalse();
        reviewPublished.Should().BeTrue();
    }

    [Fact]
    public async Task CompletedScan_PublishesTheFullSelectionOnce_AndKeepsItWhenReinitialized()
    {
        Results(Finding(CleanerArea.Privacy), Finding(CleanerArea.Registry));
        var published = 0;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(CleanerViewModel.HasScanned) || !_vm.HasScanned) return;
            published++;
            _vm.IsIdle.Should().BeTrue();
            _vm.ResultCount.Should().Be(2);
            _vm.SelectedCount.Should().Be(1);
            _vm.ApplyCommand.CanExecute(null).Should().BeTrue();
        };
        await _vm.ScanCommand.ExecuteAsync(null);
        _vm.Results[1].IsSelected = true;
        await _vm.InitializeAsync();
        _vm.HasScanned.Should().BeTrue();
        _vm.SelectedCount.Should().Be(2);
        published.Should().Be(1);
        _cleaner.Verify(x => x.ScanAsync(It.IsAny<CleanerOptions>(), It.IsAny<IReadOnlyCollection<CleanerArea>>(), It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledRescan_HidesPreviousReviewAndDoesNotPublishResults(bool cancelled)
    {
        Results(Finding(CleanerArea.Privacy));
        await _vm.ScanCommand.ExecuteAsync(null);
        _vm.HasScanned.Should().BeTrue();
        var worker = new TaskCompletionSource<CleanerScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _cleaner.Setup(x => x.ScanAsync(It.IsAny<CleanerOptions>(), It.IsAny<IReadOnlyCollection<CleanerArea>>(), It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>()))
            .Returns(worker.Task);
        var scan = _vm.ScanCommand.ExecuteAsync(null);
        _vm.HasScanned.Should().BeFalse();
        _vm.Results.Should().BeEmpty();
        if (cancelled) worker.SetCanceled();
        else worker.SetException(new IOException("Scan failed"));
        await scan;
        _vm.HasScanned.Should().BeFalse();
        _vm.Results.Should().BeEmpty();
        _vm.IsIdle.Should().BeTrue();
        _vm.ResultGroups.Should().BeEmpty();
        _vm.HasScanStatus.Should().BeTrue();
        _vm.Status.Should().Be(cancelled ? "Cleaner_Cancelled" : "Scan failed");
        _vm.ApplyCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task CancellationAfterResultPublication_DoesNotExposeReview()
    {
        using var active = new CancellationTokenSource();
        _tasks.Setup(x => x.StartTask(It.IsAny<string>(), true)).Returns(active);
        Results(Finding(CleanerArea.Privacy));
        var published = false;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CleanerViewModel.HasScanned) && _vm.HasScanned) published = true;
            if (e.PropertyName == nameof(CleanerViewModel.Status) && _vm.Status == "Cleaner_ScanComplete") active.Cancel();
        };
        await _vm.ScanCommand.ExecuteAsync(null);
        published.Should().BeFalse();
        _vm.HasScanned.Should().BeFalse();
        _vm.Results.Should().BeEmpty();
        _vm.Status.Should().Be("Cleaner_Cancelled");
        _vm.ResultGroups.Should().BeEmpty();
    }

    [Fact]
    public void CategoryCards_ExposeDescriptionsAndNotifyWhenSelectionChanges()
    {
        _localization.Setup(x => x.GetString("Cleaner_CategorySummary")).Returns("{0}/{1}");
        _vm.CategorySummary.Should().Be("4/6");
        List<string?> changes = [];
        _vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        _vm.Areas[0].Description.Should().Be("Cleaner_Privacy_Description");
        _vm.Areas[0].IsSelected = false;
        _vm.CategorySummary.Should().Be("3/6");
        changes.Should().Contain(nameof(CleanerViewModel.CategorySummary));
        List<string?> choiceChanges = [];
        _vm.Areas[0].PropertyChanged += (_, e) => choiceChanges.Add(e.PropertyName);
        _localization.Raise(x => x.LanguageChanged += null, EventArgs.Empty);
        choiceChanges.Should().Contain(nameof(CleanerChoice<CleanerArea>.Description));
    }
    [Fact]
    public async Task Restore_RequiresConfirmation_AndReportsConflicts()
    {
        var backup = new CleanerBackupInfo(Guid.NewGuid(), DateTime.UtcNow, 1, 1);
        _backups.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { backup });
        await _settings.ReloadBackupsCommand.ExecuteAsync(null);
        _settings.SelectedBackup.Should().Be(backup);
        _dialogs.Setup(x => x.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse { Confirmed = false });
        await _settings.RestoreBackupCommand.ExecuteAsync(null);
        _backups.Verify(x => x.RestoreAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _dialogs.Setup(x => x.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse { Confirmed = true });
        _backups.Setup(x => x.RestoreAsync(backup.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new CleanerRestoreResult(1, new[] { "conflict preserved" }));
        await _settings.RestoreBackupCommand.ExecuteAsync(null);
        _settings.Status.Should().Contain("conflict preserved");
        _tasks.Verify(x => x.CompleteTask(), Times.Once);
    }
    [Fact]
    public async Task Settings_EachEditAutomaticallyPersistsTypedCleanupChoices()
    {
        await _settings.LoadAsync();
        _settings.Applications.Select(x => x.Value).Should().Equal("Installed.App_publisher");
        _settings.DeepClean = true; _settings.BrowserHistory = true; _settings.MinimumAgeDays = 14;
        _settings.RegistryCategories.Single(x => x.Value == RegistryArea.Fonts).IsSelected = false;
        _settings.Applications.Single().IsSelected = true;
        await _settings.FlushAsync();
        _settings.Options.DeepClean.Should().BeTrue();
        _settings.Options.MinimumAgeDays.Should().Be(14);
        _settings.Options.RegistryAreas.Should().NotContain(RegistryArea.Fonts);
        _preferences.Verify(x => x.SetPreferenceAsync("Cleaner", It.Is<CleanerOptions>(o => o.BrowserHistory && o.ApplicationCaches.SequenceEqual(new[] { "Installed.App_publisher" }))), Times.Once);
    }
    [Fact]
    public async Task Settings_FailedSave_PreservesPublishedOptions()
    {
        await _settings.LoadAsync(); var old = _settings.Options;
        _preferences.Setup(x => x.SetPreferenceAsync("Cleaner", It.IsAny<CleanerOptions>())).ReturnsAsync(OperationResult.Failed("write failed"));
        _settings.DeepClean = true;
        await _settings.FlushAsync();
        _settings.Options.Should().BeSameAs(old); _settings.Status.Should().Be("write failed");
    }

    [Fact]
    public async Task Settings_LoadDoesNotWrite_AndCategoryResetPreservesOtherPreferences()
    {
        await _settings.LoadAsync();
        _preferences.Verify(x => x.SetPreferenceAsync("Cleaner", It.IsAny<CleanerOptions>()), Times.Never);
        _settings.BrowserHistory = true;
        _settings.RecentFiles = false;
        _settings.DeepClean = true;
        _settings.MinimumAgeDays = 14;
        _settings.BackupBeforeCleaning = false;
        (await _settings.ResetSectionAsync(CleanerSettingsSection.Privacy)).Should().BeTrue();
        _settings.Options.BrowserHistory.Should().BeFalse();
        _settings.Options.RecentFiles.Should().BeTrue();
        _settings.Options.DeepClean.Should().BeTrue();
        _settings.Options.MinimumAgeDays.Should().Be(14);
        _settings.Options.BackupBeforeCleaning.Should().BeFalse();
    }

    /// <summary>Recent-document repair has its own persisted choice and category reset.</summary>
    [Fact]
    public async Task RecentShortcuts_SaveReloadAndResetIndependentlyOfPrivacyHistory()
    {
        CleanerOptions saved = new();
        _preferences.Setup(value => value.GetPreferenceAsync("Cleaner", It.IsAny<CleanerOptions>())).Returns((string _, CleanerOptions _) => Task.FromResult(saved));
        _preferences.Setup(value => value.SetPreferenceAsync("Cleaner", It.IsAny<CleanerOptions>()))
            .Callback<string, CleanerOptions>((_, options) => saved = options).ReturnsAsync(OperationResult.Succeeded());
        await _settings.LoadAsync();
        _settings.RecentShortcuts.Should().BeTrue();
        _settings.RecentShortcuts = false;
        _settings.RecentFiles = false;
        (await _settings.FlushAsync()).Should().BeTrue();
        saved.RecentShortcuts.Should().BeFalse();
        using var reloaded = new CleanerSettingsViewModel(_preferences.Object, _backend.Object, _localization.Object, _tasks.Object,
            Mock.Of<IExplorerWindowManager>(), _backups.Object, _dialogs.Object, _mode.Object);
        await reloaded.LoadAsync();
        reloaded.RecentShortcuts.Should().BeFalse();
        (await reloaded.ResetSectionAsync(CleanerSettingsSection.Shortcuts)).Should().BeTrue();
        saved.RecentShortcuts.Should().BeTrue();
        saved.RecentFiles.Should().BeFalse();
        reloaded.RecentShortcuts = false;
        (await reloaded.ResetSectionAsync(CleanerSettingsSection.Privacy)).Should().BeTrue();
        saved.RecentFiles.Should().BeTrue();
        saved.RecentShortcuts.Should().BeFalse();
    }

    [Fact]
    public async Task ExpandedPrivacyOptions_SaveReloadAndResetWithTheirOwnCategory()
    {
        CleanerOptions saved = new();
        _preferences.Setup(x => x.GetPreferenceAsync("Cleaner", It.IsAny<CleanerOptions>())).Returns((string _, CleanerOptions _) => Task.FromResult(saved));
        _preferences.Setup(x => x.SetPreferenceAsync("Cleaner", It.IsAny<CleanerOptions>()))
            .Callback<string, CleanerOptions>((_, options) => saved = options).ReturnsAsync(OperationResult.Succeeded());
        await _settings.LoadAsync();
        _settings.WindowsHistory = false;
        _settings.ApplicationPrivacyCaches = false;
        _settings.CertificateCache = false;
        _settings.JumpLists = true;
        _settings.ApplicationHistory = true;
        _settings.BrowserOfflineCache = true;
        _settings.DefenderLogs = true;
        _settings.MinimumAgeDays = 21;
        (await _settings.FlushAsync()).Should().BeTrue();
        saved.Should().BeEquivalentTo(new CleanerOptions
        {
            WindowsHistory = false, ApplicationPrivacyCaches = false, CertificateCache = false,
            JumpLists = true, ApplicationHistory = true, BrowserOfflineCache = true, DefenderLogs = true, MinimumAgeDays = 21
        });
        using var reloaded = new CleanerSettingsViewModel(_preferences.Object, _backend.Object, _localization.Object, _tasks.Object,
            Mock.Of<IExplorerWindowManager>(), _backups.Object, _dialogs.Object, _mode.Object);
        await reloaded.LoadAsync();
        reloaded.Options.Should().BeEquivalentTo(saved);
        reloaded.WindowsHistory.Should().BeFalse();
        reloaded.ApplicationPrivacyCaches.Should().BeFalse();
        reloaded.CertificateCache.Should().BeFalse();
        reloaded.JumpLists.Should().BeTrue();
        reloaded.ApplicationHistory.Should().BeTrue();
        reloaded.BrowserOfflineCache.Should().BeTrue();
        reloaded.DefenderLogs.Should().BeTrue();
        (await reloaded.ResetSectionAsync(CleanerSettingsSection.Privacy)).Should().BeTrue();
        reloaded.Options.Should().BeEquivalentTo(new CleanerOptions { MinimumAgeDays = 21 });
    }

    [Fact]
    public async Task RegistryAccounts_RestoreAnExplicitEmptySelection_AndPassItToScan()
    {
        _preferences.Setup(x => x.GetPreferenceAsync("Cleaner", It.IsAny<CleanerOptions>()))
            .ReturnsAsync(new CleanerOptions { RegistryAccountSids = [] });
        await _settings.LoadAsync();
        _settings.RegistryAccounts.Should().OnlyContain(account => !account.IsSelected);
        await _vm.ScanCommand.ExecuteAsync(null);
        _cleaner.Verify(x => x.ScanAsync(It.Is<CleanerOptions>(options => options.RegistryAccountSids != null && options.RegistryAccountSids.Length == 0),
            It.IsAny<IReadOnlyCollection<CleanerArea>>(), It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(CleanerSettingsSection.Privacy)]
    [InlineData(CleanerSettingsSection.Junk)]
    [InlineData(CleanerSettingsSection.Shortcuts)]
    public async Task FileAccountSelectionsAutosaveIndependently_AndResetOnlyTheirOwnCategory(CleanerSettingsSection section)
    {
        await _settings.LoadAsync();
        var choices = _settings.AccountChoices(section)!;
        var current = choices.Single(account => account.Value.EndsWith("1001"));
        var other = choices.Single(account => account.Value.EndsWith("1002"));
        current.IsSelected.Should().BeTrue();
        other.IsSelected.Should().BeFalse();
        current.IsSelected = false;
        other.IsSelected = true;
        (await _settings.FlushAsync()).Should().BeTrue();
        string[]? Selection() => section switch
        {
            CleanerSettingsSection.Privacy => _settings.Options.PrivacyAccountSids,
            CleanerSettingsSection.Junk => _settings.Options.JunkAccountSids,
            _ => _settings.Options.ShortcutAccountSids
        };
        Selection().Should().Equal(other.Value);
        _settings.RegistryAccounts.Single(account => account.Value.EndsWith("1001")).IsSelected.Should().BeTrue();
        (await _settings.ResetSectionAsync(CleanerSettingsSection.Registry)).Should().BeTrue();
        Selection().Should().Equal(other.Value);
        (await _settings.ResetSectionAsync(section)).Should().BeTrue();
        Selection().Should().BeNull();
        current.IsSelected.Should().BeTrue();
        other.IsSelected.Should().BeFalse();
    }

    [Fact]
    public async Task EmptyFileAccountScopesPersistAndReachTheScan_AndUnavailableProfilesStayVisible()
    {
        const string removed = "S-1-5-21-1-2-3-9999";
        _preferences.Setup(preferences => preferences.GetPreferenceAsync("Cleaner", It.IsAny<CleanerOptions>()))
            .ReturnsAsync(new CleanerOptions { PrivacyAccountSids = [], JunkAccountSids = [removed], ShortcutAccountSids = [] });
        await _settings.LoadAsync();
        _settings.PrivacyAccounts.Should().OnlyContain(account => !account.IsSelected);
        _settings.ShortcutAccounts.Should().OnlyContain(account => !account.IsSelected);
        _settings.JunkAccounts.Single(account => account.Value == removed).IsSelected.Should().BeTrue();
        _settings.JunkAccounts.Single(account => account.Value == removed).Description.Should().Be("Cleaner_RegistryAccountUnavailable");
        await _vm.ScanCommand.ExecuteAsync(null);
        _cleaner.Verify(cleaner => cleaner.ScanAsync(It.Is<CleanerOptions>(options => options.PrivacyAccountSids!.Length == 0 &&
            options.ShortcutAccountSids!.Length == 0 && options.JunkAccountSids!.SequenceEqual(new[] { removed })),
            It.IsAny<IReadOnlyCollection<CleanerArea>>(), It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task JunkAccountChangesRefreshTheApplicationInventory_AndRejectOlderCompletions()
    {
        await _settings.LoadAsync();
        var pending = new TaskCompletionSource<CleanerApplicationInventory>();
        _backend.Setup(backend => backend.GetApplicationsAsync(It.Is<CleanerOptions>(options => options.JunkAccountSids != null && options.JunkAccountSids.Length == 0), It.IsAny<CancellationToken>()))
            .Returns(pending.Task);
        _backend.Setup(backend => backend.GetApplicationsAsync(It.Is<CleanerOptions>(options => options.JunkAccountSids != null && options.JunkAccountSids.Length == 1), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CleanerApplicationInventory([new("Other.App_publisher", "Other app")], []));
        _settings.JunkAccounts.Single(account => account.Value.EndsWith("1001")).IsSelected = false;
        _settings.JunkAccounts.Single(account => account.Value.EndsWith("1002")).IsSelected = true;
        pending.SetResult(new([new("Stale.App_publisher", "Stale app")], []));
        (await _settings.FlushAsync()).Should().BeTrue();
        _settings.Applications.Select(app => app.Value).Should().Equal("Other.App_publisher");
        _settings.Options.JunkAccountSids.Should().Equal("S-1-5-21-1-2-3-1002");
    }

    [Fact]
    public async Task JunkInventoryNoticesRemainSeparateFromInvalidPreferenceErrors()
    {
        await _settings.LoadAsync();
        _backend.Setup(backend => backend.GetApplicationsAsync(It.IsAny<CleanerOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CleanerApplicationInventory([], ["Account application inventory unavailable"]));
        _settings.MinimumAgeDays = double.NaN;
        _settings.JunkAccounts.Single(account => account.Value.EndsWith("1001")).IsSelected = false;
        (await _settings.FlushAsync()).Should().BeFalse();
        _settings.Status.Should().Be("Cleaner_InvalidAge");
        _settings.ApplicationStatus.Should().Be("Account application inventory unavailable");
    }

    [Fact]
    public async Task SavedUnavailableAccountRemainsVisibleAndSelectedUntilUserChangesIt()
    {
        const string removed = "S-1-5-21-1-2-3-9999";
        _preferences.Setup(x => x.GetPreferenceAsync("Cleaner", It.IsAny<CleanerOptions>()))
            .ReturnsAsync(new CleanerOptions { RegistryAccountSids = [removed] });
        await _settings.LoadAsync();
        var account = _settings.RegistryAccounts.Single(account => account.Value == removed);
        account.IsSelected.Should().BeTrue();
        account.Description.Should().Be("Cleaner_RegistryAccountUnavailable");
        _settings.BrowserHistory = true;
        await _settings.FlushAsync();
        _settings.Options.RegistryAccountSids.Should().Equal(removed);
        account.IsSelected = false;
        await _settings.FlushAsync();
        _settings.Options.RegistryAccountSids.Should().BeEmpty();
    }

    [Fact]
    public async Task Settings_JunkResetRecoversInvalidAge_AndKeepsOtherCategories()
    {
        await _settings.LoadAsync();
        _settings.DeepClean = true;
        _settings.MinimumAgeDays = double.NaN;
        (await _settings.FlushAsync()).Should().BeFalse();
        (await _settings.ResetSectionAsync(CleanerSettingsSection.Junk)).Should().BeTrue();
        _settings.MinimumAgeDays.Should().Be(7);
        _settings.Options.DeepClean.Should().BeTrue();
        _settings.Status.Should().BeEmpty();
    }

    /// <summary>New junk switches participate in automatic saves, category reset and scan snapshots.</summary>
    [Fact]
    public async Task ExpandedJunkSettings_AutosaveResetAndReachTheScanner()
    {
        await _settings.LoadAsync();
        _settings.WindowsLogs = false;
        _settings.CrashDumps = false;
        _settings.InternetCache = false;
        _settings.MinimumAgeDays = 0;
        (await _settings.FlushAsync()).Should().BeTrue();
        _settings.Options.WindowsLogs.Should().BeFalse();
        _settings.Options.CrashDumps.Should().BeFalse();
        _settings.Options.InternetCache.Should().BeFalse();
        _settings.Options.MinimumAgeDays.Should().Be(0);
        (await _settings.ResetSectionAsync(CleanerSettingsSection.Privacy)).Should().BeTrue();
        await _vm.ScanCommand.ExecuteAsync(null);
        _cleaner.Verify(x => x.ScanAsync(It.Is<CleanerOptions>(options => !options.WindowsLogs && !options.CrashDumps &&
            !options.InternetCache && options.MinimumAgeDays == 0), It.IsAny<IReadOnlyCollection<CleanerArea>>(),
            It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
        (await _settings.ResetSectionAsync(CleanerSettingsSection.Junk)).Should().BeTrue();
        _settings.Options.WindowsLogs.Should().BeTrue();
        _settings.Options.CrashDumps.Should().BeTrue();
        _settings.Options.InternetCache.Should().BeTrue();
        _settings.Options.MinimumAgeDays.Should().Be(7);
    }

    [Fact]
    public async Task Scan_WaitsForAutomaticPersistence_AndUsesLatestEdits()
    {
        await _settings.LoadAsync();
        var write = new TaskCompletionSource<OperationResult>();
        _preferences.Setup(x => x.SetPreferenceAsync("Cleaner", It.IsAny<CleanerOptions>())).Returns(write.Task);
        _settings.DeepClean = true;
        var scan = _vm.ScanCommand.ExecuteAsync(null);
        scan.IsCompleted.Should().BeFalse();
        _cleaner.Verify(x => x.ScanAsync(It.IsAny<CleanerOptions>(), It.IsAny<IReadOnlyCollection<CleanerArea>>(), It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
        write.SetResult(OperationResult.Succeeded());
        await scan;
        _cleaner.Verify(x => x.ScanAsync(It.Is<CleanerOptions>(options => options.DeepClean), It.IsAny<IReadOnlyCollection<CleanerArea>>(), It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Scan_InvalidAutomaticEditBlocksScanning()
    {
        await _settings.LoadAsync();
        _settings.MinimumAgeDays = double.NaN;
        await _vm.ScanCommand.ExecuteAsync(null);
        _cleaner.Verify(x => x.ScanAsync(It.IsAny<CleanerOptions>(), It.IsAny<IReadOnlyCollection<CleanerArea>>(), It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
        _vm.Status.Should().NotBeEmpty();
    }
    [Theory]
    [InlineData(-1)] [InlineData(1.5)] [InlineData(366)] [InlineData(double.NaN)]
    public async Task Settings_InvalidAge_IsNotPersisted(double age)
    {
        await _settings.LoadAsync(); _settings.MinimumAgeDays = age;
        await _settings.FlushAsync();
        _preferences.Verify(x => x.SetPreferenceAsync("Cleaner", It.IsAny<CleanerOptions>()), Times.Never);
    }
    [Fact]
    public async Task Scan_PreviewsResults_AndRegistryRequiresExplicitSelection()
    {
        Results(Finding(CleanerArea.Registry), Finding(CleanerArea.Privacy), Finding(CleanerArea.Registry, false));
        await _vm.ScanCommand.ExecuteAsync(null);
        _vm.Results.Should().HaveCount(3);
        _vm.Results[0].IsSelected.Should().BeFalse();
        _vm.Results[1].IsSelected.Should().BeTrue();
        _vm.SelectAllCommand.Execute(null);
        _vm.Results[2].IsSelected.Should().BeFalse();
        _cleaner.Verify(x => x.ApplyAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
    [Fact]
    public async Task Apply_CancelledConfirmation_DoesNotClean()
    {
        Results(Finding(CleanerArea.Privacy));
        await _vm.ScanCommand.ExecuteAsync(null);
        _dialogs.Setup(x => x.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse { Confirmed = false });
        await _vm.ApplyCommand.ExecuteAsync(null);
        _cleaner.Verify(x => x.ApplyAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
        _vm.IsBusy.Should().BeFalse();
    }
    [Fact]
    public async Task Apply_Confirmed_OnlyAppliesSelectedResults_ThenRequiresRescan()
    {
        var selected = Finding(CleanerArea.Privacy); var omitted = Finding(CleanerArea.Registry);
        Results(selected, omitted);
        await _vm.ScanCommand.ExecuteAsync(null);
        _dialogs.Setup(x => x.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse { Confirmed = true });
        _cleaner.Setup(x => x.ApplyAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>())).ReturnsAsync(new CleanerApplyResult(1, []));
        await _vm.ApplyCommand.ExecuteAsync(null);
        _cleaner.Verify(x => x.ApplyAsync(It.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(selected.Id)), It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
        _vm.ApplyCommand.CanExecute(null).Should().BeFalse();
    }
    [Fact]
    public async Task EditingSettings_InvalidatesPreviouslyScannedSelection()
    {
        Results(Finding(CleanerArea.Privacy)); await _vm.ScanCommand.ExecuteAsync(null);
        _vm.ApplyCommand.CanExecute(null).Should().BeTrue();
        _settings.DeepClean = !_settings.DeepClean;
        await _settings.FlushAsync();
        _vm.ApplyCommand.CanExecute(null).Should().BeFalse();
    }
    [Fact]
    public async Task CancelledScan_CompletesGlobalTask_AndLeavesNoApplicableResults()
    {
        _cleaner.Setup(x => x.ScanAsync(It.IsAny<CleanerOptions>(), It.IsAny<IReadOnlyCollection<CleanerArea>>(), It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());
        await _vm.ScanCommand.ExecuteAsync(null);
        _vm.IsBusy.Should().BeFalse(); _vm.Results.Should().BeEmpty();
        _vm.ApplyCommand.CanExecute(null).Should().BeFalse();
        _tasks.Verify(x => x.CompleteTask(), Times.Once);
    }
    [Fact]
    public async Task InvalidPersistedSettings_BlockScan_AndExplicitResetRecovers()
    {
        _preferences.Setup(x => x.GetPreferenceAsync("Cleaner", It.IsAny<CleanerOptions>())).ReturnsAsync(new CleanerOptions { MinimumAgeDays = -2 });
        await _vm.ScanCommand.ExecuteAsync(null);
        _cleaner.Verify(x => x.ScanAsync(It.IsAny<CleanerOptions>(), It.IsAny<IReadOnlyCollection<CleanerArea>>(), It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
        await _settings.ResetAllAsync();
        _settings.IsLoaded.Should().BeTrue(); _settings.Options.MinimumAgeDays.Should().Be(7);
        _preferences.Verify(x => x.SetPreferenceAsync("Cleaner", It.IsAny<CleanerOptions>()), Times.Once);
    }
    public void Dispose() { _vm.Dispose(); _settings.Dispose(); }
}
