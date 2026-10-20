using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Cleaner.Models;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Infrastructure.Features.Cleaner;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Cleaner;

/// <summary>Exercises junk source rules against synthetic Windows and account directories.</summary>
public sealed class CleanerJunkScanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cleaner-junk-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<ICleanerSystemFolders> _folders = new();
    private readonly CleanerFileScanner _scanner;

    public CleanerJunkScanTests()
    {
        var user = new Mock<IInteractiveUserService>();
        user.Setup(x => x.GetInteractiveUserFolderPath(It.IsAny<Environment.SpecialFolder>()))
            .Returns((Environment.SpecialFolder folder) => Path.Combine(_root, "user", folder.ToString()));
        _folders.Setup(x => x.GetFolderPath(It.IsAny<Environment.SpecialFolder>()))
            .Returns((Environment.SpecialFolder folder) => Path.Combine(_root, "system", folder.ToString()));
        _scanner = new(new TestCleanerRegistrySessions(user.Object, Mock.Of<ICleanerRegistryReader>(), Mock.Of<ICleanerRegistryEditor>()),
            Mock.Of<IAppxPackageSource>(), Mock.Of<ICleanerProcessMonitor>(), Mock.Of<ICleanerShortcutReader>(), _folders.Object);
    }

    /// <summary>Creates synthetic evidence with deterministic age and access metadata.</summary>
    private string Write(string owner, Environment.SpecialFolder folder, string relative, int ageDays = 30)
    {
        var path = Path.Combine(_root, owner, folder.ToString(), relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "synthetic junk evidence");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-ageDays).AddMinutes(-1));
        File.SetLastAccessTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
        return path;
    }

    /// <summary>System discovery uses specific diagnostic formats and stays out of installation and event data.</summary>
    [Fact]
    public async Task WindowsDiagnostics_FindLogsAndDumps_WithoutSelectingOtherSystemFiles()
    {
        var logs = new[] { @"Logs\CBS\CBS.log", @"Logs\CBS\CbsPersist_20260101.cab", @"Logs\DISM\dism.log.bak",
            @"Logs\WindowsUpdate\trace.ETL", @"Logs\Nested\trace.LOG", "setupact.log", @"Panther\UnattendGC\setupact.log", @"INF\setupapi.dev.log" }
            .Select(relative => Write("system", Environment.SpecialFolder.Windows, relative)).ToArray();
        var dumps = new[] { "MEMORY.DMP", @"Minidump\crash.dmp", @"LiveKernelReports\WATCHDOG\kernel.dmp" }
            .Select(relative => Write("system", Environment.SpecialFolder.Windows, relative)).ToArray();
        var kept = new[] { @"Logs\CBS\component.cab", @"Logs\CBS\settings.xml", @"Logs\events.evtx", @"Panther\unattend.xml",
            @"INF\driver.inf", @"INF\subfolder\setupapi.dev.log", @"Installer\needed.msi", @"WinSxS\component.log",
            @"System32\winevt\Logs\System.evtx", @"Prefetch\app.pf", @"Minidump\keep.txt", "important.dll" }
            .Select(relative => Write("system", Environment.SpecialFolder.Windows, relative)).ToArray();

        var result = await _scanner.ScanAsync(CleanerArea.JunkFiles, new() { JunkAccountSids = [] }, default);

        result.Findings.Select(x => x.Detail).Should().BeEquivalentTo(logs.Concat(dumps));
        result.Findings.Should().OnlyHaveUniqueItems(x => x.Detail);
        result.Findings.Should().OnlyContain(x => x.CanClean && ((CleanerFileTarget)x.Target).Profile == null);
        result.Findings.Where(x => logs.Contains(x.Detail)).Should().OnlyContain(x => x.LabelKey == "Cleaner_WindowsLogs");
        result.Findings.Where(x => dumps.Contains(x.Detail)).Should().OnlyContain(x => x.LabelKey == "Cleaner_CrashDumps");
        result.Notices.Should().BeEmpty();
        foreach (var finding in result.Findings) _scanner.Clean((CleanerFileTarget)finding.Target);
        kept.Should().OnlyContain(path => File.Exists(path));
    }

    /// <summary>WER stores exist independently for user accounts and the machine.</summary>
    [Fact]
    public async Task ErrorReports_CoverUserAndMachineStores_AndRespectEmptyAccountSelection()
    {
        var user = new[] { "ReportArchive", "ReportQueue", "Temp" }
            .Select(store => Write("user", Environment.SpecialFolder.LocalApplicationData, @"Microsoft\Windows\WER\" + store + @"\report\Report.wer")).ToArray();
        var common = new[] { "ReportArchive", "ReportQueue", "Temp" }
            .Select(store => Write("system", Environment.SpecialFolder.CommonApplicationData, @"Microsoft\Windows\WER\" + store + @"\report\Report.wer")).ToArray();
        Write("system", Environment.SpecialFolder.CommonApplicationData, @"Microsoft\Windows\WER\settings.dat");

        var all = await _scanner.ScanAsync(CleanerArea.JunkFiles, new(), default);
        all.Findings.Select(x => x.Detail).Should().BeEquivalentTo(user.Concat(common));
        all.Findings.Should().OnlyContain(x => x.LabelKey == "Cleaner_ErrorReports");
        all.Findings.Where(x => user.Contains(x.Detail)).Should().OnlyContain(x => ((CleanerFileTarget)x.Target).Profile != null);
        var machineOnly = await _scanner.ScanAsync(CleanerArea.JunkFiles, new() { JunkAccountSids = [] }, default);
        machineOnly.Findings.Select(x => x.Detail).Should().BeEquivalentTo(common);
        (await _scanner.ScanAsync(CleanerArea.JunkFiles, new() { ErrorReports = false }, default)).Findings.Should().BeEmpty();
    }

    /// <summary>Only known cache files are selected; persistent browser and package state stays outside the rules.</summary>
    [Fact]
    public async Task ProfileCaches_CoverInternetThumbnailsShadersAndDumps_WithoutSelectingPersistentState()
    {
        var expected = new[] { @"Microsoft\Windows\INetCache\IE\cache\page.htm", @"Microsoft\Windows\Explorer\thumbcache_96.db",
            @"D3DSCache\shader.bin", @"CrashDumps\app.exe.123.dmp" }
            .Select(relative => Write("user", Environment.SpecialFolder.LocalApplicationData, relative)).ToArray();
        var kept = new[] { @"Microsoft\Windows\WebCache\WebCacheV01.dat", @"Microsoft\Windows\INetCookies\cookie.dat",
            @"Microsoft\Windows\Explorer\iconcache.db", @"CrashDumps\keep.txt", @"Packages\App_publisher\LocalState\database.db" }
            .Select(relative => Write("user", Environment.SpecialFolder.LocalApplicationData, relative)).ToArray();

        var result = await _scanner.ScanAsync(CleanerArea.JunkFiles, new(), default);
        result.Findings.Select(x => x.Detail).Should().BeEquivalentTo(expected);
        result.Findings.Should().OnlyContain(x => ((CleanerFileTarget)x.Target).Profile != null);
        foreach (var finding in result.Findings) _scanner.Clean((CleanerFileTarget)finding.Target);
        kept.Should().OnlyContain(path => File.Exists(path));
    }

    /// <summary>Each category is independently disabled without changing other settings.</summary>
    [Theory]
    [InlineData("logs")]
    [InlineData("dumps")]
    [InlineData("internet")]
    public async Task NewCategorySwitches_ExcludeOnlyTheirOwnFiles(string category)
    {
        var log = Write("system", Environment.SpecialFolder.Windows, @"Logs\DISM\dism.log");
        var dump = Write("user", Environment.SpecialFolder.LocalApplicationData, @"CrashDumps\app.dmp");
        var cache = Write("user", Environment.SpecialFolder.LocalApplicationData, @"Microsoft\Windows\INetCache\IE\cache.dat");
        var options = new CleanerOptions { WindowsLogs = category != "logs", CrashDumps = category != "dumps", InternetCache = category != "internet" };
        var excluded = category switch { "logs" => log, "dumps" => dump, _ => cache };
        var result = await _scanner.ScanAsync(CleanerArea.JunkFiles, options, default);
        result.Findings.Select(x => x.Detail).Should().BeEquivalentTo(new[] { log, dump, cache }.Where(path => path != excluded));
    }

    /// <summary>Recent and locked files stay visible, while zero age explicitly enables recent inactive files.</summary>
    [Fact]
    public async Task RecentDiagnostics_AreInventoriedAndAgeProtected_AndLockedFilesStayDisabledAtZeroAge()
    {
        var recent = Write("system", Environment.SpecialFolder.Windows, @"Logs\DISM\dism.log", ageDays: 0);
        var busyPath = Write("system", Environment.SpecialFolder.CommonApplicationData, @"Microsoft\Windows\WER\ReportQueue\report.wer");
        using (var busy = new FileStream(busyPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await _scanner.ScanAsync(CleanerArea.JunkFiles, new(), default);
            result.Findings.Should().HaveCount(2).And.OnlyContain(x => !x.CanClean && x.UnavailableReason != null);
            result.Findings.Single(x => x.Detail == recent).UnavailableReason.Should().Contain("7 days");
            var zeroAge = await _scanner.ScanAsync(CleanerArea.JunkFiles, new() { MinimumAgeDays = 0 }, default);
            zeroAge.Findings.Single(x => x.Detail == recent).CanClean.Should().BeTrue();
            zeroAge.Findings.Single(x => x.Detail == busyPath).CanClean.Should().BeFalse();
        }
        File.Exists(recent).Should().BeTrue();
        File.Exists(busyPath).Should().BeTrue();
    }

    /// <summary>An unresolved common location must not suppress valid Windows or user sources.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    public async Task InvalidSystemLocation_ReportsCoverageGap_AndContinuesOtherSources(string unresolved)
    {
        var log = Write("system", Environment.SpecialFolder.Windows, @"Logs\DISM\dism.log");
        var report = Write("user", Environment.SpecialFolder.LocalApplicationData, @"Microsoft\Windows\WER\ReportArchive\Report.wer");
        _folders.Setup(x => x.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)).Returns(unresolved);
        var result = await _scanner.ScanAsync(CleanerArea.JunkFiles, new(), default);
        result.Findings.Select(x => x.Detail).Should().BeEquivalentTo(log, report);
        result.Notices.Should().OnlyContain(x => x.Contains("Cleaner_ErrorReports"));
        result.Notices.Should().NotBeEmpty();
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
