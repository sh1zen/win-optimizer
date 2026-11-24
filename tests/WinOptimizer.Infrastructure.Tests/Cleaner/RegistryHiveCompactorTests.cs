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
    private readonly Mock<IRegistryDefragBoot> _boot = new(MockBehavior.Strict);
    private readonly RegistryHiveCompactor _compactor;

    public RegistryHiveCompactorTests()
    {
        Directory.CreateDirectory(_root);
        var user = new Mock<IInteractiveUserService>();
        user.Setup(x => x.GetInteractiveUserFolderPath(It.IsAny<Environment.SpecialFolder>())).Returns(_root);
        var accounts = new TestCleanerRegistrySessions(user.Object, Mock.Of<ICleanerRegistryReader>(), Mock.Of<ICleanerRegistryEditor>());
        _compactor = new(accounts, new TestCleanerBackupVault(_root), _native.Object, _boot.Object);
    }

    [Fact]
    public void ScanMeasuresRecoverableSpaceAndReportsTheLastOfflineResult()
    {
        File.WriteAllBytes(Path.Combine(_root, "NTUSER.DAT"), new byte[16384]);
        _native.Setup(x => x.Save("HKEY_CURRENT_USER", It.IsAny<string>()))
            .Callback<string, string>((_, path) => File.WriteAllBytes(path, new byte[4096]));
        _boot.Setup(x => x.GetStatusNotice()).Returns("Original hives restored");

        var report = _compactor.Scan(new CleanerOptions(), default);

        report.Findings.Should().ContainSingle().Which.Bytes.Should().Be(12288);
        report.Notices.Should().ContainSingle().Which.Should().Be("Original hives restored");
        Directory.GetFiles(_root, "analysis-*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task UnsupportedHiveDoesNotCreateABootJob()
    {
        await Assert.ThrowsAsync<IOException>(async () => await _compactor.ScheduleAsync([new(@"HKEY_LOCAL_MACHINE\SAM")], default));
        _native.VerifyNoOtherCalls();
        _boot.VerifyNoOtherCalls();
    }

    public void Dispose()
    {
        var full = Path.GetFullPath(_root);
        if (!full.StartsWith(Path.Combine(Path.GetTempPath(), "win-optimizer-hive-tests") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid test directory.");
        Directory.Delete(full, recursive: true);
    }
}
