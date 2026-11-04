using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Cleaner.Interfaces;
using WinOptimizer.Core.Features.Cleaner.Models;
using WinOptimizer.Core.Features.Cleaner.Services;
using Xunit;

namespace WinOptimizer.Core.Tests.Cleaner;

public class CleanerServiceTests
{
    private readonly Mock<ICleanerBackend> _backend = new();
    private readonly Mock<ICleanerBackupService> _backups = new();
    public CleanerServiceTests()
    {
        _backups.Setup(x => x.CreateAsync(It.IsAny<IReadOnlyList<CleanerFinding>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CleanerBackupInfo(Guid.NewGuid(), DateTime.UtcNow, 0, 0));
    }
    /// <summary>Creates distinct resources unless a regression explicitly supplies overlapping evidence.</summary>
    private static CleanerFinding Finding(bool actionable = true)
    {
        var id = Guid.NewGuid();
        return new(id, CleanerArea.JunkFiles, "Cleaner_JunkFiles", "temporary file", 10, actionable,
            new CleanerFileTarget(@"C:\Temp", $@"C:\Temp\{id:N}.tmp", 10, DateTime.UnixEpoch, DateTime.UtcNow));
    }

    private CleanerService SetupScan(params CleanerFinding[] findings)
    {
        _backend.Setup(x => x.ScanAsync(It.IsAny<CleanerArea>(), It.IsAny<CleanerOptions>(), It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CleanerScanResult(findings, []));
        _backend.Setup(x => x.CleanAsync(It.IsAny<CleanerFinding>(), It.IsAny<CleanerOptions>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return new(_backend.Object, _backups.Object);
    }
    private static Task<CleanerScanResult> Scan(CleanerService service) => service.ScanAsync(new(), [CleanerArea.JunkFiles], null, default);

    [Fact]
    public async Task CleanupReportsEachOriginalResource_AndCountsOnlySuccessfulFileSpace()
    {
        var file = Finding();
        var denied = Finding() with { Area = CleanerArea.Privacy };
        var registry = Finding() with { Area = CleanerArea.Registry, Bytes = 128,
            Target = new CleanerRegistryTarget(@"HKEY_CURRENT_USER\Software\Example", "value", "data", 1, false, "missing") };
        var maintenance = Finding() with { Area = CleanerArea.Disk, Bytes = 0, Target = new CleanerDiskTarget(@"C:\") };
        var findings = new[] { file, denied, registry, maintenance };
        _backend.Setup(backend => backend.ScanAsync(It.IsAny<CleanerArea>(), It.IsAny<CleanerOptions>(), It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CleanerArea area, CleanerOptions _, IProgress<CleanerProgress>? _, CancellationToken _) =>
                new CleanerScanResult(findings.Where(finding => finding.Area == area).ToArray(), []));
        _backend.Setup(backend => backend.CleanAsync(It.IsAny<CleanerFinding>(), It.IsAny<CleanerOptions>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _backend.Setup(backend => backend.CleanAsync(denied, It.IsAny<CleanerOptions>(), It.IsAny<CancellationToken>())).ThrowsAsync(new UnauthorizedAccessException("Protected file"));
        var service = new CleanerService(_backend.Object, _backups.Object);
        await service.ScanAsync(new(), findings.Select(finding => finding.Area).ToArray(), null, default);

        var result = await service.ApplyAsync(findings.Select(finding => finding.Id).ToArray(), null, default);

        Assert.Equal(3, result.Completed);
        Assert.Single(result.Errors);
        Assert.Equal(4, result.Items.Count);
        Assert.Same(file, Assert.Single(result.Items, item => item.Finding.Id == file.Id).Finding);
        Assert.Equal(2, result.Items.Count(item => item.Removed));
        Assert.Equal(10, result.Items.Sum(item => item.EstimatedFreedFileBytes));
        Assert.Equal(128, result.Items.Sum(item => item.RemovedRegistryBytes));
        Assert.False(result.IsCancelled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationRetainsConfirmedOutcomes_WithoutClaimingUnfinishedWork(bool insideBackend)
    {
        using var cancellation = new CancellationTokenSource();
        var first = Finding(); var second = Finding();
        var service = SetupScan(first, second);
        await Scan(service);
        _backend.Setup(backend => backend.CleanAsync(first, It.IsAny<CleanerOptions>(), It.IsAny<CancellationToken>()))
            .Callback(() => { if (!insideBackend) cancellation.Cancel(); }).Returns(Task.CompletedTask);
        if (insideBackend)
            _backend.Setup(backend => backend.CleanAsync(second, It.IsAny<CleanerOptions>(), It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());

        var result = await service.ApplyAsync([first.Id, second.Id], null, cancellation.Token);

        Assert.True(result.IsCancelled);
        Assert.Equal(1, result.Completed);
        Assert.Empty(result.Errors);
        Assert.Same(first, Assert.Single(result.Items).Finding);
        Assert.Equal(10, result.Items[0].EstimatedFreedFileBytes);
        if (!insideBackend) _backend.Verify(backend => backend.CleanAsync(second, It.IsAny<CleanerOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void ExistingPreferencesWithoutBackupFields_EnableBothBackups()
    {
        var options = System.Text.Json.JsonSerializer.Deserialize<CleanerOptions>("{\"DeepClean\":true}")!;
        options.BackupBeforeCleaning.Should().BeTrue();
        options.BackupBeforeRegistryDefrag.Should().BeTrue();
        options.PrivacyAccountSids.Should().BeNull();
        options.JunkAccountSids.Should().BeNull();
        options.ShortcutAccountSids.Should().BeNull();
        options.WindowsHistory.Should().BeTrue();
        options.ApplicationPrivacyCaches.Should().BeTrue();
        options.CertificateCache.Should().BeTrue();
        options.JumpLists.Should().BeFalse();
        options.ApplicationHistory.Should().BeFalse();
        options.DefenderLogs.Should().BeFalse();
        options.BrowserOfflineCache.Should().BeFalse();
        options.RecentShortcuts.Should().BeTrue();
    }

    [Fact]
    public async Task PrivacySubstages_PreserveMeasuredProgressWithinTheOverallScan()
    {
        _backend.Setup(x => x.ScanAsync(It.IsAny<CleanerArea>(), It.IsAny<CleanerOptions>(), It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>()))
            .Returns<CleanerArea, CleanerOptions, IProgress<CleanerProgress>, CancellationToken>((area, _, progress, _) =>
            {
                if (area == CleanerArea.Privacy) progress.Report(new("privacy registry", 5, 10, 2, 2));
                return Task.FromResult(new CleanerScanResult([], []));
            });
        List<CleanerProgress> reports = [];
        var progress = new Mock<IProgress<CleanerProgress>>();
        progress.Setup(x => x.Report(It.IsAny<CleanerProgress>())).Callback<CleanerProgress>(reports.Add);
        await new CleanerService(_backend.Object, _backups.Object).ScanAsync(new(), [CleanerArea.Privacy, CleanerArea.Registry], progress.Object, default);
        reports.Single(x => x.Message == "privacy registry").Percentage.Should().Be(37.5);
        reports[^1].Percentage.Should().Be(100);
    }

    [Fact]
    public async Task OverlappingPrivacyAndRegistryRules_PublishAndApplyTheValueOnlyOnce()
    {
        var target = new CleanerRegistryTarget(@"HKEY_CURRENT_USER\Software\History", "url1", "\"value\"", 1, false, "") { PrivacyRule = "Cleaner_ExplorerHistory" };
        var privacy = new CleanerFinding(Guid.NewGuid(), CleanerArea.Privacy, "Cleaner_ExplorerHistory", "history", 0, true, target);
        var registry = privacy with { Id = Guid.NewGuid(), Area = CleanerArea.Registry, Target = target with { KeyPath = target.KeyPath.ToUpperInvariant() } };
        _backend.Setup(x => x.ScanAsync(CleanerArea.Privacy, It.IsAny<CleanerOptions>(), It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CleanerScanResult([privacy], []));
        _backend.Setup(x => x.ScanAsync(CleanerArea.Registry, It.IsAny<CleanerOptions>(), It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CleanerScanResult([registry], []));
        var service = new CleanerService(_backend.Object, _backups.Object);
        var scan = await service.ScanAsync(new(), [CleanerArea.Registry, CleanerArea.Privacy], null, default);
        scan.Findings.Should().ContainSingle().Which.Should().Be(privacy);
        (await service.ApplyAsync([privacy.Id], null, default)).Completed.Should().Be(1);
        _backups.Verify(x => x.CreateAsync(It.Is<IReadOnlyList<CleanerFinding>>(items => items.Count == 1 && items[0] == privacy), It.IsAny<CancellationToken>()), Times.Once);
    }
    /// <summary>Overlapping file categories retain one finding, backup and cleanup in stage order.</summary>
    [Theory]
    [InlineData(CleanerArea.Privacy, CleanerArea.Shortcuts)]
    [InlineData(CleanerArea.Privacy, CleanerArea.JunkFiles)]
    [InlineData(CleanerArea.JunkFiles, CleanerArea.Shortcuts)]
    public async Task OverlappingFileResults_PublishAndApplyTheFileOnlyOnce(CleanerArea first, CleanerArea second)
    {
        var target = new CleanerFileTarget(@"C:\Profile\Recent", @"C:\Profile\Recent\document.lnk", 10, DateTime.UnixEpoch, DateTime.UtcNow);
        var privacy = new CleanerFinding(Guid.NewGuid(), first, "Cleaner_RecentFiles", target.Path, target.Length, true, target);
        var shortcut = privacy with { Id = Guid.NewGuid(), Area = second,
            Target = target with { Path = target.Path.ToUpperInvariant(), ShortcutTarget = @"C:\missing.txt" } };
        _backend.Setup(value => value.ScanAsync(first, It.IsAny<CleanerOptions>(), It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CleanerScanResult([privacy], []));
        _backend.Setup(value => value.ScanAsync(second, It.IsAny<CleanerOptions>(), It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CleanerScanResult([shortcut], []));
        var service = new CleanerService(_backend.Object, _backups.Object);
        var scan = await service.ScanAsync(new(), [second, first], null, default);
        scan.Findings.Should().ContainSingle().Which.Should().Be(privacy);
        (await service.ApplyAsync([privacy.Id], null, default)).Completed.Should().Be(1);
        _backups.Verify(value => value.CreateAsync(It.Is<IReadOnlyList<CleanerFinding>>(items => items.Count == 1 && items[0] == privacy), It.IsAny<CancellationToken>()), Times.Once);
        _backend.Verify(value => value.CleanAsync(privacy, It.IsAny<CleanerOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        _backend.Verify(value => value.CleanAsync(shortcut, It.IsAny<CleanerOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RequiredBackupFailure_BlocksEveryModification()
    {
        var item = Finding(); var service = SetupScan(item); await Scan(service);
        _backups.Setup(x => x.CreateAsync(It.IsAny<IReadOnlyList<CleanerFinding>>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("disk full"));
        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync([item.Id], null, default));
        _backend.Verify(x => x.CleanAsync(It.IsAny<CleanerFinding>(), It.IsAny<CleanerOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Scan_MapsBackendMeasurementsToOrderedStages_AndTotalsCheckedItems()
    {
        var service = new CleanerService(_backend.Object, _backups.Object);
        _backend.Setup(x => x.ScanAsync(It.IsAny<CleanerArea>(), It.IsAny<CleanerOptions>(), It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>()))
            .Returns<CleanerArea, CleanerOptions, IProgress<CleanerProgress>, CancellationToken>((area, _, progress, _) =>
            {
                progress.Report(new($"Cleaner_{area}", 5, 10));
                return Task.FromResult(new CleanerScanResult([], []) { CheckedItems = 10 });
            });
        List<CleanerProgress> reports = [];
        var progress = new Mock<IProgress<CleanerProgress>>();
        progress.Setup(x => x.Report(It.IsAny<CleanerProgress>())).Callback<CleanerProgress>(reports.Add);
        var result = await service.ScanAsync(new(), [CleanerArea.Registry, CleanerArea.JunkFiles], progress.Object, default);
        result.CheckedItems.Should().Be(20);
        reports.Should().Contain(x => x.Message == "Cleaner_JunkFiles" && x.Percentage == 25);
        reports.Should().Contain(x => x.Message == "Cleaner_Registry" && x.Percentage == 75);
        reports[^1].Percentage.Should().Be(100);
    }

    [Fact]
    public async Task Cancellation_StopsAnActiveBackend_AndReleasesTheScanGate()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new CleanerService(_backend.Object, _backups.Object);
        _backend.Setup(x => x.ScanAsync(It.IsAny<CleanerArea>(), It.IsAny<CleanerOptions>(), It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>()))
            .Returns<CleanerArea, CleanerOptions, IProgress<CleanerProgress>, CancellationToken>(async (_, _, _, token) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return new CleanerScanResult([], []);
            });
        var scan = service.ScanAsync(new(), [CleanerArea.Registry], null, cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync([Guid.NewGuid()], null, default));
        _backend.Setup(x => x.ScanAsync(It.IsAny<CleanerArea>(), It.IsAny<CleanerOptions>(), It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CleanerScanResult([], []));
        (await service.ScanAsync(new(), [CleanerArea.Registry], null, default)).Findings.Should().BeEmpty();
    }
    [Fact]
    public async Task DisabledBackup_IsPassedToBackend_AndDoesNotCreateSession()
    {
        var item = Finding(); var service = SetupScan(item);
        await service.ScanAsync(new() { BackupBeforeCleaning = false, BackupBeforeRegistryDefrag = false }, [CleanerArea.JunkFiles], null, default);
        await service.ApplyAsync([item.Id], null, default);
        _backups.Verify(x => x.CreateAsync(It.IsAny<IReadOnlyList<CleanerFinding>>(), It.IsAny<CancellationToken>()), Times.Never);
        _backend.Verify(x => x.CleanAsync(item, It.Is<CleanerOptions>(o => !o.BackupBeforeRegistryDefrag), It.IsAny<CancellationToken>()), Times.Once);
    }
    [Fact]
    public async Task BackupCompletesBeforeAnySelectedResourceIsCleaned()
    {
        var first = Finding(); var second = Finding(); var service = SetupScan(first, second); await Scan(service);
        List<string> order = [];
        _backups.Setup(x => x.CreateAsync(It.IsAny<IReadOnlyList<CleanerFinding>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<CleanerFinding>, CancellationToken>((items, token) => { items.Count.Should().Be(2); order.Add("backup"); })
            .ReturnsAsync(new CleanerBackupInfo(Guid.NewGuid(), DateTime.UtcNow, 2, 0));
        _backend.Setup(x => x.CleanAsync(It.IsAny<CleanerFinding>(), It.IsAny<CleanerOptions>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("clean")).Returns(Task.CompletedTask);
        await service.ApplyAsync([first.Id, second.Id], null, default);
        order.Should().Equal("backup", "clean", "clean");
    }
    [Fact]
    public async Task Apply_UsesOriginalEvidence_AndConsumesSuccessfulSelection()
    {
        var item = Finding(); var service = SetupScan(item);
        await Scan(service);
        var result = await service.ApplyAsync([item.Id, item.Id], null, default);
        result.Completed.Should().Be(1); result.Errors.Should().BeEmpty();
        _backend.Verify(x => x.CleanAsync(item, It.IsAny<CleanerOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync([item.Id], null, default));
    }

    [Fact]
    public async Task Apply_PerformsRegistryCorrectionsBeforeCompaction()
    {
        var registry = Finding() with { Area = CleanerArea.Registry };
        var defrag = Finding() with { Area = CleanerArea.RegistryDefrag };
        var service = new CleanerService(_backend.Object, _backups.Object);
        _backend.Setup(x => x.ScanAsync(CleanerArea.Registry, It.IsAny<CleanerOptions>(), It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>())).ReturnsAsync(new CleanerScanResult([registry], []));
        _backend.Setup(x => x.ScanAsync(CleanerArea.RegistryDefrag, It.IsAny<CleanerOptions>(), It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>())).ReturnsAsync(new CleanerScanResult([defrag], []));
        List<CleanerArea> order = [];
        _backend.Setup(x => x.CleanAsync(It.IsAny<CleanerFinding>(), It.IsAny<CleanerOptions>(), It.IsAny<CancellationToken>())).Callback<CleanerFinding, CleanerOptions, CancellationToken>((item, options, token) => order.Add(item.Area)).Returns(Task.CompletedTask);
        await service.ScanAsync(new(), [CleanerArea.RegistryDefrag, CleanerArea.Registry], null, default);
        await service.ApplyAsync([defrag.Id, registry.Id], null, default);
        order.Should().Equal(CleanerArea.Registry, CleanerArea.RegistryDefrag);
    }

    [Fact]
    public async Task Apply_RejectsUnknownOrDiagnosticSelection_BeforeAnyMutation()
    {
        var valid = Finding(); var diagnostic = Finding(false);
        var privacy = Finding(false) with { Area = CleanerArea.Privacy, UnavailableReason = "Close chrome and scan again." };
        var service = SetupScan(valid, diagnostic, privacy);
        await Scan(service);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync([valid.Id, Guid.NewGuid()], null, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync([valid.Id, diagnostic.Id], null, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync([valid.Id, privacy.Id], null, default));
        _backups.Verify(x => x.CreateAsync(It.IsAny<IReadOnlyList<CleanerFinding>>(), It.IsAny<CancellationToken>()), Times.Never);
        _backend.Verify(x => x.CleanAsync(It.IsAny<CleanerFinding>(), It.IsAny<CleanerOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NewFailedScan_InvalidatesPreviousResults()
    {
        var item = Finding(); var service = SetupScan(item); await Scan(service);
        _backend.Setup(x => x.ScanAsync(It.IsAny<CleanerArea>(), It.IsAny<CleanerOptions>(), It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("scan failed"));
        await Assert.ThrowsAsync<IOException>(() => Scan(service));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync([item.Id], null, default));
    }

    /// <summary>Access-denied registry failures are isolated to their finding, just like changed files.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_ReportsSpecificFailure_ContinuesRemainingSelection_AndPreventsReplay(bool registryDenied)
    {
        var broken = Finding(); var good = Finding(); var service = SetupScan(broken, good); await Scan(service);
        Exception failure = registryDenied ? new System.Security.SecurityException("Requested registry access is not allowed.") : new IOException("file changed");
        _backend.Setup(x => x.CleanAsync(broken, It.IsAny<CleanerOptions>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        var result = await service.ApplyAsync([broken.Id, good.Id], null, default);
        result.Completed.Should().Be(1); result.Errors.Should().ContainSingle().Which.Should().Contain(failure.Message);
        _backend.Verify(x => x.CleanAsync(good, It.IsAny<CleanerOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync([broken.Id], null, default));
    }

    [Fact]
    public async Task CancelledScan_DoesNotPublishPartialResults()
    {
        var item = Finding(); var service = SetupScan(item);
        _backend.Setup(x => x.ScanAsync(CleanerArea.Registry, It.IsAny<CleanerOptions>(), It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.ScanAsync(new(), [CleanerArea.JunkFiles, CleanerArea.Registry], null, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync([item.Id], null, default));
    }

    [Theory]
    [InlineData(-1)] [InlineData(366)]
    public void Options_RejectInvalidAge(int days) => Assert.Throws<ArgumentException>(() => new CleanerOptions { MinimumAgeDays = days }.Validate());

    /// <summary>Zero is an explicit opt-in to review recently modified junk files.</summary>
    [Fact]
    public void Options_AcceptZeroAge() => new CleanerOptions { MinimumAgeDays = 0 }.Validate().MinimumAgeDays.Should().Be(0);

    /// <summary>New categories get defaults while explicitly saved exclusions remain authoritative.</summary>
    [Fact]
    public void Options_OlderPreferencesGainNewSources_AndKeepSavedExclusions()
    {
        var options = System.Text.Json.JsonSerializer.Deserialize<CleanerOptions>("{\"WindowsTemp\":false,\"ErrorReports\":false}")!.Validate();
        options.WindowsTemp.Should().BeFalse();
        options.ErrorReports.Should().BeFalse();
        options.WindowsLogs.Should().BeTrue();
        options.CrashDumps.Should().BeTrue();
        options.InternetCache.Should().BeTrue();
    }

    [Theory]
    [InlineData("..\\outside")] [InlineData("C:\\Windows")] [InlineData("..")] [InlineData("/tmp")]
    public void Options_RejectPackagePathTraversal(string package) => Assert.Throws<ArgumentException>(() => new CleanerOptions { ApplicationCaches = [package] }.Validate());

    [Fact]
    public async Task InvalidOptions_AreRejectedBeforeScanning()
    {
        var service = SetupScan();
        await Assert.ThrowsAsync<ArgumentException>(() => service.ScanAsync(new() { DiskRoot = @"C:\ & whoami" }, [CleanerArea.Disk], null, default));
        _backend.Verify(x => x.ScanAsync(It.IsAny<CleanerArea>(), It.IsAny<CleanerOptions>(), It.IsAny<IProgress<CleanerProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("S-1-5-21-1-2-3-1001\\Software")]
    [InlineData("S-1-5-18")]
    [InlineData("S-1-5-21-1-2-3-9999999999")]
    [InlineData("")]
    [InlineData(null)]
    public void Options_RejectInvalidAccountIdentities(string? sid) =>
        Assert.Throws<ArgumentException>(() => new CleanerOptions { RegistryAccountSids = [sid!] }.Validate());

    [Theory]
    [InlineData(CleanerArea.Privacy)]
    [InlineData(CleanerArea.JunkFiles)]
    [InlineData(CleanerArea.Shortcuts)]
    public void FileAccountScopesRejectInvalidSids_AndAcceptEmptySelections(CleanerArea area)
    {
        CleanerOptions Scope(string[] selection) => area switch
        {
            CleanerArea.Privacy => new() { PrivacyAccountSids = selection },
            CleanerArea.JunkFiles => new() { JunkAccountSids = selection },
            _ => new() { ShortcutAccountSids = selection }
        };
        Assert.Throws<ArgumentException>(() => Scope(["S-1-5-21-1-2-3-1001\\Software"]).Validate());
        Assert.Throws<ArgumentException>(() => Scope(["S-1-5-18"]).Validate());
        Scope([]).Validate().Should().NotBeNull();
        Scope(["S-1-5-21-1-2-3-1001"]).Validate().Should().NotBeNull();
    }

    [Fact]
    public async Task ScanOwnsIndependentSnapshotsOfEveryFileAccountScope()
    {
        var item = Finding();
        var service = SetupScan(item);
        var privacy = new[] { "S-1-5-21-1-2-3-1001" };
        var junk = new[] { "S-1-5-21-1-2-3-1002" };
        var shortcuts = new[] { "S-1-5-21-1-2-3-1003" };
        await service.ScanAsync(new() { PrivacyAccountSids = privacy, JunkAccountSids = junk, ShortcutAccountSids = shortcuts }, [CleanerArea.JunkFiles], null, default);
        privacy[0] = junk[0] = shortcuts[0] = "S-1-5-21-1-2-3-9999";
        await service.ApplyAsync([item.Id], null, default);
        _backend.Verify(backend => backend.CleanAsync(item, It.Is<CleanerOptions>(options =>
            options.PrivacyAccountSids![0] == "S-1-5-21-1-2-3-1001" && options.JunkAccountSids![0] == "S-1-5-21-1-2-3-1002" &&
            options.ShortcutAccountSids![0] == "S-1-5-21-1-2-3-1003"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ScanOwnsAnIndependentSnapshotOfSelectedAccountSids()
    {
        var item = Finding();
        var service = SetupScan(item);
        var selected = new[] { "S-1-5-21-1-2-3-1001" };
        await service.ScanAsync(new() { RegistryAccountSids = selected }, [CleanerArea.Registry], null, default);
        selected[0] = "S-1-5-21-1-2-3-1002";
        await service.ApplyAsync([item.Id], null, default);
        _backend.Verify(backend => backend.CleanAsync(item, It.Is<CleanerOptions>(options => options.RegistryAccountSids!.SequenceEqual(new[] { "S-1-5-21-1-2-3-1001" })), It.IsAny<CancellationToken>()), Times.Once);
    }
}
