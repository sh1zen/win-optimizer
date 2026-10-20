using FluentAssertions;
using Microsoft.Win32;
using Moq;
using WinOptimizer.Core.Features.Cleaner.Models;
using WinOptimizer.Core.Features.Cleaner.Services;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Infrastructure.Features.Cleaner;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Cleaner;

/// <summary>Exercises privacy discovery and the normal backup/delete/restore path using isolated registry boundaries.</summary>
public sealed class CleanerPrivacyRegistryTests : IDisposable
{
    private const string Explorer = @"Software\Microsoft\Windows\CurrentVersion\Explorer\";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "win-optimizer-privacy-tests", Guid.NewGuid().ToString("N"));
    private readonly Mock<ICleanerRegistryReader> _reader = new();
    private readonly Mock<ICleanerRegistryEditor> _editor = new();
    private readonly Dictionary<string, List<CleanerRegistryValue>> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly TestCleanerRegistrySessions _sessions;
    private readonly TestCleanerBackupVault _vault;
    private readonly CleanerRegistryScanner _registry;
    private readonly CleanerFileScanner _files;

    public CleanerPrivacyRegistryTests()
    {
        var user = new Mock<IInteractiveUserService>();
        user.Setup(x => x.GetInteractiveUserFolderPath(It.IsAny<Environment.SpecialFolder>())).Returns(_root);
        _reader.Setup(x => x.GetSubKeyNames(It.IsAny<string>(), It.IsAny<bool>())).Returns([]);
        _reader.Setup(x => x.GetValues(It.IsAny<string>(), false, It.IsAny<CancellationToken>(), It.IsAny<ICollection<string>?>()))
            .Returns((string path, bool _, CancellationToken _, ICollection<string>? _) => _values.GetValueOrDefault(path) ?? []);
        _sessions = new(user.Object, _reader.Object, _editor.Object);
        _vault = new(Path.Combine(_root, "vault"));
        _registry = new(_sessions, _vault);
        _files = new(_sessions, Mock.Of<IAppxPackageSource>(), Mock.Of<ICleanerProcessMonitor>(), Mock.Of<ICleanerShortcutReader>(), Mock.Of<ICleanerSystemFolders>());
    }

