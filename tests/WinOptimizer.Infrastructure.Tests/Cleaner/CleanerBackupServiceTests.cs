using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Cleaner.Models;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Infrastructure.Features.Cleaner;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Cleaner;

public sealed class CleanerBackupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "CleanerRecoveryTests", Guid.NewGuid().ToString("N"));
    private readonly TestCleanerBackupVault _vault;
    private readonly Mock<ICleanerRegistryRecovery> _registry = new();
    private readonly Mock<ICleanerProcessMonitor> _processes = new();
    private readonly CleanerFileScanner _files;
    private readonly CleanerBackupService _backups;
    public CleanerBackupServiceTests()
    {
        Directory.CreateDirectory(_root);
        _vault = new(Path.Combine(_root, "vault"));
        var user = new Mock<IInteractiveUserService>();
        user.Setup(x => x.GetInteractiveUserFolderPath(It.IsAny<Environment.SpecialFolder>())).Returns(_root);
        _files = new(new TestCleanerRegistrySessions(user.Object, Mock.Of<ICleanerRegistryReader>(), Mock.Of<ICleanerRegistryEditor>()),
            Mock.Of<IAppxPackageSource>(), _processes.Object, Mock.Of<ICleanerShortcutReader>(), Mock.Of<ICleanerSystemFolders>());
        _backups = new(_vault, _files, _registry.Object);
    }
    private CleanerFinding FileFinding(string relative = @"source\old.tmp")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "original bytes");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30));
        File.SetLastAccessTimeUtc(path, DateTime.UtcNow.AddDays(-30));
        var info = new FileInfo(path);
        return new(Guid.NewGuid(), CleanerArea.JunkFiles, "Cleaner_JunkFiles", path, info.Length, true,
            new CleanerFileTarget(_root, path, info.Length, info.LastWriteTimeUtc, DateTime.UtcNow.AddDays(-7)));
    }
    private static CleanerFinding RegistryFinding() => new(Guid.NewGuid(), CleanerArea.Registry, "Cleaner_Registry", "registry evidence", 0, true,
        new CleanerRegistryTarget(@"HKEY_CURRENT_USER\Software\Classes\test\DefaultIcon", "", "\"C:\\\\missing.dll\"", 2, true, @"C:\missing.dll"));

    [Fact]
    public async Task FileCleanupAndRestore_RoundTripsBytesAndTimestamp_AndListsBackup()
    {
        var finding = FileFinding(); var target = (CleanerFileTarget)finding.Target;
        var info = await _backups.CreateAsync([finding], default);
        File.Exists(target.Path).Should().BeTrue();
        _files.Clean(target);
        File.Exists(target.Path).Should().BeFalse();
        var result = await _backups.RestoreAsync(info.Id, default);
        result.Restored.Should().Be(1); result.Errors.Should().BeEmpty();
        File.ReadAllText(target.Path).Should().Be("original bytes");
        File.GetLastWriteTimeUtc(target.Path).Should().Be(target.LastWriteUtc);
        (await _backups.ListAsync(default)).Should().ContainSingle().Which.Id.Should().Be(info.Id);
        (await _backups.RestoreAsync(info.Id, default)).Errors.Should().BeEmpty();
    }
    [Fact]
    public async Task RegistryBackup_PreservesExactNameKindAndView_WithoutReadingLiveRegistry()
    {
        var finding = RegistryFinding();
        var info = await _backups.CreateAsync([finding], default);
        _registry.Verify(x => x.Verify((CleanerRegistryTarget)finding.Target), Times.Once);
        var result = await _backups.RestoreAsync(info.Id, default);
        result.Restored.Should().Be(1);
        _registry.Verify(x => x.Restore((CleanerRegistryTarget)finding.Target), Times.Once);
    }
    [Fact]
    public async Task ExistingDifferentFile_IsPreservedAndReported()
    {
        var finding = FileFinding();
        var info = await _backups.CreateAsync([finding], default);
        File.WriteAllText(finding.Detail, "new application data");
        var result = await _backups.RestoreAsync(info.Id, default);
        result.Restored.Should().Be(0); result.Errors.Should().ContainSingle();
        File.ReadAllText(finding.Detail).Should().Be("new application data");
    }
    [Fact]
    public async Task DamagedPayload_BlocksEntireRestore_BeforeRegistryOrFilesAreChanged()
    {
        var finding = FileFinding();
        var info = await _backups.CreateAsync([RegistryFinding(), finding], default);
        File.Delete(finding.Detail);
        var payload = Directory.GetFiles(_vault.RootDirectory, "*.bin", SearchOption.AllDirectories).Single();
        File.AppendAllText(payload, "corruption");
        await Assert.ThrowsAsync<IOException>(() => _backups.RestoreAsync(info.Id, default));
        File.Exists(finding.Detail).Should().BeFalse();
        _registry.Verify(x => x.Restore(It.IsAny<CleanerRegistryTarget>()), Times.Never);
    }
    [Fact]
    public async Task DamagedManifest_IsRejected_BeforeAnyRestore()
    {
        var finding = RegistryFinding();
        var info = await _backups.CreateAsync([finding], default);
        var manifest = Directory.GetFiles(_vault.RootDirectory, "manifest.backup", SearchOption.AllDirectories).Single();
        var bytes = File.ReadAllBytes(manifest); bytes[^1] ^= 1; File.WriteAllBytes(manifest, bytes);
        await Assert.ThrowsAsync<IOException>(() => _backups.RestoreAsync(info.Id, default));
        _registry.Verify(x => x.Restore(It.IsAny<CleanerRegistryTarget>()), Times.Never);
    }
    [Fact]
    public async Task ChangedSource_BlocksBackup_AndNeverPublishesAnIncompleteSession()
    {
        var finding = FileFinding();
        File.WriteAllText(finding.Detail, "changed source after scan");
        await Assert.ThrowsAsync<IOException>(() => _backups.CreateAsync([finding], default));
        File.Exists(finding.Detail).Should().BeTrue();
        (await _backups.ListAsync(default)).Should().BeEmpty();
    }
    [Fact]
    public async Task ActiveBrowser_BlocksRecoveryOfItsMissingHistoryFile()
    {
        var finding = FileFinding(@"Google\Chrome\User Data\Default\History");
        var info = await _backups.CreateAsync([finding], default);
        File.Delete(finding.Detail);
        _processes.Setup(x => x.IsRunning("chrome")).Returns(true);
        var result = await _backups.RestoreAsync(info.Id, default);
        result.Restored.Should().Be(0); result.Errors.Should().ContainSingle();
        File.Exists(finding.Detail).Should().BeFalse();
    }
    [Fact]
    public async Task CancelledBackup_DoesNotTouchSourceOrPublishMetadata()
    {
        var finding = FileFinding();
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _backups.CreateAsync([finding], cts.Token));
        File.Exists(finding.Detail).Should().BeTrue();
        (await _backups.ListAsync(default)).Should().BeEmpty();
    }
    public void Dispose()
    {
        var parent = Path.Combine(AppContext.BaseDirectory, "CleanerRecoveryTests") + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(_root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe test directory.");
        Directory.Delete(_root, recursive: true);
    }
}
