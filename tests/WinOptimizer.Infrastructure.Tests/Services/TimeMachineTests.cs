using Microsoft.Win32;
using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Infrastructure.Features.Cleaner;
using WinOptimizer.Infrastructure.Features.Common.Services;
using WinOptimizer.Infrastructure.Features.Drivers;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

public sealed class TimeMachineTests : IDisposable
{
    private const string AccountSid = "S-1-5-21-111-222-333-1001";
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("win-optimizer-timemachine-");
    private readonly Clock _clock = new();
    private readonly Mock<IInteractiveUserService> _user = new();
    private readonly Mock<IChangeHistoryService> _history = new();
    private readonly Mock<ILogService> _log = new();
    private readonly UserPreferencesService _preferences;
    private readonly TimeMachineStore _store;
    private readonly TimeMachineService _service;

    public TimeMachineTests()
    {
        _user.Setup(x => x.InteractiveUserSid).Returns(AccountSid);
        var log = _log.Object;
        _store = new(_user.Object, _clock, _history.Object, log, _directory.FullName);
        var files = new Mock<IFileSystemService>();
        var json = """{"Theme":"Dark","Language":"it","WindowWidth":1000}""";
        files.Setup(x => x.FileExists(It.IsAny<string>())).Returns(true);
        files.Setup(x => x.ReadAllTextAsync(It.IsAny<string>(), default)).Returns(() => Task.FromResult(json));
        files.Setup(x => x.WriteAllTextAsync(It.IsAny<string>(), It.IsAny<string>(), default))
            .Callback<string, string, CancellationToken>((_, content, _) => json = content).Returns(Task.CompletedTask);
        _preferences = new(log, _user.Object, files.Object, _store);
        var catalogue = new Mock<ICompatibleSettingsRegistry>();
        catalogue.Setup(x => x.GetAllFilteredSettings()).Returns(new Dictionary<string, IEnumerable<SettingDefinition>>());
        var mode = new Mock<IApplicationModeService>();
        mode.SetupGet(x => x.CurrentMode).Returns(WinOptimizerMode.Normal);
        mode.Setup(x => x.BeginSystemOperation()).Returns(Mock.Of<IDisposable>());
        var tasks = Mock.Of<IScheduledTaskService>();
        _service = new(_store, _history.Object, catalogue.Object, new(log, _user.Object, _store), _preferences,
            tasks, new(tasks, _store), new(Mock.Of<ICleanerRegistrySessions>(), Mock.Of<ICleanerBackupVault>()),
            new WindowsDriverRemovalBackend(Mock.Of<IProcessExecutor>(), _store), mode.Object, Mock.Of<ILocalizationService>(), log);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task DamagedRecordsDoNotBlockHealthySnapshotsOrRetention(string corruptJson)
    {
        var expired = _store.SaveSnapshot([]);
        var before = new TimeMachineState(new(TimeMachineArea.Preference, "Theme"), "Theme", TimeMachineValue.From("Dark"));
        var expiredUndo = _store.PrepareUndo(before, TimeMachineValue.From("Light"));
        _clock.Now = _clock.Now.AddDays(3);
        var healthy = _store.SaveSnapshot([]);
        var broken = _store.SaveSnapshot([]);
        var brokenUndo = _store.PrepareUndo(before, TimeMachineValue.From("Light"));
        var damagedManifest = Manifest("Snapshots", broken.Id);
        var damagedUndoManifest = Manifest("Changes", brokenUndo.Id);
        await File.WriteAllTextAsync(damagedManifest, corruptJson);
        await File.WriteAllTextAsync(damagedUndoManifest, corruptJson);

        await _service.UpdateRetentionAsync(1);

        Assert.Equal(1, await _service.RetentionDaysAsync());
        Assert.Equal(healthy.Id, Assert.Single(await _service.ListSnapshotsAsync()).Id);
        Assert.False(File.Exists(Manifest("Snapshots", expired.Id)));
        Assert.False(File.Exists(Manifest("Changes", expiredUndo.Id)));
        Assert.Equal(corruptJson, await File.ReadAllTextAsync(damagedManifest));
        Assert.Equal(corruptJson, await File.ReadAllTextAsync(damagedUndoManifest));
        _log.Verify(log => log.LogWarning(It.Is<string>(message => message.Contains(broken.Id.ToString("N")))), Times.AtLeastOnce);
        _log.Verify(log => log.LogWarning(It.Is<string>(message => message.Contains(brokenUndo.Id.ToString("N")))), Times.AtLeastOnce);
    }

    private string Manifest(string kind, Guid id) =>
        Path.Combine(_directory.FullName, AccountSid, kind, id.ToString("N"), "item.json");

    [Fact]
    public async Task ReadingSelectedSnapshotReturnsSavedValuesAndHonorsAccountAndRetention()
    {
        var theme = new TimeMachineState(new(TimeMachineArea.Preference, "Theme"), "Theme", TimeMachineValue.From("Light"));
        var missing = new TimeMachineState(new(TimeMachineArea.Registry, @"HKCU\Software\Example", "Value"), "Example", TimeMachineValue.Missing);
        var snapshot = _store.SaveSnapshot([theme, missing]);
        _store.SaveSnapshot([]);

        var saved = await _service.ReadSnapshotAsync(snapshot.Id, default);
        Assert.Equal(snapshot.Id, saved.Id);
        Assert.Equal(new[] { theme, missing }, saved.States);

        _user.Setup(x => x.InteractiveUserSid).Returns("S-1-5-21-111-222-333-1002");
        await Assert.ThrowsAsync<IOException>(() => _service.ReadSnapshotAsync(snapshot.Id, default));
        _user.Setup(x => x.InteractiveUserSid).Returns(AccountSid);
        _clock.Now = _clock.Now.AddDays(TimeMachineRetention.DefaultDays);
        await Assert.ThrowsAsync<IOException>(() => _service.ReadSnapshotAsync(snapshot.Id, default));
        Assert.True(File.Exists(Manifest("Snapshots", snapshot.Id)));
        _history.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DeletingASnapshotPreservesNewestFirstOrderAndOtherRecoveryRecords()
    {
        var (oldest, _) = await _service.CaptureAsync(default);
        _clock.Now = _clock.Now.AddMinutes(1);
        var (selected, _) = await _service.CaptureAsync(default);
        _clock.Now = _clock.Now.AddMinutes(1);
        var (newest, _) = await _service.CaptureAsync(default);
        var undo = _store.PrepareUndo(selected.States[0], TimeMachineValue.Missing, selected.Id);
        Assert.Equal([newest.Id, selected.Id, oldest.Id], (await _service.ListSnapshotsAsync()).Select(snapshot => snapshot.Id));

        _user.Setup(x => x.InteractiveUserSid).Returns("S-1-5-21-111-222-333-1002");
        await _service.DeleteSnapshotAsync(selected.Id, default);
        Assert.True(File.Exists(Manifest("Snapshots", selected.Id)));
        _user.Setup(x => x.InteractiveUserSid).Returns(AccountSid);

        await _service.DeleteSnapshotAsync(selected.Id, default);

        Assert.False(File.Exists(Manifest("Snapshots", selected.Id)));
        await Assert.ThrowsAsync<IOException>(() => _service.CompareAsync(selected.Id, default));
        Assert.Equal([newest.Id, oldest.Id], (await _service.ListSnapshotsAsync()).Select(snapshot => snapshot.Id));
        Assert.Equal(undo, _store.ReadUndo(undo.Id, TimeMachineRetention.DefaultDays));
    }

    [Fact]
    public async Task ANewRetentionPolicyCannotCommitWhileAnOlderPruneIsDeletingRecords()
    {
        _store.SaveSnapshot([]);
        _clock.Now = _clock.Now.AddDays(3);
        var pruning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        _clock.BeforeNextRead = () =>
        {
            pruning.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Pruning was not released.");
        };

        var shorten = _service.UpdateRetentionAsync(1);
        Task extend = Task.CompletedTask;
        try
        {
            await pruning.Task.WaitAsync(TimeSpan.FromSeconds(10));
            extend = _service.UpdateRetentionAsync(90);
            Assert.Equal(1, await _service.RetentionDaysAsync());
            Assert.False(extend.IsCompleted);
        }
        finally
        {
            release.Set();
            await Task.WhenAll(shorten, extend);
        }
        Assert.Equal(90, await _service.RetentionDaysAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewRejectsADriverBackupThatIsMissingOrDamaged(bool hasDamagedInf)
    {
        var infHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("Expected contents"u8));
        var before = new TimeMachineState(new(TimeMachineArea.Driver, "oem1.inf"), "Driver",
            TimeMachineValue.From(new { InfPath = "driver.inf", InfHash = infHash, Files = new Dictionary<string, string> { ["driver.inf"] = infHash } }));
        var undo = _store.PrepareUndo(before, TimeMachineValue.Missing);
        if (hasDamagedInf)
        {
            var payload = _store.DriverPayload(undo.Id);
            Directory.CreateDirectory(payload);
            await File.WriteAllTextAsync(Path.Combine(payload, "driver.inf"), "Damaged");
        }
        var receipt = new ChangeHistoryEntry
        {
            Timestamp = _clock.Now, Kind = HistoryEntryKind.Maintenance, Name = "Driver",
            Topic = LogTopic.Drivers, RecoveryId = undo.Id, AccountSid = AccountSid
        };

        var row = Assert.Single(await _service.ReviewHistoryAsync([receipt], default));

        Assert.False(row.CanRestore);
        Assert.NotNull(row.Notice);
    }

    [Fact]
    public async Task SnapshotRestoresOnlyTheSelectedPreferenceAndRejectsChangesAfterReview()
    {
        var (snapshot, missing) = await _service.CaptureAsync(default);
        Assert.Equal(0, missing);
        await _preferences.SetPreferenceAsync("Theme", "Light");
        await _preferences.SetPreferenceAsync("Language", "en");
        await _preferences.SetPreferenceAsync("WindowWidth", 1100);

        var comparison = await _service.CompareAsync(snapshot.Id, default);
        Assert.Equal(2, comparison.Differences.Count);
        var theme = Assert.Single(comparison.Differences, x => x.Baseline.Target.Key == "Theme");
        var language = Assert.Single(comparison.Differences, x => x.Baseline.Target.Key == "Language");
        var restored = await _service.RestoreSnapshotAsync(snapshot.Id, [theme], default);
        Assert.False(restored.Cancelled);
        Assert.Null(Assert.Single(restored.Results).Error);
        Assert.Equal("Dark", await _preferences.GetPreferenceAsync("Theme", ""));
        Assert.Equal("en", await _preferences.GetPreferenceAsync("Language", ""));
        Assert.Equal(1100, await _preferences.GetPreferenceAsync("WindowWidth", 0));

        await _preferences.SetPreferenceAsync("Language", "de");
        var rejected = await _service.RestoreSnapshotAsync(snapshot.Id, [language], default);
        Assert.NotNull(Assert.Single(rejected.Results).Error);
        Assert.Equal("de", await _preferences.GetPreferenceAsync("Language", ""));
    }

    [Fact]
    public async Task RestoreOfASingleHistoryEntryClearsAnOriginallyMissingPreference()
    {
        Guid recoveryId = default;
        _history.Setup(x => x.LogMaintenance(LogTopic.Settings, "SoftwareAppsSortMode", It.IsAny<string>(),
                It.IsAny<Guid?>(), AccountSid))
            .Callback<LogTopic, string, string, Guid?, string?>((_, _, _, id, _) => recoveryId = id.GetValueOrDefault());
        Assert.True((await _preferences.SetPreferenceAsync("SoftwareAppsSortMode", "Name")).Success);
        Assert.NotEqual(Guid.Empty, recoveryId);
        var result = await _service.RestoreHistoryAsync([recoveryId], default);
        Assert.Null(Assert.Single(result.Results).Error);
        Assert.Equal("default", await _preferences.GetPreferenceAsync("SoftwareAppsSortMode", "default"));
    }

    [Fact]
    public async Task CancellingAfterOneWriteReportsThatWriteAndPreservesTheNextSetting()
    {
        using var cancel = new CancellationTokenSource();
        var (snapshot, _) = await _service.CaptureAsync(default);
        await _preferences.SetPreferenceAsync("Theme", "Light");
        await _preferences.SetPreferenceAsync("Language", "en");
        var comparison = await _service.CompareAsync(snapshot.Id, default);
        var theme = comparison.Differences.Single(difference => difference.Baseline.Target.Key == "Theme");
        var language = comparison.Differences.Single(difference => difference.Baseline.Target.Key == "Language");
        _history.Setup(history => history.LogMaintenance(LogTopic.Settings, "Theme", "App preference changed",
            It.IsAny<Guid?>(), AccountSid)).Callback(() => cancel.Cancel());

        var restored = await _service.RestoreSnapshotAsync(snapshot.Id, [theme, language], cancel.Token);

        Assert.True(restored.Cancelled);
        Assert.Equal(new TimeMachineRestoreResult("Theme", null), Assert.Single(restored.Results));
        Assert.Equal("Dark", await _preferences.GetPreferenceAsync("Theme", ""));
        Assert.Equal("en", await _preferences.GetPreferenceAsync("Language", ""));
    }

    [Fact]
    public async Task HistoryRestorePreservesCompletedReceiptsOnCancellation()
    {
        using var cancel = new CancellationTokenSource();
        var ids = new List<Guid>();
        _history.Setup(history => history.LogMaintenance(LogTopic.Settings, It.IsAny<string>(), "App preference changed",
            It.IsAny<Guid?>(), AccountSid)).Callback<LogTopic, string, string, Guid?, string?>((_, _, _, id, _) => ids.Add(id!.Value));
        await _preferences.SetPreferenceAsync("Theme", "Light");
        await _preferences.SetPreferenceAsync("Language", "en");
        _history.Setup(history => history.LogMaintenance(LogTopic.Settings, "Theme", "App preference changed",
            It.IsAny<Guid?>(), AccountSid)).Callback(() => cancel.Cancel());

        var restored = await _service.RestoreHistoryAsync(ids, cancel.Token);

        Assert.True(restored.Cancelled);
        Assert.Equal(new TimeMachineRestoreResult("Theme", null), Assert.Single(restored.Results));
        Assert.Equal("Dark", await _preferences.GetPreferenceAsync("Theme", ""));
        Assert.Equal("en", await _preferences.GetPreferenceAsync("Language", ""));
    }

    [Fact]
    public async Task HistoryPagesRetainLegacyAndSameAccountRecordsWithNoCrossAccountDisclosure()
    {
        const string file = "ChangeHistory.jsonl";
        _history.Setup(x => x.GetFilesAsync(default)).ReturnsAsync([new(file, _clock.Now)]);
        _history.Setup(x => x.ReadRecentAsync(file, null, 200, default)).ReturnsAsync(new LogPage<ChangeHistoryEntry>(
        [
            new() { Timestamp = _clock.Now, Kind = HistoryEntryKind.Maintenance, Name = "Other account", AccountSid = "S-1-5-21-555-222-333-1002" },
            new() { Timestamp = _clock.Now, Kind = HistoryEntryKind.Maintenance, Name = "Deleted file", AccountSid = AccountSid },
            new() { Timestamp = _clock.Now, Kind = HistoryEntryKind.SettingAction, Name = "Legacy change" }
        ], 120, true));
        var page = await _service.ReadHistoryAsync(null, default);
        Assert.Equal(120L, page.NextOffset);
        Assert.Equal(new[] { "Deleted file", "Legacy change" }, page.Entries.Select(x => x.Name));
        var details = await _service.ReviewHistoryAsync(page.Entries, default);
        Assert.All(details, entry => Assert.False(entry.CanRestore));
    }

    [Fact]
    public async Task DayReviewKeepsEntriesAfterAnOutOfOrderPageAndFiltersOtherAccounts()
    {
        const string file = "ChangeHistory.jsonl";
        var sameDay = new ChangeHistoryEntry
        {
            Timestamp = _clock.Now, Kind = HistoryEntryKind.Maintenance, Name = "Same local day", AccountSid = AccountSid
        };
        var previousDay = sameDay with { Timestamp = _clock.Now.AddDays(-2), Name = "Earlier calendar date" };
        var anotherAccount = sameDay with { AccountSid = "S-1-5-21-555-222-333-1002", Name = "Other account" };
        _history.Setup(source => source.GetFilesAsync(default)).ReturnsAsync([new(file, _clock.Now)]);
        _history.Setup(source => source.ReadRecentAsync(file, 120, 200, default))
            .ReturnsAsync(new LogPage<ChangeHistoryEntry>([previousDay], 60, true, 1));
        _history.Setup(source => source.ReadRecentAsync(file, 60, 200, default))
            .ReturnsAsync(new LogPage<ChangeHistoryEntry>([sameDay, anotherAccount], 0, false));

        var (day, unreadable) = await _service.ReadHistoryDayRemainderAsync(TimeMachineService.GetHistoryDay(sameDay), 120, default);

        Assert.Same(sameDay, Assert.Single(day));
        Assert.Equal(1, unreadable);
        _history.Verify(source => source.GetFilesAsync(default), Times.Once);
    }

    [Fact]
    public async Task RetentionDefaultsToTwentyEightDaysAndPrunesPayloadsWithoutDeletingReceipts()
    {
        Assert.Equal(28, await _service.RetentionDaysAsync());
        var snapshot = _store.SaveSnapshot([]);
        var entry = _store.PrepareUndo(new(new(TimeMachineArea.Driver, "oem1.inf"), "Driver", TimeMachineValue.From(1)),
            TimeMachineValue.Missing);
        var driverDirectory = _store.DriverPayload(entry.Id);
        Directory.CreateDirectory(driverDirectory);
        await File.WriteAllTextAsync(Path.Combine(driverDirectory, "package.inf"), "test package");
        _clock.Now = _clock.Now.AddDays(2);

        await _service.UpdateRetentionAsync(1);

        Assert.Equal(1, await _service.RetentionDaysAsync());
        Assert.Empty(await _service.ListSnapshotsAsync());
        Assert.Null(_store.ReadUndo(entry.Id, 1));
        Assert.False(Directory.Exists(driverDirectory));
        await Assert.ThrowsAsync<IOException>(() => _service.CompareAsync(snapshot.Id, default));
        _history.VerifyNoOtherCalls();
    }

    [Fact]
    public void RawRegistryTypeAndAbsenceSurvivePersistence()
    {
        var target = new TimeMachineTarget(TimeMachineArea.Registry, @"HKCU\Software\Example", "Value");
        var before = TimeMachineValue.From(new byte[] { 1, 2 }, (int)RegistryValueKind.Binary);
        var undo = _store.PrepareUndo(new(target, "Example", before), TimeMachineValue.Missing);
        var saved = Assert.IsType<TimeMachineUndo>(_store.ReadUndo(undo.Id, 28));
        Assert.Equal(target, saved.Before.Target);
        Assert.True(saved.Before.Value.SameAs(before));
        Assert.True(saved.After.SameAs(TimeMachineValue.Missing));
        Assert.False(saved.Before.Value.SameAs(TimeMachineValue.From(new byte[] { 1, 2 }, (int)RegistryValueKind.None)));
    }

    public void Dispose()
    {
        _service.Dispose();
        _directory.Delete(recursive: true);
    }

    private sealed class Clock : TimeProvider
    {
        public Action? BeforeNextRead;
        public DateTimeOffset Now { get; set; } = new(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow()
        {
            Interlocked.Exchange(ref BeforeNextRead, null)?.Invoke();
            return Now;
        }
    }
}