    private void Values(string relative, params CleanerRegistryValue[] values) => _values[@"HKEY_CURRENT_USER\" + relative] = [.. values];
    private static CleanerRegistryValue Text(string name, string text = "private history") => new(name, text, RegistryValueKind.String);

    [Fact]
    public void WindowsHistory_CoversExistingReferencesAndBinaryHistory_WithoutSelectingConfiguration()
    {
        Values(Explorer + "RunMRU", Text("a", "notepad"), Text("MRUList", "a"), Text("Configuration"));
        Values(Explorer + "TypedPaths", Text("url1", _root), Text("other"));
        Values(Explorer + "RecentDocs", new("0", new byte[] { 1, 2, 3 }, RegistryValueKind.Binary),
            new("MRUListEx", new byte[] { 255, 255, 255, 255 }, RegistryValueKind.Binary), Text("ViewStream"));
        var dialogs = @"HKEY_CURRENT_USER\" + Explorer + @"ComDlg32\OpenSavePidlMRU";
        _reader.Setup(x => x.GetSubKeyNames(dialogs, false)).Returns([".txt"]);
        Values(Explorer + @"ComDlg32\OpenSavePidlMRU\.txt", new CleanerRegistryValue("0", new byte[] { 4, 5 }, RegistryValueKind.Binary));
        Values(@"Software\Microsoft\Microsoft Management Console\Recent File List", Text("File1"), Text("Max"));
        Values(@"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache",
            Text(@"C:\Windows\explorer.exe.FriendlyAppName"), Text("LangID"));
        List<CleanerProgress> reports = [];
        var progress = new Mock<IProgress<CleanerProgress>>();
        progress.Setup(x => x.Report(It.IsAny<CleanerProgress>())).Callback<CleanerProgress>(reports.Add);
        var result = _registry.ScanPrivacy(new(), default, progress.Object);
        result.Findings.Should().HaveCount(8).And.OnlyContain(x => x.Area == CleanerArea.Privacy && x.CanClean);
        result.Findings.Select(x => ((CleanerRegistryTarget)x.Target).ValueName).Should().NotContain(["Configuration", "other", "ViewStream", "Max", "LangID"]);
        result.Findings.Should().Contain(x => x.LabelKey == "Cleaner_MuiHistory");
        result.Notices.Should().BeEmpty();
        reports[^1].Completed.Should().Be(result.CheckedItems);
        reports[^1].Total.Should().Be(result.CheckedItems);
        _reader.Verify(x => x.GetValues(It.Is<string>(p => p.StartsWith("HKEY_LOCAL_MACHINE")), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<ICollection<string>?>()), Times.Never);
        _editor.VerifyNoOtherCalls();
    }

    [Fact]
    public void ApplicationHistory_IsOptIn_AndCoversOfficeUserListsAndWinRarWithoutConfiguration()
    {
        Values(@"Software\WinRAR\ArcHistory", Text("0"), Text("MaxItems"));
        Values(@"Software\Microsoft\Office\16.0\Word\File MRU", Text("Item 1"), Text("Max Display"));
        const string users = @"HKEY_CURRENT_USER\Software\Microsoft\Office\16.0\Excel\User MRU";
        _reader.Setup(x => x.GetSubKeyNames(users, false)).Returns(["LiveId_test"]);
        Values(@"Software\Microsoft\Office\16.0\Excel\User MRU\LiveId_test\Place MRU", Text("Item 2"));
        _registry.ScanPrivacy(new() { WindowsHistory = false }, default).Findings.Should().BeEmpty();
        var result = _registry.ScanPrivacy(new() { WindowsHistory = false, ApplicationHistory = true }, default);
        result.Findings.Should().HaveCount(3);
        result.Findings.Select(x => ((CleanerRegistryTarget)x.Target).ValueName).Should().BeEquivalentTo("0", "Item 1", "Item 2");
    }

    [Fact]
    public void PrivacyAccounts_AreIndependentOfRegistryAccounts_AndEmptySelectionsReadNothing()
    {
        const string sid = "S-1-5-21-1-2-3-1002";
        _sessions.Profiles[sid] = _root;
        _values[@"HKEY_USERS\" + sid + "\\" + Explorer + "RunMRU"] = [Text("a")];
        var result = _registry.ScanPrivacy(new() { PrivacyAccountSids = [sid, sid], RegistryAccountSids = [] }, default);
        result.Findings.Should().ContainSingle().Which.Detail.Should().StartWith(@"HKEY_USERS\" + sid);
        _sessions.Opened.Should().Equal(sid);
        _sessions.Closed.Should().Equal(sid);
        _reader.Invocations.Clear();
        _registry.ScanPrivacy(new() { PrivacyAccountSids = [] }, default).Findings.Should().BeEmpty();
        _reader.VerifyNoOtherCalls();
    }

    [Fact]
    public void MultiplePrivacyAccounts_ShareOneMeasuredInventory_AndReleaseSessionsBeforeInspection()
    {
        string[] selected = ["S-1-5-21-1-2-3-1001", "S-1-5-21-1-2-3-1002"];
        foreach (var sid in selected) _sessions.Profiles[sid] = Path.Combine(_root, sid);
        List<CleanerProgress> reports = [];
        var progress = new Mock<IProgress<CleanerProgress>>();
        progress.Setup(x => x.Report(It.IsAny<CleanerProgress>())).Callback<CleanerProgress>(value =>
        {
            reports.Add(value);
            if (value.Total > 0) _sessions.Closed.Should().BeEquivalentTo(selected);
        });
        var scan = _registry.ScanPrivacy(new() { PrivacyAccountSids = selected }, default, progress.Object);
        reports.Where(x => x.Total > 0).Should().OnlyContain(x => x.Total == scan.CheckedItems);
        reports[^1].Percentage.Should().Be(100);
        _sessions.Opened.Should().BeEquivalentTo(selected);
    }

    [Fact]
    public void UnreadableHistory_DoesNotHideOtherFindings_AndCancellationReleasesTheSession()
    {
        _reader.Setup(x => x.GetValues(@"HKEY_CURRENT_USER\" + Explorer + "RunMRU", false, It.IsAny<CancellationToken>(), It.IsAny<ICollection<string>?>()))
            .Throws(new UnauthorizedAccessException("denied"));
        Values(Explorer + "TypedPaths", Text("url1"));
        var scan = _registry.ScanPrivacy(new(), default);
        scan.Notices.Should().ContainSingle().Which.Should().Contain("RunMRU");
        scan.Findings.Should().ContainSingle();
        using var cancellation = new CancellationTokenSource();
        _reader.Setup(x => x.GetSubKeyNames(It.IsAny<string>(), false)).Callback(cancellation.Cancel).Returns([".txt"]);
        Assert.ThrowsAny<OperationCanceledException>(() => _registry.ScanPrivacy(new(), cancellation.Token));
        _sessions.Closed.Count.Should().Be(_sessions.Opened.Count);
    }

    [Fact]
    public async Task PrivacyBackendAndService_RoundTripMixedFileAndBinaryRegistryEvidenceThroughBackups()
    {
        Values(Explorer + "RecentDocs", new CleanerRegistryValue("0", new byte[] { 1, 7, 9 }, RegistryValueKind.Binary));
        var cache = Path.Combine(_root, @"AppData\LocalLow\Microsoft\CryptnetUrlCache\Content\test");
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        File.WriteAllText(cache, "cached certificate");
        File.SetLastWriteTimeUtc(cache, DateTime.UtcNow.AddDays(-1));
        File.SetLastAccessTimeUtc(cache, DateTime.UtcNow.AddDays(-1));
        var key = new Mock<ICleanerRegistryKey>();
        var path = @"HKEY_CURRENT_USER\" + Explorer + "RecentDocs";
        key.Setup(x => x.ReadValue("0")).Returns(() => _values[path].SingleOrDefault(x => x.Name == "0"));
        key.Setup(x => x.DeleteValue("0")).Callback(() => _values[path].Clear());
        key.Setup(x => x.SetValue(It.IsAny<CleanerRegistryValue>())).Callback<CleanerRegistryValue>(value => _values[path].Add(value));
        _editor.Setup(x => x.OpenKey(path, false, It.IsAny<bool>())).Returns(key.Object);
        var backend = new WindowsCleanerBackend(_files, _registry, new(_sessions, _vault, Mock.Of<IRegistryHiveOperations>()), Mock.Of<IProcessExecutor>());
        var backups = new CleanerBackupService(_vault, _files, _registry);
        var service = new CleanerService(backend, backups);
        var options = new CleanerOptions();
        var report = await service.ScanAsync(options, [CleanerArea.Privacy], null, default);
        report.Findings.Should().HaveCount(2);
        var apply = await service.ApplyAsync(report.Findings.Select(x => x.Id).ToArray(), null, default);
        apply.Completed.Should().Be(2);
        apply.Errors.Should().BeEmpty();
        File.Exists(cache).Should().BeFalse();
        _values[path].Should().BeEmpty();
        var backup = (await backups.ListAsync(default)).Should().ContainSingle().Subject;
        var restored = await backups.RestoreAsync(backup.Id, default);
        restored.Restored.Should().Be(2);
        restored.Errors.Should().BeEmpty();
        File.ReadAllText(cache).Should().Be("cached certificate");
        _values[path].Single().Data.Should().BeEquivalentTo(new byte[] { 1, 7, 9 });
    }

    [Fact]
    public async Task ChangedHistoryAndUnallowlistedTargets_ArePreserved()
    {
        Values(Explorer + "RunMRU", Text("a", "old command"));
        var target = (CleanerRegistryTarget)_registry.ScanPrivacy(new(), default).Findings.Single().Target;
        var key = new Mock<ICleanerRegistryKey>();
        key.SetupSequence(x => x.ReadValue("a")).Returns(Text("a", "old command")).Returns(Text("a", "new command"));
        _editor.Setup(x => x.OpenKey(target.KeyPath, false, It.IsAny<bool>())).Returns(key.Object);
        await Assert.ThrowsAsync<IOException>(() => _registry.CleanAsync(target, default));
        key.Verify(x => x.DeleteValue(It.IsAny<string>()), Times.Never);
        Assert.Throws<IOException>(() => _registry.Verify(target with { KeyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run" }));
        Assert.Throws<IOException>(() => _registry.Restore(target with { KeyPath = @"HKEY_LOCAL_MACHINE\Software\Test" }));
    }

    public void Dispose()
    {
        var root = Path.GetFullPath(_root);
        if (!root.StartsWith(Path.Combine(Path.GetTempPath(), "win-optimizer-privacy-tests") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid test directory.");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
