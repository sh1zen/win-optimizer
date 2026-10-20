using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Cleaner.Models;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Infrastructure.Features.Cleaner;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Cleaner;

public sealed class RegistryHiveCompactorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "win-optimizer-hive-tests", Guid.NewGuid().ToString("N"));
    private readonly Mock<IRegistryHiveOperations> _native = new();
    private readonly RegistryHiveCompactor _compactor;
    public RegistryHiveCompactorTests()
    {
        Directory.CreateDirectory(_root);
        var user = new Mock<IInteractiveUserService>();
        user.Setup(x => x.GetInteractiveUserFolderPath(It.IsAny<Environment.SpecialFolder>())).Returns(_root);
        var vault = new TestCleanerBackupVault(_root);
        _native.SetupGet(x => x.IsAvailable).Returns(true);
        _compactor = new(new TestCleanerRegistrySessions(user.Object, Mock.Of<ICleanerRegistryReader>(), Mock.Of<ICleanerRegistryEditor>()), vault, _native.Object);
    }
    [Fact]
    public void Compaction_RequiresVerifiedBackup_BeforeCallingWindows()
    {
        string? backup = null;
        _native.Setup(x => x.Save("HKEY_CURRENT_USER", It.IsAny<string>())).Callback<string, string>((_, path) => { File.WriteAllText(path, "hive backup"); backup = path; });
        _native.Setup(x => x.Compact("HKEY_CURRENT_USER")).Callback(() => { backup.Should().NotBeNull(); File.Exists(backup![..^8]).Should().BeTrue(); });
        _compactor.Clean(new("HKEY_CURRENT_USER"), default);
        _native.Verify(x => x.Compact("HKEY_CURRENT_USER"), Times.Once);
        File.Exists(backup![..^8]).Should().BeTrue();
    }
    [Fact]
    public void ExplicitlyDisabledBackup_SkipsSnapshot_AndStillChecksNativeAvailability()
    {
        _compactor.Clean(new("HKEY_CURRENT_USER"), default, createBackup: false);
        _native.Verify(x => x.Save(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _native.Verify(x => x.Compact("HKEY_CURRENT_USER"), Times.Once);
    }
    [Fact]
    public void Scan_MeasuresRecoverableSpace_AndDeletesOnlyItsTemporaryHive()
    {
        File.WriteAllBytes(Path.Combine(_root, "NTUSER.DAT"), new byte[16384]);
        _native.Setup(x => x.Save("HKEY_CURRENT_USER", It.IsAny<string>())).Callback<string, string>((_, path) => File.WriteAllBytes(path, new byte[4096]));
        var report = _compactor.Scan(new CleanerOptions(), default);
        report.Findings.Should().ContainSingle().Which.Bytes.Should().Be(12288);
        Directory.GetFiles(_root, "analysis-*", SearchOption.AllDirectories).Should().BeEmpty();
        _native.Verify(x => x.Compact(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void UnsupportedCompaction_ReportsNotice_WithoutCreatingBackups()
    {
        _native.SetupGet(x => x.IsAvailable).Returns(false);
        var report = _compactor.Scan(new CleanerOptions(), default);
        report.Findings.Should().BeEmpty(); report.Notices.Should().ContainSingle();
        _native.Verify(x => x.Save(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void MissingBackup_BlocksCompaction()
    {
        Assert.Throws<IOException>(() => _compactor.Clean(new("HKEY_CURRENT_USER"), default));
        _native.Verify(x => x.Compact(It.IsAny<string>()), Times.Never);
    }
    [Fact]
    public void BackupFailure_BlocksCompaction()
    {
        _native.Setup(x => x.Save(It.IsAny<string>(), It.IsAny<string>())).Throws(new IOException("backup failed"));
        Assert.Throws<IOException>(() => _compactor.Clean(new("HKEY_CURRENT_USER"), default));
        _native.Verify(x => x.Compact(It.IsAny<string>()), Times.Never);
    }
    [Fact]
    public void UnknownHive_IsRejectedBeforeBackup()
    {
        Assert.Throws<IOException>(() => _compactor.Clean(new(@"HKEY_LOCAL_MACHINE\SAM"), default));
        _native.Verify(x => x.Save(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }
    [Fact]
    public void CancellationAfterBackup_BlocksCompaction_AndKeepsBackup()
    {
        using var cts = new CancellationTokenSource();
        _native.Setup(x => x.Save(It.IsAny<string>(), It.IsAny<string>())).Callback<string, string>((_, path) => { File.WriteAllText(path, "hive backup"); cts.Cancel(); });
        Assert.Throws<OperationCanceledException>(() => _compactor.Clean(new("HKEY_CURRENT_USER"), cts.Token));
        _native.Verify(x => x.Compact(It.IsAny<string>()), Times.Never);
        Directory.GetFiles(_root, "*.hiv", SearchOption.AllDirectories).Should().ContainSingle();
    }
    public void Dispose()
    {
        var full = Path.GetFullPath(_root);
        if (!full.StartsWith(Path.Combine(Path.GetTempPath(), "win-optimizer-hive-tests") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid test directory.");
        Directory.Delete(full, recursive: true);
    }
}
