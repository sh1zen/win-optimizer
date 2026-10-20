using FluentAssertions;
using Microsoft.Win32;
using Moq;
using WinOptimizer.Core.Features.Cleaner.Models;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Infrastructure.Features.Cleaner;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Cleaner;

public sealed class CleanerRegistryScanTests
{
    private readonly Mock<ICleanerRegistryReader> _reader = new();
    private readonly CleanerRegistryScanner _scanner;
    public CleanerRegistryScanTests()
    {
        _reader.Setup(x => x.GetSubKeyNames(It.IsAny<string>(), It.IsAny<bool>())).Returns([]);
        _reader.Setup(x => x.GetValues(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<ICollection<string>?>())).Returns([]);
        _scanner = new(new TestCleanerRegistrySessions(Mock.Of<IInteractiveUserService>(), _reader.Object, Mock.Of<ICleanerRegistryEditor>()), Mock.Of<ICleanerBackupVault>());
    }

    [Fact]
    public void StandardScan_CoversMachineAndRedirected32BitRegistrations_WithMeasuredProgress()
    {
        const string root = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        foreach (var prefix in new[] { "HKEY_CURRENT_USER", "HKEY_LOCAL_MACHINE" })
            _reader.Setup(x => x.GetSubKeyNames(prefix + "\\" + root, It.IsAny<bool>())).Returns(["test.exe"]);
        // An unregistered class is verified through the read-only boundary; no live registry is touched.
        var options = new CleanerOptions { RegistryAreas = [RegistryArea.Software, RegistryArea.SystemExtensions] };
        _reader.Setup(x => x.GetSubKeyNames(@"HKEY_LOCAL_MACHINE\Software\Classes", false)).Returns([".test"]);
        _reader.Setup(x => x.GetValues(@"HKEY_LOCAL_MACHINE\Software\Classes\.test", false, It.IsAny<CancellationToken>(), It.IsAny<ICollection<string>?>()))
            .Returns([new("", "Missing.Test.Class", RegistryValueKind.String)]);
        List<CleanerProgress> measurements = [];
        var progress = new Mock<IProgress<CleanerProgress>>();
        progress.Setup(x => x.Report(It.IsAny<CleanerProgress>())).Callback<CleanerProgress>(measurements.Add);
        var result = _scanner.Scan(options, default, progress.Object);
        _reader.Verify(x => x.GetValues(@"HKEY_LOCAL_MACHINE\" + root + @"\test.exe", false, It.IsAny<CancellationToken>(), It.IsAny<ICollection<string>?>()), Times.Once);
        _reader.Verify(x => x.GetValues(@"HKEY_LOCAL_MACHINE\" + root + @"\test.exe", true, It.IsAny<CancellationToken>(), It.IsAny<ICollection<string>?>()), Times.Once);
        result.Findings.Should().ContainSingle().Which.CanClean.Should().BeTrue();
        measurements.Should().Contain(x => x.IsIndeterminate);
        measurements.Should().Contain(x => x.Message == "Cleaner_Registry_SystemExtensions");
        measurements[^1].Completed.Should().Be(result.CheckedItems);
        measurements[^1].Total.Should().Be(result.CheckedItems);
        measurements[^1].Percentage.Should().Be(100);
    }

    [Fact]
    public void CancellationDuringDiscovery_StopsBeforeReadingAnyValues()
    {
        using var cancellation = new CancellationTokenSource();
        _reader.Setup(x => x.GetSubKeyNames(It.IsAny<string>(), It.IsAny<bool>()))
            .Callback(() => cancellation.Cancel()).Returns(["first", "second"]);
        Assert.Throws<OperationCanceledException>(() => _scanner.Scan(new() { RegistryAreas = [RegistryArea.ActiveX] }, cancellation.Token));
        _reader.Verify(x => x.GetValues(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<ICollection<string>?>()), Times.Never);
    }

    [Theory]
    [InlineData(RegistryArea.ApplicationPaths)]
    [InlineData(RegistryArea.Fonts)]
    [InlineData(RegistryArea.StartMenu)]
    public void SharedWindowsRegistrations_AreNotCountedOrCleanedTwice(RegistryArea area)
    {
        _scanner.Scan(new() { RegistryAreas = [area] }, default);
        _reader.Verify(x => x.GetValues(It.IsAny<string>(), true, It.IsAny<CancellationToken>(), It.IsAny<ICollection<string>?>()), Times.Never);
    }

    [Fact]
    public void IntegrityScan_UsesBothInterfaceViews_AndOneSharedTypeLibraryView()
    {
        _scanner.Scan(new() { RegistryAreas = [RegistryArea.Integrity] }, default);
        _reader.Verify(x => x.GetValues(@"HKEY_LOCAL_MACHINE\Software\Classes\Interface", true, It.IsAny<CancellationToken>(), It.IsAny<ICollection<string>?>()), Times.Once);
        _reader.Verify(x => x.GetValues(@"HKEY_LOCAL_MACHINE\Software\Classes\TypeLib", true, It.IsAny<CancellationToken>(), It.IsAny<ICollection<string>?>()), Times.Never);
    }

    [Fact]
    public void UserComRegistrations_IncludeTheRedirected32BitView()
    {
        _scanner.Scan(new() { RegistryAreas = [RegistryArea.ActiveX] }, default);
        _reader.Verify(x => x.GetValues(@"HKEY_CURRENT_USER\Software\Classes\CLSID", true, It.IsAny<CancellationToken>(), It.IsAny<ICollection<string>?>()), Times.Once);
        CleanerRegistryScanner.ReferenceView(@"HKEY_LOCAL_MACHINE\Software\Classes\TypeLib\test\1.0\0\win32", RegistryView.Registry64)
            .Should().Be(RegistryView.Registry32);
    }

    [Fact]
    public void SharedDllReferenceCounts_AreInspectedAsPaths_WithoutChangingTheirValueType()
    {
        var missing = Path.Combine(AppContext.BaseDirectory, Guid.NewGuid().ToString("N"), "removed.dll");
        _reader.Setup(x => x.GetValues(@"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\SharedDLLs", false, It.IsAny<CancellationToken>(), It.IsAny<ICollection<string>?>()))
            .Returns([new(missing, 2, RegistryValueKind.DWord)]);
        var result = _scanner.Scan(new() { RegistryAreas = [RegistryArea.SharedFiles] }, default);
        var target = (CleanerRegistryTarget)result.Findings.Should().ContainSingle().Which.Target;
        target.Kind.Should().Be((int)RegistryValueKind.DWord);
        target.Data.Should().Be("2");
        target.MissingPath.Should().Be(missing);
    }

    [Fact]
    public void CancellationDuringInspection_DoesNotContinueToOtherKeys()
    {
        using var cancellation = new CancellationTokenSource();
        _reader.Setup(x => x.GetValues(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<ICollection<string>?>()))
            .Callback(() => cancellation.Cancel()).Returns([]);
        Assert.Throws<OperationCanceledException>(() => _scanner.Scan(new() { RegistryAreas = [RegistryArea.ApplicationPaths] }, cancellation.Token));
        _reader.Verify(x => x.GetValues(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<ICollection<string>?>()), Times.Once);
    }

    [Fact]
    public void DeepScan_VisitsProgIdCommands_CaseInsensitively_AndReportsAccessFailures()
    {
        _reader.Setup(x => x.GetSubKeyNames(@"HKEY_CURRENT_USER\Software\Classes", false)).Returns(["Test.Prog", "CLSID", ".test"]);
        _reader.Setup(x => x.GetSubKeyNames(@"HKEY_CURRENT_USER\Software\Classes\Test.Prog", false)).Returns(["Shell", "Unrelated"]);
        _reader.Setup(x => x.GetSubKeyNames(@"HKEY_CURRENT_USER\Software\Classes\Test.Prog\Shell", false)).Returns(["Open"]);
        _reader.Setup(x => x.GetSubKeyNames(@"HKEY_CURRENT_USER\Software\Classes\Test.Prog\Shell\Open", false)).Returns(["Command"]);
        _reader.Setup(x => x.GetValues(@"HKEY_CURRENT_USER\Software\Classes\Test.Prog\Shell\Open\Command", false, It.IsAny<CancellationToken>(), It.IsAny<ICollection<string>?>()))
            .Throws(new UnauthorizedAccessException("test access denied"));
        var report = _scanner.Scan(new() { RegistryAreas = [RegistryArea.Shortcuts], DeepClean = true }, default);
        report.Notices.Should().ContainSingle().Which.Should().Contain("test access denied");
        _reader.Verify(x => x.GetValues(@"HKEY_CURRENT_USER\Software\Classes\Test.Prog\Unrelated", false, It.IsAny<CancellationToken>(), It.IsAny<ICollection<string>?>()), Times.Never);
        _reader.Verify(x => x.GetValues(@"HKEY_CURRENT_USER\Software\Classes\CLSID", false, It.IsAny<CancellationToken>(), It.IsAny<ICollection<string>?>()), Times.Never);
    }

    [Fact]
    public void SelectedAccountsUseTheirOwnPaths_AndMachineRegistrationsRunOnce()
    {
        const string first = "S-1-5-21-1-2-3-1001", second = "S-1-5-21-1-2-3-1002";
        var sessions = new TestCleanerRegistrySessions(Mock.Of<IInteractiveUserService>(), _reader.Object, Mock.Of<ICleanerRegistryEditor>());
        sessions.Profiles.Add(first, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        sessions.Profiles.Add(second, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        const string branch = @"\Software\Microsoft\Windows\Help";
        foreach (var sid in new[] { first, second })
            _reader.Setup(reader => reader.GetValues(@"HKEY_USERS\" + sid + branch, false, It.IsAny<CancellationToken>(), It.IsAny<ICollection<string>?>()))
                .Returns([new("test.chm", @"%USERPROFILE%\removed.chm", RegistryValueKind.ExpandString)]);
        var scanner = new CleanerRegistryScanner(sessions, Mock.Of<ICleanerBackupVault>());
        var report = scanner.Scan(new() { RegistryAreas = [RegistryArea.HelpFiles], RegistryAccountSids = [first, first, second] }, default);
        Assert.Equal(2, report.Findings.Count);
        Assert.Contains(report.Findings, finding => ((CleanerRegistryTarget)finding.Target).MissingPath == Path.Combine(sessions.Profiles[first], "removed.chm"));
        Assert.Contains(report.Findings, finding => ((CleanerRegistryTarget)finding.Target).MissingPath == Path.Combine(sessions.Profiles[second], "removed.chm"));
        _reader.Verify(reader => reader.GetValues("HKEY_LOCAL_MACHINE" + branch, false, It.IsAny<CancellationToken>(), It.IsAny<ICollection<string>?>()), Times.Once);
        _reader.Verify(reader => reader.GetValues("HKEY_CURRENT_USER" + branch, It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<ICollection<string>?>()), Times.Never);
        Assert.Equal(sessions.Opened.OrderBy(sid => sid), sessions.Closed.OrderBy(sid => sid));
    }

    [Fact]
    public void EmptyAccountSelectionScansOnlyMachineKeys_AndMissingAccountsProduceNotices()
    {
        var sessions = new TestCleanerRegistrySessions(Mock.Of<IInteractiveUserService>(), _reader.Object, Mock.Of<ICleanerRegistryEditor>());
        var scanner = new CleanerRegistryScanner(sessions, Mock.Of<ICleanerBackupVault>());
        scanner.Scan(new() { RegistryAreas = [RegistryArea.ApplicationPaths], RegistryAccountSids = [] }, default);
        _reader.Verify(reader => reader.GetValues(It.Is<string>(path => path.StartsWith("HKEY_CURRENT_USER")), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<ICollection<string>?>()), Times.Never);
        var report = scanner.Scan(new() { RegistryAreas = [RegistryArea.ApplicationPaths], RegistryAccountSids = ["S-1-5-21-1-2-3-9999"] }, default);
        Assert.Contains(report.Notices, notice => notice.Contains("Account unavailable"));
    }

    [Fact]
    public void RegisteredClassesAndReferenceCachesAreIsolatedBetweenAccounts()
    {
        const string first = "S-1-5-21-1-2-3-1001", second = "S-1-5-21-1-2-3-1002";
        var sessions = new TestCleanerRegistrySessions(Mock.Of<IInteractiveUserService>(), _reader.Object, Mock.Of<ICleanerRegistryEditor>());
        sessions.Profiles.Add(first, @"C:\Profiles\First"); sessions.Profiles.Add(second, @"C:\Profiles\Second");
        foreach (var sid in new[] { first, second })
        {
            _reader.Setup(reader => reader.GetSubKeyNames(@"HKEY_USERS\" + sid + @"\Software\Classes", false)).Returns([".test"]);
            _reader.Setup(reader => reader.GetValues(@"HKEY_USERS\" + sid + @"\Software\Classes\.test", false, It.IsAny<CancellationToken>(), It.IsAny<ICollection<string>?>()))
                .Returns([new("", "Account.Application", RegistryValueKind.String)]);
        }
        _reader.Setup(reader => reader.KeyExists(@"HKEY_USERS\" + first + @"\Software\Classes\Account.Application", false)).Returns(true);
        var scanner = new CleanerRegistryScanner(sessions, Mock.Of<ICleanerBackupVault>());
        var report = scanner.Scan(new() { RegistryAreas = [RegistryArea.FileExtensions], RegistryAccountSids = [first, second] }, default);
        var finding = Assert.Single(report.Findings);
        Assert.StartsWith(@"HKEY_USERS\" + second, ((CleanerRegistryTarget)finding.Target).KeyPath);
    }

    [Fact]
    public async Task CleanupAndRecoveryReopenTheScannedAccount_AndRejectUnknownAccounts()
    {
        const string sid = "S-1-5-21-1-2-3-1002";
        var editor = new Mock<ICleanerRegistryEditor>();
        var key = new Mock<ICleanerRegistryKey>();
        var sessions = new TestCleanerRegistrySessions(Mock.Of<IInteractiveUserService>(), _reader.Object, editor.Object);
        sessions.Profiles.Add(sid, @"C:\Profiles\Other");
        var target = new CleanerRegistryTarget(@"HKEY_USERS\" + sid + @"\Software\Classes\.test", "", "\"Missing.Application\"", (int)RegistryValueKind.String, false, "class:Missing.Application");
        key.Setup(key => key.ReadValue("")).Returns(new CleanerRegistryValue("", "Missing.Application", RegistryValueKind.String));
        editor.Setup(editor => editor.OpenKey(target.KeyPath, false, It.IsAny<bool>())).Returns(key.Object);
        var scanner = new CleanerRegistryScanner(sessions, Mock.Of<ICleanerBackupVault>());
        await scanner.CleanAsync(target, default);
        key.Verify(key => key.DeleteValue(""), Times.Once);
        key.Setup(key => key.ReadValue("")).Returns((CleanerRegistryValue?)null);
        scanner.Restore(target);
        key.Verify(key => key.SetValue(It.Is<CleanerRegistryValue>(value => (string)value.Data == "Missing.Application")), Times.Once);
        Assert.All(sessions.Opened, openedSid => Assert.Equal(sid, openedSid));
        Assert.Equal(sessions.Opened.Count, sessions.Closed.Count);
        await Assert.ThrowsAsync<IOException>(() => scanner.CleanAsync(target with { KeyPath = target.KeyPath.Replace(sid, "S-1-5-21-1-2-3-9999") }, default));
    }

    [Fact]
    public async Task ProfilePathChangedAfterScanBlocksDeletionOfReResolvedReferences()
    {
        const string sid = "S-1-5-21-1-2-3-1002";
        var editor = new Mock<ICleanerRegistryEditor>();
        var key = new Mock<ICleanerRegistryKey>();
        var sessions = new TestCleanerRegistrySessions(Mock.Of<IInteractiveUserService>(), _reader.Object, editor.Object);
        sessions.Profiles.Add(sid, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var path = @"HKEY_USERS\" + sid + @"\Software\Microsoft\Windows\Help";
        var value = new CleanerRegistryValue("help.chm", @"%USERPROFILE%\removed.chm", RegistryValueKind.ExpandString);
        _reader.Setup(reader => reader.GetValues(path, false, It.IsAny<CancellationToken>(), It.IsAny<ICollection<string>?>())).Returns([value]);
        key.Setup(key => key.ReadValue(value.Name)).Returns(value);
        editor.Setup(editor => editor.OpenKey(path, false, It.IsAny<bool>())).Returns(key.Object);
        var scanner = new CleanerRegistryScanner(sessions, Mock.Of<ICleanerBackupVault>());
        var target = (CleanerRegistryTarget)Assert.Single(scanner.Scan(new() { RegistryAreas = [RegistryArea.HelpFiles], RegistryAccountSids = [sid] }, default).Findings).Target;
        sessions.Profiles[sid] = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAsync<IOException>(() => scanner.CleanAsync(target, default));
        key.Verify(key => key.DeleteValue(It.IsAny<string>()), Times.Never);
    }
}
