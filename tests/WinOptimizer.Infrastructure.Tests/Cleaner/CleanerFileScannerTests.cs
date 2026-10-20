using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Cleaner.Models;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Infrastructure.Features.Cleaner;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Cleaner;

public sealed class CleanerFileScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "win-optimizer-cleaner-tests", Guid.NewGuid().ToString("N"));
    private readonly Mock<IInteractiveUserService> _user = new();
    private readonly Mock<IAppxPackageSource> _packages = new();
    private readonly CleanerFileScanner _scanner;
    public CleanerFileScannerTests()
    {
        Directory.CreateDirectory(_root);
        _user.Setup(x => x.GetInteractiveUserFolderPath(Environment.SpecialFolder.LocalApplicationData)).Returns(_root);
        _user.Setup(x => x.GetInteractiveUserFolderPath(Environment.SpecialFolder.ApplicationData)).Returns(Path.Combine(_root, "AppData", "Roaming"));
        _user.Setup(x => x.GetInteractiveUserFolderPath(Environment.SpecialFolder.UserProfile)).Returns(_root);
        _packages.Setup(x => x.GetInstalledPackageNamesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Installed.App" });
        var folders = new Mock<ICleanerSystemFolders>();
        folders.Setup(x => x.GetFolderPath(It.IsAny<Environment.SpecialFolder>()))
            .Returns((Environment.SpecialFolder folder) => Path.Combine(_root, "system", folder.ToString()));
        _scanner = new(new TestCleanerRegistrySessions(_user.Object, Mock.Of<ICleanerRegistryReader>(), Mock.Of<ICleanerRegistryEditor>()),
            _packages.Object, Mock.Of<ICleanerProcessMonitor>(), Mock.Of<ICleanerShortcutReader>(), folders.Object);
    }
    private string WriteAgedFile(string relative, bool old = true)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "test evidence");
        if (old) { File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30)); File.SetLastAccessTimeUtc(path, DateTime.UtcNow.AddDays(-30)); }
        return path;
    }
    [Fact]
    public async Task JunkScan_InventoriesTempFiles_AndOnlySelectedInstalledAppCaches()
    {
        var temp = WriteAgedFile(@"Temp\old.tmp");
        var recent = WriteAgedFile(@"Temp\recent.tmp", false);
        var cache = WriteAgedFile(@"Packages\Installed.App_publisher\LocalCache\old.tmp");
        WriteAgedFile(@"Packages\Removed.App_publisher\LocalCache\old.tmp");
        WriteAgedFile(@"Packages\Installed.App_publisher\LocalState\database.db");
        var options = new CleanerOptions { ApplicationCaches = ["Installed.App_publisher", "Removed.App_publisher"] };
        var scan = await _scanner.ScanAsync(CleanerArea.JunkFiles, options, default);
        scan.Findings.Select(x => x.Detail).Should().BeEquivalentTo(temp, recent, cache);
        scan.Findings.Single(x => x.Detail == recent).CanClean.Should().BeFalse();
        scan.Findings.Where(x => x.CanClean).Select(x => x.Detail).Should().BeEquivalentTo(temp, cache);
        scan.Findings.Single(x => x.Detail == temp).LabelKey.Should().Be("Cleaner_UserTemp");
        scan.Findings.Single(x => x.Detail == cache).LabelKey.Should().Be("Cleaner_ApplicationCaches");
        var applications = await _scanner.GetApplicationsAsync(default);
        applications.Select(x => x.Id).Should().Equal("Installed.App_publisher");
    }
    [Fact]
    public async Task FilesHeldByAnotherProcess_RemainVisibleAndBecomeActionableAfterRescan()
    {
        var path = WriteAgedFile(@"Temp\busy.tmp");
        using (var busy = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var scan = await _scanner.ScanAsync(CleanerArea.JunkFiles, new(), default);
            var finding = scan.Findings.Should().ContainSingle().Subject;
            finding.Detail.Should().Be(path);
            finding.Bytes.Should().Be(busy.Length);
            finding.CanClean.Should().BeFalse();
            finding.UnavailableReason.Should().NotBeNullOrWhiteSpace();
            scan.Notices.Should().ContainSingle().Which.Should().Contain("busy.tmp");
        }
        var rescan = await _scanner.ScanAsync(CleanerArea.JunkFiles, new(), default);
        rescan.Findings.Should().ContainSingle().Which.CanClean.Should().BeTrue();
        File.Exists(path).Should().BeTrue();
    }

    /// <summary>Reading an old cache before scanning must not reset its modification-based age.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task JunkAge_UsesModificationTime_WhilePreservingPreScanAccess(bool backup)
    {
        var path = WriteAgedFile(@"Temp\old.tmp");
        var recentAccess = DateTime.UtcNow.AddHours(-1);
        File.SetLastAccessTimeUtc(path, recentAccess);
        // Filesystems may store access times at a coarser resolution than DateTime.
        recentAccess = File.GetLastAccessTimeUtc(path);
        recentAccess.Should().BeAfter(DateTime.UtcNow.AddDays(-7));
        var recent = WriteAgedFile(@"Temp\recent.tmp", false);
        File.SetLastAccessTimeUtc(recent, DateTime.UtcNow.AddDays(-30));
        var originalWrite = File.GetLastWriteTimeUtc(path);
        var beforeScan = DateTime.UtcNow;

        var result = await _scanner.ScanAsync(CleanerArea.JunkFiles, new() { BackupBeforeCleaning = backup }, default);

        result.Findings.Should().HaveCount(2);
        var finding = result.Findings.Single(x => x.Detail == path);
        result.Findings.Single(x => x.Detail == recent).CanClean.Should().BeFalse();
        finding.Detail.Should().Be(path);
        finding.CanClean.Should().BeTrue();
        result.Notices.Should().BeEmpty();
        var evidence = (CleanerFileTarget)finding.Target;
        evidence.CutoffUtc.Should().BeOnOrAfter(beforeScan).And.BeOnOrBefore(DateTime.UtcNow);
        File.GetLastAccessTimeUtc(path).Should().Be(recentAccess);
        File.GetLastWriteTimeUtc(path).Should().Be(originalWrite);
        // Backup and deletion use the same evidence and must accept the already observed access time.
        using (CleanerFileDeletion.OpenVerified(evidence, deleting: false)) { }
        _scanner.Clean(evidence);
        File.Exists(path).Should().BeFalse();
        File.Exists(recent).Should().BeTrue();
    }

    [Fact]
    public async Task FileProgress_CountsEveryCandidate_IncludingFilesProtectedByAge()
    {
        WriteAgedFile(@"Temp\one.tmp");
        WriteAgedFile(@"Temp\two.tmp");
        WriteAgedFile(@"Temp\recent.tmp", false);
        List<CleanerProgress> reports = [];
        var progress = new Mock<IProgress<CleanerProgress>>();
        progress.Setup(x => x.Report(It.IsAny<CleanerProgress>())).Callback<CleanerProgress>(reports.Add);
        var result = await _scanner.ScanAsync(CleanerArea.JunkFiles, new(), default, progress.Object);
        result.Findings.Should().HaveCount(3);
        result.Findings.Count(x => x.CanClean).Should().Be(2);
        result.CheckedItems.Should().Be(3);
        reports[^1].Total.Should().Be(3);
        reports[^1].Completed.Should().Be(3);
    }

    [Fact]
    public async Task CancellingAfterTheFileInventory_StopsBeforeInspection()
    {
        var path = WriteAgedFile(@"Temp\one.tmp");
        using var cancellation = new CancellationTokenSource();
        var progress = new Mock<IProgress<CleanerProgress>>();
        progress.Setup(x => x.Report(It.IsAny<CleanerProgress>())).Callback<CleanerProgress>(value =>
        {
            if (value.Total > 0) cancellation.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _scanner.ScanAsync(CleanerArea.JunkFiles, new(), cancellation.Token, progress.Object));
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task OptionalCaches_OnlySelectRecognizedAgedCacheAndReportFiles()
    {
        var shader = WriteAgedFile(@"D3DSCache\old.bin");
        var thumbnail = WriteAgedFile(@"Microsoft\Windows\Explorer\thumbcache_96.db");
        WriteAgedFile(@"Microsoft\Windows\Explorer\iconcache.db");
        var report = WriteAgedFile(@"Microsoft\Windows\WER\ReportArchive\old.wer");
        var scan = await _scanner.ScanAsync(CleanerArea.JunkFiles, new() { UserTemp = false, ShaderCache = true, ThumbnailCache = true, ErrorReports = true }, default);
        scan.Findings.Select(x => x.Detail).Should().BeEquivalentTo(shader, thumbnail, report);
        scan.Findings.Select(x => x.LabelKey).Should().BeEquivalentTo("Cleaner_ShaderCache", "Cleaner_ThumbnailCache", "Cleaner_ErrorReports");
    }

    [Fact]
    public async Task ExpandedBrowserCleanup_PreservesPasswordsBookmarksAndFirefoxHistoryDatabase()
    {
        _user.Setup(x => x.GetInteractiveUserFolderPath(Environment.SpecialFolder.UserProfile)).Returns(_root);
        var chrome = WriteAgedFile(@"Google\Chrome\User Data\Default\Cache\old");
        var history = WriteAgedFile(@"Google\Chrome\User Data\Default\History");
        WriteAgedFile(@"Google\Chrome\User Data\Default\Login Data");
        WriteAgedFile(@"Google\Chrome\User Data\Default\Bookmarks");
        var firefox = WriteAgedFile(@"Mozilla\Firefox\Profiles\test.default\cache2\old");
        WriteAgedFile(@"AppData\Roaming\Mozilla\Firefox\Profiles\test.default\places.sqlite");
        var opera = WriteAgedFile(@"AppData\Roaming\Opera Software\Opera Stable\Cache\old");
        var brave = WriteAgedFile(@"BraveSoftware\Brave-Browser\User Data\Default\GPUCache\old");
        var recent = WriteAgedFile(@"AppData\Roaming\Microsoft\Windows\Recent\document.lnk");
        var scan = await _scanner.ScanAsync(CleanerArea.Privacy, new() { BrowserHistory = true }, default);
        scan.Findings.Select(x => x.Detail).Should().BeEquivalentTo(chrome, history, firefox, opera, brave, recent);
        scan.Findings.Single(x => x.Detail == recent).LabelKey.Should().Be("Cleaner_RecentFiles");
        scan.Findings.Single(x => x.Detail == history).LabelKey.Should().Be("Cleaner_BrowserHistory");
        scan.Findings.Where(x => x.Detail != history && x.Detail != recent).Should().OnlyContain(x => x.LabelKey.StartsWith("Cleaner_BrowserCache_"));
    }

    [Fact]
    public async Task BrowserCaches_CoverModernProfilesAndSharedCaches_WithoutSelectingSiteData()
    {
        const string chrome = @"Google\Chrome\User Data";
        WriteAgedFile(chrome + @"\Work\Preferences");
        var work = WriteAgedFile(chrome + @"\Work\Cache\Cache_Data\data_1");
        var gpu = WriteAgedFile(chrome + @"\Profile 2\DawnGraphiteCache\data_2");
        var shared = WriteAgedFile(chrome + @"\ShaderCache\data_3");
        var opera = WriteAgedFile(@"AppData\Roaming\Opera Software\Opera Stable\Default\Cache\Cache_Data\data_4");
        var localOpera = WriteAgedFile(@"Opera Software\Opera GX Stable\Default\Cache\Cache_Data\data_6");
        var offline = WriteAgedFile(chrome + @"\Work\Service Worker\CacheStorage\site\data_5");
        WriteAgedFile(chrome + @"\Work\Service Worker\Database\CURRENT");
        WriteAgedFile(chrome + @"\Work\IndexedDB\site\data");
        WriteAgedFile(chrome + @"\Work\Network\Cookies");
        WriteAgedFile(chrome + @"\NotAProfile\Cache\keep");
        var result = await _scanner.ScanAsync(CleanerArea.Privacy, new(), default);
        result.Findings.Select(x => x.Detail).Should().BeEquivalentTo(work, gpu, shared, opera, localOpera);
        result.Findings.Where(x => x.Detail != opera && x.Detail != localOpera).Should().OnlyContain(x => x.LabelKey == "Cleaner_BrowserCache_chrome");
        result.Findings.Should().OnlyHaveUniqueItems(x => x.Detail);
        var onlyOffline = await _scanner.ScanAsync(CleanerArea.Privacy,
            new() { BrowserCache = false, BrowserOfflineCache = true }, default);
        onlyOffline.Findings.Select(x => x.Detail).Should().Equal(offline);
    }

    /// <summary>Active browsers remain visible without making their files selectable or opening them for cleanup.</summary>
    [Theory]
    [InlineData("chrome", @"Google\Chrome\User Data\Default")]
    [InlineData("msedge", @"Microsoft\Edge\User Data\Profile 1")]
    [InlineData("firefox", @"Mozilla\Firefox\Profiles\test.default")]
    [InlineData("opera", @"AppData\Roaming\Opera Software\Opera Stable\Default")]
    public async Task RunningBrowser_IsInventoried_AndClosingItEnablesCleanupAfterRescan(string processName, string profile)
    {
        var cacheDirectory = processName == "firefox" ? "cache2" : "Cache";
        var path = WriteAgedFile(Path.Combine(profile, cacheDirectory, "entry"));
        var password = WriteAgedFile(Path.Combine(profile, "Login Data"));
        var processes = new Mock<ICleanerProcessMonitor>();
        processes.Setup(process => process.IsRunning(processName)).Returns(true);
        var scanner = new CleanerFileScanner(new TestCleanerRegistrySessions(_user.Object, Mock.Of<ICleanerRegistryReader>(), Mock.Of<ICleanerRegistryEditor>()),
            _packages.Object, processes.Object, Mock.Of<ICleanerShortcutReader>(), Mock.Of<ICleanerSystemFolders>());
        var originalAccess = File.GetLastAccessTimeUtc(path);
        var originalWrite = File.GetLastWriteTimeUtc(path);

        // An exclusive holder proves active-source discovery needs only file metadata.
        using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await scanner.ScanAsync(CleanerArea.Privacy, new(), default);
            var finding = result.Findings.Should().ContainSingle().Subject;
            finding.Detail.Should().Be(path);
            finding.Bytes.Should().Be(held.Length);
            finding.CanClean.Should().BeFalse();
            finding.UnavailableReason.Should().Contain(processName).And.Contain("scan again");
            result.CheckedItems.Should().Be(1);
            Assert.Throws<IOException>(() => scanner.Clean((CleanerFileTarget)finding.Target));
            Assert.Throws<IOException>(() => scanner.ValidateForRestore((CleanerFileTarget)finding.Target));
        }
        File.GetLastAccessTimeUtc(path).Should().Be(originalAccess);
        File.GetLastWriteTimeUtc(path).Should().Be(originalWrite);

        processes.Setup(process => process.IsRunning(processName)).Returns(false);
        var rescan = await scanner.ScanAsync(CleanerArea.Privacy, new(), default);
        var available = rescan.Findings.Should().ContainSingle().Subject;
        available.CanClean.Should().BeTrue();
        available.UnavailableReason.Should().BeNull();
        scanner.Clean((CleanerFileTarget)available.Target);
        File.Exists(path).Should().BeFalse();
        File.Exists(password).Should().BeTrue();
    }

    /// <summary>Locked privacy data is reported separately from files that can actually be selected.</summary>
    [Fact]
    public async Task LockedPrivacyFile_RemainsInInventory_AndBecomesActionableAfterRescan()
    {
        var path = WriteAgedFile(@"Microsoft\OneDrive\logs\Personal\busy.log");
        var available = WriteAgedFile(@"Microsoft\OneDrive\logs\Personal\available.log");
        using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await _scanner.ScanAsync(CleanerArea.Privacy, new(), default);
            result.Findings.Select(finding => finding.Detail).Should().BeEquivalentTo(path, available);
            var blocked = result.Findings.Single(finding => finding.Detail == path);
            blocked.CanClean.Should().BeFalse();
            blocked.UnavailableReason.Should().NotBeNullOrWhiteSpace();
            blocked.Bytes.Should().Be(held.Length);
            result.Findings.Single(finding => finding.Detail == available).CanClean.Should().BeTrue();
            result.CheckedItems.Should().Be(2);
            result.Notices.Should().ContainSingle().Which.Should().Contain("busy.log");
        }
        var rescan = await _scanner.ScanAsync(CleanerArea.Privacy, new(), default);
        rescan.Findings.Should().HaveCount(2).And.OnlyContain(finding => finding.CanClean && finding.UnavailableReason == null);
        rescan.Notices.Should().BeEmpty();
    }

    [Fact]
    public async Task PrivacyCaches_IncludeOnlyKnownDisposableData_AndKeepSessionsDocumentsAndDatabases()
    {
        var expected = new[]
        {
            @"Microsoft\OneDrive\logs\Personal\trace.log",
            @"AppData\Roaming\Telegram Desktop\tdata\user_data\cache\0\media",
            @"AppData\Roaming\Telegram Desktop\tdata\user_data#2\media_cache\0\video",
            @"Packages\Microsoft.WindowsStore_8wekyb3d8bbwe\LocalCache\cache",
            @"Packages\Microsoft.ZuneVideo_8wekyb3d8bbwe\AC\INetCache\cache",
            @"Packages\Microsoft.Windows.ContentDeliveryManager_cw5n1h2txyewy\LocalState\Assets\asset",
            @"AppData\LocalLow\Microsoft\CryptnetUrlCache\Content\certificate",
            @"AppData\LocalLow\Microsoft\CryptnetUrlCache\MetaData\metadata"
        }.Select(relative => WriteAgedFile(relative)).ToArray();
        var kept = new[]
        {
            @"Microsoft\OneDrive\settings\Personal\SyncEngineDatabase.db",
            @"OneDrive\Documents\document.docx",
            @"AppData\Roaming\Telegram Desktop\tdata\key_datas",
            @"AppData\Roaming\Telegram Desktop\tdata\user_data#invalid\cache\keep",
            @"Packages\Microsoft.WindowsStore_8wekyb3d8bbwe\LocalState\database.db",
            @"Packages\Microsoft.ZuneVideo_8wekyb3d8bbwe\LocalState\database.db",
            @"Packages\Microsoft.Windows.ContentDeliveryManager_cw5n1h2txyewy\LocalState\settings.dat",
            @"AppData\LocalLow\Microsoft\Crypto\private.key"
        }.Select(relative => WriteAgedFile(relative)).ToArray();
        var result = await _scanner.ScanAsync(CleanerArea.Privacy, new(), default);
        result.Notices.Should().BeEmpty();
        result.Findings.Select(x => x.Detail).Should().BeEquivalentTo(expected);
        result.Findings.Should().OnlyContain(x => ((CleanerFileTarget)x.Target).Profile != null);
        foreach (var finding in result.Findings) _scanner.Clean((CleanerFileTarget)finding.Target);
        expected.Should().OnlyContain(path => !File.Exists(path));
        kept.Should().OnlyContain(path => File.Exists(path));
        (await _scanner.ScanAsync(CleanerArea.Privacy,
            new() { ApplicationPrivacyCaches = false, CertificateCache = false }, default)).Findings.Should().BeEmpty();
    }

    [Fact]
    public async Task RunningTelegram_IsReported_AndStartingAfterScanBlocksCleanupAndRestore()
    {
        var path = WriteAgedFile(@"AppData\Roaming\Telegram Desktop\tdata\user_data\cache\media");
        var process = new Mock<ICleanerProcessMonitor>();
        var scanner = new CleanerFileScanner(new TestCleanerRegistrySessions(_user.Object, Mock.Of<ICleanerRegistryReader>(), Mock.Of<ICleanerRegistryEditor>()),
            _packages.Object, process.Object, Mock.Of<ICleanerShortcutReader>(), Mock.Of<ICleanerSystemFolders>());
        var scan = await scanner.ScanAsync(CleanerArea.Privacy, new(), default);
        var target = (CleanerFileTarget)scan.Findings.Single().Target;
        process.Setup(x => x.IsRunning("Telegram")).Returns(true);
        Assert.Throws<IOException>(() => scanner.Clean(target));
        Assert.Throws<IOException>(() => scanner.ValidateForRestore(target));
        File.Exists(path).Should().BeTrue();
        var busy = await scanner.ScanAsync(CleanerArea.Privacy, new(), default);
        var blocked = busy.Findings.Should().ContainSingle().Subject;
        blocked.Detail.Should().Be(path);
        blocked.CanClean.Should().BeFalse();
        blocked.UnavailableReason.Should().Contain("Telegram");
        busy.Notices.Should().ContainSingle().Which.Should().Contain("Close Telegram");
    }

    [Fact]
    public async Task JumpListsAndOfficeHistory_AreIndependentOptIns_AndOnlyIncludeRecognizedFiles()
    {
        var jump = WriteAgedFile(@"AppData\Roaming\Microsoft\Windows\Recent\AutomaticDestinations\app.automaticDestinations-ms");
        var custom = WriteAgedFile(@"AppData\Roaming\Microsoft\Windows\Recent\CustomDestinations\app.customDestinations-ms");
        var office = WriteAgedFile(@"AppData\Roaming\Microsoft\Office\Recent\document.lnk");
        WriteAgedFile(@"AppData\Roaming\Microsoft\Office\Recent\document.docx");
        WriteAgedFile(@"AppData\Roaming\Microsoft\Windows\Recent\AutomaticDestinations\unrelated.dat");
        (await _scanner.ScanAsync(CleanerArea.Privacy, new(), default)).Findings.Should().BeEmpty();
        var scan = await _scanner.ScanAsync(CleanerArea.Privacy, new() { JumpLists = true, ApplicationHistory = true }, default);
        scan.Findings.Select(x => x.Detail).Should().BeEquivalentTo(jump, custom, office);
        scan.Findings.Should().HaveCount(3);
    }

    [Fact]
    public async Task DefenderSupportLogs_ScanOnceWithNoAccounts_AndPreserveSecurityHistoryAndQuarantine()
    {
        var log = WriteAgedFile(@"Microsoft\Windows Defender\Support\MPLog-test.log");
        WriteAgedFile(@"Microsoft\Windows Defender\Scans\History\Service\DetectionHistory\keep");
        WriteAgedFile(@"Microsoft\Windows Defender\Quarantine\keep");
        WriteAgedFile(@"Microsoft\Windows Defender\Support\configuration.dat");
        var folders = new Mock<ICleanerSystemFolders>();
        folders.Setup(x => x.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)).Returns(_root);
        var scanner = new CleanerFileScanner(new TestCleanerRegistrySessions(_user.Object, Mock.Of<ICleanerRegistryReader>(), Mock.Of<ICleanerRegistryEditor>()),
            _packages.Object, Mock.Of<ICleanerProcessMonitor>(), Mock.Of<ICleanerShortcutReader>(), folders.Object);
        var result = await scanner.ScanAsync(CleanerArea.Privacy, new() { PrivacyAccountSids = [], DefenderLogs = true }, default);
        result.Findings.Should().ContainSingle().Which.Detail.Should().Be(log);
        result.Findings[0].LabelKey.Should().Be("Cleaner_DefenderLogs");
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    public async Task UnresolvedCommonDataFolder_IsReportedWithoutScanningARelativeDefenderPath(string path)
    {
        var folders = new Mock<ICleanerSystemFolders>();
        folders.Setup(x => x.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)).Returns(path);
        var scanner = new CleanerFileScanner(new TestCleanerRegistrySessions(_user.Object, Mock.Of<ICleanerRegistryReader>(), Mock.Of<ICleanerRegistryEditor>()),
            _packages.Object, Mock.Of<ICleanerProcessMonitor>(), Mock.Of<ICleanerShortcutReader>(), folders.Object);
        var result = await scanner.ScanAsync(CleanerArea.Privacy, new() { PrivacyAccountSids = [], DefenderLogs = true }, default);
        result.Findings.Should().BeEmpty();
        result.Notices.Should().ContainSingle().Which.Should().Contain("Microsoft Defender support logs");
    }

    [Fact]
    public async Task BrowserStartingAfterScan_BlocksCleanup()
    {
        _user.Setup(x => x.GetInteractiveUserFolderPath(Environment.SpecialFolder.UserProfile)).Returns(_root);
        var processes = new Mock<ICleanerProcessMonitor>();
        var scanner = new CleanerFileScanner(new TestCleanerRegistrySessions(_user.Object, Mock.Of<ICleanerRegistryReader>(), Mock.Of<ICleanerRegistryEditor>()),
            _packages.Object, processes.Object, Mock.Of<ICleanerShortcutReader>(), Mock.Of<ICleanerSystemFolders>());
        var path = WriteAgedFile(@"Google\Chrome\User Data\Default\Cache\old");
        var scan = await scanner.ScanAsync(CleanerArea.Privacy, new() { RecentFiles = false }, default);
        processes.Setup(x => x.IsRunning("chrome")).Returns(true);
        Assert.Throws<IOException>(() => scanner.Clean((CleanerFileTarget)scan.Findings.Single().Target));
        File.Exists(path).Should().BeTrue();
    }

    /// <summary>Recent-document repair tests actual missing targets without using the privacy or junk-age options.</summary>
    [Fact]
    public async Task RecentShortcuts_FindMissingFilesAndFolders_AndRecheckTargetsBeforeRemoval()
    {
        const string recent = @"AppData\Roaming\Microsoft\Windows\Recent";
        var missingFile = Path.Combine(_root, "missing-document.txt");
        var missingFolder = Path.Combine(_root, "missing-folder");
        var existingFile = WriteAgedFile("document.txt");
        var existingFolder = Path.Combine(_root, "existing-folder");
        Directory.CreateDirectory(existingFolder);
        var recentLink = WriteAgedFile(Path.Combine(recent, "missing-file.lnk"));
        File.SetLastWriteTimeUtc(recentLink, DateTime.UtcNow.AddHours(-1));
        File.SetLastAccessTimeUtc(recentLink, DateTime.UtcNow.AddHours(-1));
        var targets = new Dictionary<string, string>
        {
            [recentLink] = missingFile,
            [WriteAgedFile(Path.Combine(recent, "missing-folder.lnk"))] = missingFolder,
            [WriteAgedFile(Path.Combine(recent, "valid-file.lnk"))] = existingFile,
            [WriteAgedFile(Path.Combine(recent, "valid-folder.lnk"))] = existingFolder,
            [WriteAgedFile(Path.Combine(recent, "network.lnk"))] = @"\\offline-server\share\document.txt",
            [WriteAgedFile(Path.Combine(recent, "unresolved.lnk"))] = @"%UNKNOWN_SHORTCUT_ROOT%\document.txt",
            [WriteAgedFile(Path.Combine(recent, "shell.lnk"))] = ""
        };
        WriteAgedFile(Path.Combine(recent, "ordinary-document.txt"));
        var reader = new Mock<ICleanerShortcutReader>();
        reader.Setup(value => value.ReadTarget(It.IsAny<string>())).Returns((string path) => targets[path]);
        var scanner = new CleanerFileScanner(new TestCleanerRegistrySessions(_user.Object, Mock.Of<ICleanerRegistryReader>(), Mock.Of<ICleanerRegistryEditor>()),
            _packages.Object, Mock.Of<ICleanerProcessMonitor>(), reader.Object, Mock.Of<ICleanerSystemFolders>());
        var options = new CleanerOptions { DesktopShortcuts = false, StartMenuShortcuts = false, RecentFiles = false, MinimumAgeDays = 365 };

        var result = await scanner.ScanAsync(CleanerArea.Shortcuts, options, default);
        result.Findings.Should().HaveCount(2).And.OnlyContain(finding => finding.LabelKey == "Cleaner_RecentFiles" && finding.CanClean);
        result.Findings.Select(finding => ((CleanerFileTarget)finding.Target).ShortcutTarget).Should().BeEquivalentTo(missingFile, missingFolder);
        result.CheckedItems.Should().Be(targets.Count);
        result.Notices.Should().BeEmpty();
        (await scanner.ScanAsync(CleanerArea.Shortcuts, options with { RecentShortcuts = false }, default)).Findings.Should().BeEmpty();

        File.WriteAllText(missingFile, "restored document");
        Directory.CreateDirectory(missingFolder);
        foreach (var finding in result.Findings)
        {
            Assert.Throws<IOException>(() => scanner.Clean((CleanerFileTarget)finding.Target));
            File.Exists(finding.Detail).Should().BeTrue();
        }
        (await scanner.ScanAsync(CleanerArea.Shortcuts, options, default)).Findings.Should().BeEmpty();
        File.Exists(existingFile).Should().BeTrue();
    }

    [Fact]
    public async Task Cleanup_DeletesScannedFile_AndRejectsModifiedFile()
    {
        var path = WriteAgedFile(@"Temp\old.tmp");
        var scan = await _scanner.ScanAsync(CleanerArea.JunkFiles, new(), default);
        var evidence = (CleanerFileTarget)scan.Findings.Single().Target;
        File.WriteAllText(path, "changed after scan");
        Assert.Throws<IOException>(() => _scanner.Clean(evidence));
        File.Exists(path).Should().BeTrue();
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30)); File.SetLastAccessTimeUtc(path, DateTime.UtcNow.AddDays(-30));
        scan = await _scanner.ScanAsync(CleanerArea.JunkFiles, new(), default);
        _scanner.Clean((CleanerFileTarget)scan.Findings.Single().Target);
        File.Exists(path).Should().BeFalse();
    }
    [Fact]
    public async Task JunkAccessAfterScanStillBlocksBackupAndRemoval()
    {
        var path = WriteAgedFile(@"Temp\old.tmp");
        var result = await _scanner.ScanAsync(CleanerArea.JunkFiles, new(), default);
        var evidence = Assert.IsType<CleanerFileTarget>(Assert.Single(result.Findings).Target);
        File.SetLastAccessTimeUtc(path, DateTime.UtcNow.AddDays(1));

        Assert.Throws<IOException>(() => _scanner.Clean(evidence));
        Assert.Throws<IOException>(() => CleanerFileDeletion.OpenVerified(evidence, deleting: false));
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void Cleanup_RejectsSiblingDirectoryAndTraversal()
    {
        var path = WriteAgedFile(@"TempOther\keep.tmp");
        var info = new FileInfo(path);
        var evidence = new CleanerFileTarget(Path.Combine(_root, "Temp"), path, info.Length, info.LastWriteTimeUtc, DateTime.UtcNow);
        Assert.Throws<IOException>(() => _scanner.Clean(evidence));
        File.Exists(path).Should().BeTrue();
    }
    [Theory]
    [InlineData(null, @"D:\Users\Interactive\Desktop")]
    [InlineData(@"%USERPROFILE%\OneDrive\Desktop", @"D:\Users\Interactive\OneDrive\Desktop")]
    [InlineData(@"E:\RedirectedDesktop", @"E:\RedirectedDesktop")]
    public void InteractiveDesktop_UsesInteractiveProfileAndConfiguredRedirection(string? configured, string expected) =>
        WinOptimizer.Infrastructure.Features.Common.Services.InteractiveUserService.ResolveInteractiveDesktopPath(@"D:\Users\Interactive", configured).Should().Be(expected);

    [Theory]
    [InlineData("%UNRESOLVED_USER_VARIABLE%\\Desktop")]
    [InlineData("relative\\Desktop")]
    public void InteractiveDesktop_RejectsUnresolvedPaths(string configured) =>
        Assert.Throws<IOException>(() => WinOptimizer.Infrastructure.Features.Common.Services.InteractiveUserService.ResolveInteractiveDesktopPath(@"D:\Users\Interactive", configured));

    public void Dispose()
    {
        var full = Path.GetFullPath(_root);
        if (!full.StartsWith(Path.Combine(Path.GetTempPath(), "win-optimizer-cleaner-tests") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid test directory.");
        Directory.Delete(full, recursive: true);
    }
}
