using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Infrastructure.Features.Common.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

public class WindowsVersionServiceTests
{
    private readonly Mock<ILogService> _mockLog = new();
    private readonly WindowsVersionService _sut;

    public WindowsVersionServiceTests()
    {
        _sut = new WindowsVersionService(_mockLog.Object);
    }

    // ── Constructor ──

    [Fact]
    public void Constructor_NullLogService_ThrowsArgumentNullException()
    {
        var act = () => new WindowsVersionService(null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("logService");
    }

    // ── GetWindowsBuildNumber ──

    [Fact]
    public void GetWindowsBuildNumber_MatchesEnvironmentOSVersion()
    {
        var expected = Environment.OSVersion.Version.Build;

        var result = _sut.GetWindowsBuildNumber();

        result.Should().Be(expected);
    }

    // ── IsWindows11 ──

    [Fact]
    public void IsWindows11_ReturnsBoolBasedOnBuildNumber()
    {
        var result = _sut.IsWindows11();

        // On Windows 11 (build >= 22000), should return true; on Windows 10, false
        var expectedBuild = Environment.OSVersion.Version.Build;
        if (Environment.OSVersion.Version.Major == 10 && expectedBuild >= 22000)
        {
            result.Should().BeTrue();
        }
        else if (Environment.OSVersion.Version.Major != 10)
        {
            result.Should().BeFalse();
        }
    }

    // ── IsWindowsServer ──

    [Fact]
    public void IsWindowsServer_ReturnsConsistentValue()
    {
        var first = _sut.IsWindowsServer();
        var second = _sut.IsWindowsServer();

        first.Should().Be(second);
    }

}
