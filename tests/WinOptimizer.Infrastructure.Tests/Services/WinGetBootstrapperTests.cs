using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Services.WinGet;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

/// <summary>
/// Checks initial availability, readiness diagnostics and pre-cancelled installation.
/// </summary>
public class WinGetBootstrapperTests
{
    private readonly Mock<ILogService> _mockLogService = new();
    private readonly Mock<ILocalizationService> _mockLocalization = new();
    private readonly Mock<IInteractiveUserService> _mockInteractiveUserService = new();
    private readonly Mock<IPowerShellRunner> _mockPowerShellRunner = new();
    private readonly Mock<IFileSystemService> _mockFileSystemService = new();
    private readonly Mock<ITaskProgressService> _mockTaskProgressService = new();
    private readonly WinGetComSession _comSession;
    private readonly WinGetBootstrapper _sut;

    public WinGetBootstrapperTests()
    {
        _comSession = new WinGetComSession(_mockLogService.Object);

        _sut = new WinGetBootstrapper(
            _comSession,
            _mockLogService.Object,
            _mockLocalization.Object,
            _mockInteractiveUserService.Object,
            _mockPowerShellRunner.Object,
            _mockFileSystemService.Object,
            _mockTaskProgressService.Object,
            new System.Net.Http.HttpClient());
    }

    [Fact]
    public void IsSystemWinGetAvailable_DefaultsToFalse()
    {
        // The backing field _systemWinGetAvailable defaults to false
        _sut.IsSystemWinGetAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task EnsureWinGetReadyAsync_LogsAvailabilityCheck()
    {
        // Arrange
        _mockInteractiveUserService.Setup(s => s.IsOtsElevation).Returns(false);
        _mockFileSystemService.Setup(f => f.FileExists(It.IsAny<string>())).Returns(false);

        // Act
        await _sut.EnsureWinGetReadyAsync();

        // Assert: should log "Checking WinGet availability..."
        _mockLogService.Verify(
            l => l.LogInformation(It.Is<string>(s => s.Contains("Checking WinGet availability"))),
            Times.Once);
    }

    [Fact]
    public async Task EnsureWinGetReadyAsync_LogsSystemWinGetAvailability()
    {
        // Arrange
        _mockInteractiveUserService.Setup(s => s.IsOtsElevation).Returns(false);
        _mockFileSystemService.Setup(f => f.FileExists(It.IsAny<string>())).Returns(false);

        // Act
        await _sut.EnsureWinGetReadyAsync();

        // Assert: should log the system winget availability status
        _mockLogService.Verify(
            l => l.LogInformation(It.Is<string>(s => s.Contains("System winget available:"))),
            Times.Once);
    }

    [Fact]
    public async Task InstallWinGetAsync_WhenCancelled_ReturnsFalse()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act - the method catches OperationCanceledException internally
        var result = await _sut.InstallWinGetAsync(cts.Token);

        // Assert
        result.Should().BeFalse();
    }

}
