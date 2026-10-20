using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Cleaner.Models;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Infrastructure.Features.Cleaner;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Cleaner;

/// <summary>Uses registered-profile fakes and isolated files to exercise complete multi-account cleanup paths.</summary>
public sealed class CleanerAccountScopeTests : IDisposable
{
    private const string First = "S-1-5-21-1-2-3-1001", Second = "S-1-5-21-1-2-3-1002";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cleaner-account-scopes-" + Guid.NewGuid().ToString("N"));
    private readonly TestCleanerRegistrySessions _accounts;
    private readonly Mock<IAppxPackageSource> _packages = new();
    private readonly Mock<ICleanerProcessMonitor> _processes = new();
    private readonly Mock<ICleanerShortcutReader> _shortcuts = new();
    private readonly Mock<IRegistryHiveOperations> _native = new();
    private readonly CleanerFileScanner _files;

    public CleanerAccountScopeTests()
    {
        var user = new Mock<IInteractiveUserService>();
        user.Setup(user => user.GetInteractiveUserFolderPath(It.IsAny<Environment.SpecialFolder>())).Returns(Path.Combine(_root, "interactive"));
        _accounts = new(user.Object, Mock.Of<ICleanerRegistryReader>(), Mock.Of<ICleanerRegistryEditor>());
        _accounts.Profiles.Add(First, Path.Combine(_root, "first"));
        _accounts.Profiles.Add(Second, Path.Combine(_root, "second"));
        var system = new Mock<ICleanerSystemFolders>();
        system.Setup(system => system.GetFolderPath(It.IsAny<Environment.SpecialFolder>()))
            .Returns((Environment.SpecialFolder folder) => Path.Combine(_root, "system", folder.ToString()));
        _packages.Setup(packages => packages.GetInstalledPackageNamesForUserAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Selected.App" });
        _files = new(_accounts, _packages.Object, _processes.Object, _shortcuts.Object, system.Object);
        Directory.CreateDirectory(_root);
    }

    private string Write(string path, int length = 8)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30));
        File.SetLastAccessTimeUtc(path, DateTime.UtcNow.AddDays(-30));
        return path;
    }
    private string Local(string sid, string relative) => Path.Combine(_accounts.Profiles[sid], @"AppData\Local", relative);
    private string Roaming(string sid, string relative) => Path.Combine(_accounts.Profiles[sid], @"AppData\Roaming", relative);

    [Fact]
    public async Task PrivacyScansOnlySelectedProfiles_AndKeepsAccountEvidence()
    {
        Write(Local(First, @"Google\Chrome\User Data\Default\Cache\unselected"));
        var cache = Write(Local(Second, @"Google\Chrome\User Data\Default\Cache\selected"));
        var history = Write(Local(Second, @"Google\Chrome\User Data\Default\History"));
        var recent = Write(Roaming(Second, @"Microsoft\Windows\Recent\recent.lnk"));
        Write(Local(Second, @"Google\Chrome\User Data\Default\Login Data"));

        var result = await _files.ScanAsync(CleanerArea.Privacy, new() { PrivacyAccountSids = [Second, Second], BrowserHistory = true }, default);

        result.Findings.Select(finding => finding.Detail).Should().BeEquivalentTo(cache, history, recent);
        result.Findings.Should().OnlyContain(finding => ((CleanerFileTarget)finding.Target).Profile!.AccountSid == Second);
        _accounts.Opened.Should().Equal(Second);
        _accounts.Closed.Should().Equal(Second);
        _files.Clean((CleanerFileTarget)result.Findings.Single(finding => finding.Detail == recent).Target);
        File.Exists(recent).Should().BeFalse();
        File.Exists(cache).Should().BeTrue();
    }

    [Fact]
    public async Task JunkUsesEachAccountsPackageInventory_AndDeduplicatesApplicationChoices()
    {
        foreach (var sid in new[] { First, Second })
        {
            Write(Local(sid, @"Packages\Selected.App_publisher\LocalCache\old"));
            Write(Local(sid, @"Packages\Selected.App_publisher\LocalState\database"));
            Write(Local(sid, @"Packages\Uninstalled.App_publisher\LocalCache\keep"));
            Write(Local(sid, @"Temp\old"));
        }
        var options = new CleanerOptions { JunkAccountSids = [First, Second, First], ApplicationCaches = ["Selected.App_publisher", "Uninstalled.App_publisher"] };
        var inventory = await _files.GetApplicationsAsync(options, default);
        inventory.Applications.Should().ContainSingle().Which.Id.Should().Be("Selected.App_publisher");
        inventory.Notices.Should().BeEmpty();
        var result = await _files.ScanAsync(CleanerArea.JunkFiles, options, default);
        result.Findings.Should().HaveCount(4).And.OnlyContain(finding => !finding.Detail.Contains("LocalState") && !finding.Detail.Contains("Uninstalled"));
        result.Findings.Select(finding => ((CleanerFileTarget)finding.Target).Profile!.AccountSid).Should().BeEquivalentTo(First, First, Second, Second);
        _packages.Verify(packages => packages.GetInstalledPackageNamesForUserAsync(First, It.IsAny<CancellationToken>()), Times.Exactly(2));
        _packages.Verify(packages => packages.GetInstalledPackageNamesForUserAsync(Second, It.IsAny<CancellationToken>()), Times.Exactly(2));
        _packages.Verify(packages => packages.GetInstalledPackageNamesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EmptySelectionsSkipUserData_AndStillScanEnabledCommonLocationsOnce()
    {
        Write(Local(First, @"Temp\keep"));
        var common = Write(Path.Combine(_root, "system", "Windows", "Temp", "common.tmp"));
        var options = new CleanerOptions { JunkAccountSids = [], PrivacyAccountSids = [], ShortcutAccountSids = [], WindowsTemp = true };
        var junk = await _files.ScanAsync(CleanerArea.JunkFiles, options, default);
        junk.Findings.Should().ContainSingle().Which.Detail.Should().Be(common);
        ((CleanerFileTarget)junk.Findings[0].Target).Profile.Should().BeNull();
        (await _files.ScanAsync(CleanerArea.Privacy, options, default)).Findings.Should().BeEmpty();
        (await _files.GetApplicationsAsync(options, default)).Applications.Should().BeEmpty();
        _accounts.Opened.Should().BeEmpty();
        _packages.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(CleanerArea.JunkFiles)]
    [InlineData(CleanerArea.Shortcuts)]
    public async Task SharedLocationsRetainTheirOrderAmongProfileLocations(CleanerArea area)
    {
        string[] expected;
        if (area == CleanerArea.JunkFiles)
            expected = [Write(Local(First, @"Temp\old")), Write(Path.Combine(_root, @"system\Windows\Temp\old")), Write(Local(First, @"D3DSCache\old"))];
        else
        {
            expected = [Write(Path.Combine(_accounts.Profiles[First], @"Desktop\user.lnk")),
                Write(Path.Combine(_root, @"system\CommonDesktopDirectory\common.lnk")),
                Write(Roaming(First, @"Microsoft\Windows\Start Menu\Programs\user.lnk")),
                Write(Path.Combine(_root, @"system\CommonPrograms\common.lnk"))];
            _shortcuts.Setup(reader => reader.ReadTarget(It.IsAny<string>())).Returns(Path.Combine(_root, "missing.exe"));
        }
        var options = new CleanerOptions { JunkAccountSids = [First], ShortcutAccountSids = [First], WindowsTemp = true, ShaderCache = true };
        var report = await _files.ScanAsync(area, options, default);
        report.Findings.Select(finding => finding.Detail).Should().Equal(expected);
    }

    [Fact]
    public async Task ShortcutsUseSelectedDesktopProgramsAndRecent_AndResolveTargetsInThatProfile()
    {
        var valid = Write(Path.Combine(_accounts.Profiles[Second], "existing.exe"));
        var desktop = Write(Path.Combine(_accounts.Profiles[Second], @"Desktop\valid.lnk"));
        var redirected = Path.Combine(_root, "redirected-programs");
        _accounts.Folders[(Second, Environment.SpecialFolder.Programs)] = redirected;
        var broken = Write(Path.Combine(redirected, "broken.lnk"));
        var recent = Write(Roaming(Second, @"Microsoft\Windows\Recent\document.lnk"));
        var common = Write(Path.Combine(_root, "system", "CommonDesktopDirectory", "common.lnk"));
        Write(Path.Combine(_accounts.Profiles[First], @"Desktop\unselected.lnk"));
        var unselectedRecent = Write(Roaming(First, @"Microsoft\Windows\Recent\unselected.lnk"));
        _shortcuts.Setup(reader => reader.ReadTarget(desktop)).Returns(@"%USERPROFILE%\existing.exe");
        _shortcuts.Setup(reader => reader.ReadTarget(broken)).Returns(@"%USERPROFILE%\missing.exe");
        _shortcuts.Setup(reader => reader.ReadTarget(recent)).Returns(@"%USERPROFILE%\missing-document.txt");
        _shortcuts.Setup(reader => reader.ReadTarget(common)).Returns(Path.Combine(_root, "common-missing.exe"));

        var result = await _files.ScanAsync(CleanerArea.Shortcuts, new() { ShortcutAccountSids = [Second, Second] }, default);

        result.Findings.Select(finding => finding.Detail).Should().BeEquivalentTo(broken, common, recent);
        var recentTarget = (CleanerFileTarget)result.Findings.Single(finding => finding.Detail == recent).Target;
        recentTarget.Profile!.AccountSid.Should().Be(Second);
        recentTarget.Profile.Folder.Should().Be(Environment.SpecialFolder.ApplicationData);
        recentTarget.ShortcutTarget.Should().Be(Path.Combine(_accounts.Profiles[Second], "missing-document.txt"));
        _shortcuts.Verify(reader => reader.ReadTarget(unselectedRecent), Times.Never);
        ((CleanerFileTarget)result.Findings.Single(finding => finding.Detail == broken).Target).ShortcutTarget
            .Should().Be(Path.Combine(_accounts.Profiles[Second], "missing.exe"));
        _shortcuts.Verify(reader => reader.ReadTarget(common), Times.Once);
        _files.Clean((CleanerFileTarget)result.Findings.Single(finding => finding.Detail == broken).Target);
        _files.Clean(recentTarget);
        File.Exists(broken).Should().BeFalse();
        File.Exists(recent).Should().BeFalse();
        File.Exists(valid).Should().BeTrue();
    }

    [Fact]
    public async Task StartMenuSearchIncludesRootAndNestedLinksAndRetainsRedirectedProgramsWithoutDuplicates()
    {
        var menu = Path.Combine(_root, "redirected-menu");
        _accounts.Folders[(Second, Environment.SpecialFolder.StartMenu)] = menu;
        _accounts.Folders[(Second, Environment.SpecialFolder.Programs)] = Path.Combine(menu, "Programs");
        var rootLink = Write(Path.Combine(menu, "root.lnk"));
        var nested = Write(Path.Combine(menu, @"Programs\Vendor\nested.lnk"));
        var common = Write(Path.Combine(_root, @"system\CommonStartMenu\root.lnk"));
        _shortcuts.Setup(reader => reader.ReadTarget(It.IsAny<string>())).Returns(Path.Combine(_root, "missing.exe"));
        var options = new CleanerOptions { ShortcutAccountSids = [Second], DesktopShortcuts = false, RecentShortcuts = false };

        var result = await _files.ScanAsync(CleanerArea.Shortcuts, options, default);

        result.Notices.Should().BeEmpty();
        result.Findings.Select(finding => finding.Detail).Should().BeEquivalentTo(rootLink, nested, common);
        result.CheckedItems.Should().Be(3);
        _shortcuts.Verify(reader => reader.ReadTarget(nested), Times.Once);
        var target = (CleanerFileTarget)result.Findings.Single(finding => finding.Detail == rootLink).Target;
        target.Profile!.Folder.Should().Be(Environment.SpecialFolder.StartMenu);
        target.Profile.AccountSid.Should().Be(Second);
        var backups = new CleanerBackupService(new TestCleanerBackupVault(Path.Combine(_root, "vault")),
            _files, Mock.Of<ICleanerRegistryRecovery>());
        var backup = await backups.CreateAsync([result.Findings.Single(finding => finding.Detail == rootLink)], default);
        _files.Clean(target);
        File.Exists(rootLink).Should().BeFalse();
        (await backups.RestoreAsync(backup.Id, default)).Errors.Should().BeEmpty();
        File.Exists(rootLink).Should().BeTrue();
        (await _files.ScanAsync(CleanerArea.Shortcuts, options with { StartMenuShortcuts = false }, default)).CheckedItems.Should().Be(0);
    }

    [Fact]
    public async Task UnavailableAccountsAndPackageInventoriesProduceNotices_AndOtherTemporaryFilesStillScan()
    {
        const string missing = "S-1-5-21-1-2-3-9999";
        var temp = Write(Local(Second, @"Temp\old"));
        _packages.Setup(packages => packages.GetInstalledPackageNamesForUserAsync(Second, It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("package inventory denied"));
        var result = await _files.ScanAsync(CleanerArea.JunkFiles,
            new() { JunkAccountSids = [missing, Second], ApplicationCaches = ["Selected.App_publisher"] }, default);
        result.Findings.Should().ContainSingle().Which.Detail.Should().Be(temp);
        result.Notices.Should().HaveCount(2).And.Contain(notice => notice.Contains(missing)).And.Contain(notice => notice.Contains("package inventory denied"));
    }

    [Fact]
    public async Task ProfileChangesAndRemovalBlockCleanupAndRestore_AndActiveBrowserIsCheckedForOriginalAccount()
    {
        Write(Local(Second, @"Google\Chrome\User Data\Default\Cache\old"));
        var result = await _files.ScanAsync(CleanerArea.Privacy, new() { PrivacyAccountSids = [Second], RecentFiles = false }, default);
        var target = (CleanerFileTarget)result.Findings.Single().Target;
        _processes.Setup(processes => processes.IsRunning("chrome")).Returns(true);
        Assert.Throws<IOException>(() => _files.Clean(target));
        _processes.Setup(processes => processes.IsRunning("chrome")).Returns(false);
        _accounts.Folders[(Second, Environment.SpecialFolder.LocalApplicationData)] = Path.Combine(_root, "changed-local");
        Assert.Throws<IOException>(() => _files.ValidateForRestore(target));
        Assert.Throws<IOException>(() => _files.Clean(target));
        _accounts.Profiles.Remove(Second);
        Assert.Throws<IOException>(() => _files.ValidateForRestore(target));
        File.Exists(target.Path).Should().BeTrue();
    }

    [Fact]
    public async Task MultiAccountBackupRecoveryReopensOriginalAccounts_AndRoundTripsTheirEvidence()
    {
        foreach (var sid in new[] { First, Second }) Write(Local(sid, @"Temp\old"));
        var result = await _files.ScanAsync(CleanerArea.JunkFiles, new() { JunkAccountSids = [First, Second] }, default);
        var backups = new CleanerBackupService(new TestCleanerBackupVault(Path.Combine(_root, "vault")), _files, Mock.Of<ICleanerRegistryRecovery>());
        var backup = await backups.CreateAsync(result.Findings, default);
        foreach (var finding in result.Findings) _files.Clean((CleanerFileTarget)finding.Target);
        var restored = await backups.RestoreAsync(backup.Id, default);
        restored.Errors.Should().BeEmpty();
        restored.Restored.Should().Be(2);
        result.Findings.Should().OnlyContain(finding => File.Exists(((CleanerFileTarget)finding.Target).Path));
    }

    [Fact]
    public void HiveCompactionUsesSelectedAccounts_RevalidatesBackingFile_AndPreservesEmptySelection()
    {
        foreach (var sid in new[] { First, Second }) Write(Path.Combine(_accounts.Profiles[sid], "NTUSER.DAT"), 16384);
        _native.SetupGet(native => native.IsAvailable).Returns(true);
        _native.Setup(native => native.Save(It.IsAny<string>(), It.IsAny<string>())).Callback<string, string>((_, path) => File.WriteAllBytes(path, new byte[4096]));
        var compactor = new RegistryHiveCompactor(_accounts, new TestCleanerBackupVault(Path.Combine(_root, "vault")), _native.Object);
        var report = compactor.Scan(new() { RegistryAccountSids = [First, Second, First] }, default);
        report.Findings.Should().HaveCount(2);
        report.CheckedItems.Should().Be(2);
        report.Findings.Select(finding => ((CleanerHiveTarget)finding.Target).KeyPath).Should().BeEquivalentTo(@"HKEY_USERS\" + First, @"HKEY_USERS\" + Second);
        var target = (CleanerHiveTarget)report.Findings[0].Target;
        compactor.Clean(target, default, createBackup: false);
        _native.Verify(native => native.Compact(target.KeyPath), Times.Once);
        _accounts.Profiles[First] = Path.Combine(_root, "changed-first");
        Assert.Throws<IOException>(() => compactor.Clean(target, default, createBackup: false));
        compactor.Scan(new() { RegistryAccountSids = [] }, default).Findings.Should().BeEmpty();
        _accounts.Opened.Should().NotContainNulls();
        _accounts.Opened.Count.Should().Be(_accounts.Closed.Count);
    }

    [Fact]
    public async Task CancellationDuringInventoryReleasesSelectedProfile_AndPreservesAllFiles()
    {
        Write(Local(First, @"Temp\old"));
        using var cancellation = new CancellationTokenSource();
        _packages.Setup(packages => packages.GetInstalledPackageNamesForUserAsync(First, It.IsAny<CancellationToken>()))
            .Callback(() => cancellation.Cancel()).ReturnsAsync(new HashSet<string>());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _files.ScanAsync(CleanerArea.JunkFiles,
            new() { JunkAccountSids = [First, Second], ApplicationCaches = ["Selected.App_publisher"] }, cancellation.Token));
        _accounts.Opened.Should().Equal(First);
        _accounts.Closed.Should().Equal(First);
        File.Exists(Local(First, @"Temp\old")).Should().BeTrue();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
