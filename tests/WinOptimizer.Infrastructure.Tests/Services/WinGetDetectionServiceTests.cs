using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Services.WinGet;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

/// <summary>
/// Checks installer-type input guards, case-insensitive inventory and logged COM/CLI failures.
/// </summary>
public class WinGetDetectionServiceTests
{
    private readonly Mock<ILogService> _mockLogService = new();
    private readonly Mock<IInteractiveUserService> _mockInteractiveUserService = new();
    private readonly Mock<IFileSystemService> _mockFileSystemService = new();
    private readonly WinGetComSession _comSession;
    private readonly WinGetDetectionService _sut;

    public WinGetDetectionServiceTests()
    {
        _comSession = new WinGetComSession(_mockLogService.Object);

        _sut = new WinGetDetectionService(
            _comSession,
            _mockLogService.Object,
            _mockInteractiveUserService.Object,
            _mockFileSystemService.Object);
    }

    [Fact]
    public async Task GetInstalledPackageIdsAsync_ReturnsCaseInsensitiveHashSet()
    {
        // Arrange
        _mockInteractiveUserService.Setup(s => s.IsOtsElevation).Returns(false);
        _mockInteractiveUserService
            .Setup(s => s.GetInteractiveUserFolderPath(It.IsAny<Environment.SpecialFolder>()))
            .Returns(@"C:\Users\TestUser\AppData\Local");
        _mockFileSystemService
            .Setup(f => f.CombinePath(It.IsAny<string[]>()))
            .Returns((string[] paths) => string.Join(@"\", paths));
        _mockFileSystemService
            .Setup(f => f.CreateDirectory(It.IsAny<string>()));
        _mockFileSystemService
            .Setup(f => f.FileExists(It.IsAny<string>()))
            .Returns(false);

        // Act
        var result = await _sut.GetInstalledPackageIdsAsync();

        // Assert: the HashSet should use OrdinalIgnoreCase comparer
        result.Should().NotBeNull();
        // Verify case-insensitivity by adding items and checking
        result.Add("Test.Package");
        result.Contains("test.package").Should().BeTrue();
        result.Contains("TEST.PACKAGE").Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task GetInstallerTypeAsync_WithNullOrEmptyPackageId_ReturnsNull(string? packageId)
    {
        // Act
        var result = await _sut.GetInstallerTypeAsync(packageId!);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetInstalledPackageIdsAsync_WhenComTimedOut_SkipsCom()
    {
        // Arrange: mark COM as timed out so it returns false immediately
        _comSession.ComInitTimedOut = true;
        _mockInteractiveUserService.Setup(s => s.IsOtsElevation).Returns(false);
        _mockInteractiveUserService
            .Setup(s => s.GetInteractiveUserFolderPath(It.IsAny<Environment.SpecialFolder>()))
            .Returns(@"C:\Users\TestUser\AppData\Local");
        _mockFileSystemService
            .Setup(f => f.CombinePath(It.IsAny<string[]>()))
            .Returns((string[] paths) => string.Join(@"\", paths));
        _mockFileSystemService
            .Setup(f => f.CreateDirectory(It.IsAny<string>()));
        _mockFileSystemService
            .Setup(f => f.FileExists(It.IsAny<string>()))
            .Returns(false);

        // Act
        var result = await _sut.GetInstalledPackageIdsAsync();

        // Assert: should fall through to CLI fallback
        result.Should().NotBeNull();
        _mockLogService.Verify(
            l => l.LogInformation(It.Is<string>(s => s.Contains("COM not available, falling back to CLI"))),
            Times.Once);
    }

    [Fact]
    public async Task GetInstallerTypeAsync_LogsWarningOnException()
    {
        // Arrange: setup to force an exception in the GetInstallerTypeAsync path
        _comSession.ComInitTimedOut = true;
        _mockInteractiveUserService.Setup(s => s.IsOtsElevation)
            .Throws(new InvalidOperationException("Test exception"));

        // Act
        var result = await _sut.GetInstallerTypeAsync("Test.Package");

        // Assert
        result.Should().BeNull();
        _mockLogService.Verify(
            l => l.LogWarning(It.Is<string>(s => s.Contains("Could not determine installer type"))),
            Times.Once);
    }
}
