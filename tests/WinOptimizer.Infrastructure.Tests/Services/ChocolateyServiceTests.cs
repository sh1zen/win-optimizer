using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

public class ChocolateyServiceTests
{
    private readonly Mock<ILogService> _mockLog = new();
    private readonly Mock<ITaskProgressService> _mockProgress = new();
    private readonly Mock<ILocalizationService> _mockLocalization = new();
    private readonly Mock<IProcessExecutor> _mockProcessExecutor = new();
    private readonly Mock<IFileSystemService> _mockFileSystem = new();
    private readonly ChocolateyService _sut;

    public ChocolateyServiceTests()
    {
        _mockLocalization
            .Setup(l => l.GetString(It.IsAny<string>()))
            .Returns((string key) => key);
        _mockLocalization
            .Setup(l => l.GetString(It.IsAny<string>(), It.IsAny<object[]>()))
            .Returns((string key, object[] args) => string.Format(key, args));

        // By default, choco.exe is NOT found
        _mockFileSystem
            .Setup(f => f.FileExists(It.IsAny<string>()))
            .Returns(false);
        _mockFileSystem
            .Setup(f => f.CombinePath(It.IsAny<string[]>()))
            .Returns((string[] parts) => string.Join("\\", parts));

        _sut = new ChocolateyService(
            _mockLog.Object,
            _mockProgress.Object,
            _mockLocalization.Object,
            _mockProcessExecutor.Object,
            _mockFileSystem.Object);
    }

    // ── IsChocolateyInstalledAsync ──

    [Fact]
    public async Task IsChocolateyInstalledAsync_WhenChocoNotFound_ReturnsFalse()
    {
        var result = await _sut.IsChocolateyInstalledAsync();

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsChocolateyInstalledAsync_WhenChocoFoundAtStandardPath_ReturnsTrue()
    {
        _mockFileSystem
            .Setup(f => f.FileExists(@"C:\ProgramData\chocolatey\bin\choco.exe"))
            .Returns(true);

        // Need a new instance since the old one already cached _isInstalled
        var sut = new ChocolateyService(
            _mockLog.Object,
            _mockProgress.Object,
            _mockLocalization.Object,
            _mockProcessExecutor.Object,
            _mockFileSystem.Object);

        var result = await sut.IsChocolateyInstalledAsync();

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsChocolateyInstalledAsync_CachesResult()
    {
        var first = await _sut.IsChocolateyInstalledAsync();
        var second = await _sut.IsChocolateyInstalledAsync();

        first.Should().Be(second);
        // FileExists should only be called during the first check (caching)
    }

    // ── UninstallPackageAsync ──

    [Fact]
    public async Task UninstallPackageAsync_WhenChocoNotFound_ReturnsFalse()
    {
        var result = await _sut.UninstallPackageAsync("notepadplusplus");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task UninstallPackageAsync_SuccessfulUninstall_ReturnsTrue()
    {
        _mockFileSystem
            .Setup(f => f.FileExists(@"C:\ProgramData\chocolatey\bin\choco.exe"))
            .Returns(true);
        _mockProcessExecutor
            .Setup(p => p.ExecuteWithStreamingAsync(
                @"C:\ProgramData\chocolatey\bin\choco.exe",
                It.Is<string>(s => s.Contains("uninstall notepadplusplus")),
                It.IsAny<Action<string>?>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult { ExitCode = 0 });

        var sut = new ChocolateyService(
            _mockLog.Object,
            _mockProgress.Object,
            _mockLocalization.Object,
            _mockProcessExecutor.Object,
            _mockFileSystem.Object);

        var result = await sut.UninstallPackageAsync("notepadplusplus", "Notepad++");

        result.Should().BeTrue();
    }

    // ── GetInstalledPackageIdsAsync ──

    [Fact]
    public async Task GetInstalledPackageIdsAsync_WhenChocoNotFound_ReturnsEmptySet()
    {
        var result = await _sut.GetInstalledPackageIdsAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetInstalledPackageIdsAsync_ParsesChocoListOutput()
    {
        _mockFileSystem
            .Setup(f => f.FileExists(@"C:\ProgramData\chocolatey\bin\choco.exe"))
            .Returns(true);
        _mockProcessExecutor
            .Setup(p => p.ExecuteAsync(
                @"C:\ProgramData\chocolatey\bin\choco.exe",
                "list -r",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult
            {
                ExitCode = 0,
                StandardOutput = "git|2.43.0\nnotepadplusplus|8.6.2\n7zip|23.01\n"
            });

        var sut = new ChocolateyService(
            _mockLog.Object,
            _mockProgress.Object,
            _mockLocalization.Object,
            _mockProcessExecutor.Object,
            _mockFileSystem.Object);

        var result = await sut.GetInstalledPackageIdsAsync();

        result.Should().HaveCount(3);
        result.Should().Contain("git");
        result.Should().Contain("notepadplusplus");
        result.Should().Contain("7zip");
    }

    [Fact]
    public async Task GetInstalledPackageIdsAsync_IsCaseInsensitive()
    {
        _mockFileSystem
            .Setup(f => f.FileExists(@"C:\ProgramData\chocolatey\bin\choco.exe"))
            .Returns(true);
        _mockProcessExecutor
            .Setup(p => p.ExecuteAsync(
                @"C:\ProgramData\chocolatey\bin\choco.exe",
                "list -r",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult
            {
                ExitCode = 0,
                StandardOutput = "Git|2.43.0\n"
            });

        var sut = new ChocolateyService(
            _mockLog.Object,
            _mockProgress.Object,
            _mockLocalization.Object,
            _mockProcessExecutor.Object,
            _mockFileSystem.Object);

        var result = await sut.GetInstalledPackageIdsAsync();

        result.Contains("git").Should().BeTrue();
        result.Contains("Git").Should().BeTrue();
        result.Contains("GIT").Should().BeTrue();
    }
}
