using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Infrastructure.Features.Common.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

public class WindowsCompatibilityFilterTests
{
    private readonly Mock<IWindowsVersionService> _mockVersionService = new();
    private readonly Mock<ILogService> _mockLogService = new();

    #region FilterSettingsByWindowsVersion (with applyFilter=true)

    [Fact]
    public void FilterSettingsByWindowsVersion_NoRestrictions_ReturnsAllSettings()
    {
        // Arrange
        _mockVersionService.Setup(v => v.IsWindows11()).Returns(true);
        _mockVersionService.Setup(v => v.GetWindowsBuildNumber()).Returns(22621);

        var filter = CreateFilter();
        var settings = new List<SettingDefinition>
        {
            CreateSetting("s1"),
            CreateSetting("s2"),
            CreateSetting("s3")
        };

        // Act
        var result = filter.FilterSettingsByWindowsVersion(settings);

        // Assert
        result.Should().HaveCount(3);
    }

    [Fact]
    public void FilterSettingsByWindowsVersion_Windows10OnlySetting_OnWindows11_FilteredOut()
    {
        // Arrange
        _mockVersionService.Setup(v => v.IsWindows11()).Returns(true);
        _mockVersionService.Setup(v => v.GetWindowsBuildNumber()).Returns(22621);

        var filter = CreateFilter();
        var settings = new List<SettingDefinition>
        {
            CreateSetting("win10only", isWindows10Only: true),
            CreateSetting("normal")
        };

        // Act
        var result = filter.FilterSettingsByWindowsVersion(settings);

        // Assert
        result.Should().ContainSingle()
            .Which.Id.Should().Be("normal");
    }

    [Fact]
    public void FilterSettingsByWindowsVersion_Windows11OnlySetting_OnWindows10_FilteredOut()
    {
        // Arrange
        _mockVersionService.Setup(v => v.IsWindows11()).Returns(false);
        _mockVersionService.Setup(v => v.GetWindowsBuildNumber()).Returns(19045);

        var filter = CreateFilter();
        var settings = new List<SettingDefinition>
        {
            CreateSetting("win11only", isWindows11Only: true),
            CreateSetting("normal")
        };

        // Act
        var result = filter.FilterSettingsByWindowsVersion(settings);

        // Assert
        result.Should().ContainSingle()
            .Which.Id.Should().Be("normal");
    }

    [Fact]
    public void FilterSettingsByWindowsVersion_OnWindowsServer_DoesNotFilterSettings()
    {
        // Arrange — Server 2022 (build 20348) detected as Windows 10
        _mockVersionService.Setup(v => v.IsWindows11()).Returns(false);
        _mockVersionService.Setup(v => v.GetWindowsBuildNumber()).Returns(20348);
        _mockVersionService.Setup(v => v.IsWindowsServer()).Returns(true);

        var filter = CreateFilter();
        var settings = new List<SettingDefinition>
        {
            CreateSetting("s1"),
            CreateSetting("s2"),
            CreateSetting("s3")
        };

        // Act
        var result = filter.FilterSettingsByWindowsVersion(settings);

        // Assert — all settings pass through, Server does not cause extra filtering
        result.Should().HaveCount(3);
    }

    #endregion

    #region FilterSettingsByWindowsVersion (with applyFilter=false)

    [Theory]
    [InlineData(22621, null)]
    [InlineData(22000, "Compatibility_BuildRange|22621-22631")]
    public void FilteringAndDecoration_UseTheSameRangePrecedence(int build, string? message)
    {
        _mockVersionService.Setup(v => v.IsWindows11()).Returns(true);
        _mockVersionService.Setup(v => v.GetWindowsBuildNumber()).Returns(build);
        var setting = CreateSetting("range", minimumBuild: 30000, maximumBuild: 19045,
            supportedRanges: [(22621, 22631)]);
        var filter = CreateFilter();

        filter.FilterSettingsByWindowsVersion([setting]).Any().Should().Be(message == null);
        var decorated = filter.FilterSettingsByWindowsVersion([setting], false).Single();
        decorated.VersionCompatibilityMessage.Should().Be(message);
        if (message == null) decorated.Should().BeSameAs(setting);
    }

    [Fact]
    public void FilterSettingsByWindowsVersion_ApplyFilterFalse_ReturnsAllWithCompatibilityMessages()
    {
        // Arrange
        _mockVersionService.Setup(v => v.IsWindows11()).Returns(true);
        _mockVersionService.Setup(v => v.GetWindowsBuildNumber()).Returns(22621);

        var filter = CreateFilter();
        var settings = new List<SettingDefinition>
        {
            CreateSetting("win10only", isWindows10Only: true),
            CreateSetting("normal")
        };

        // Act
        var result = filter.FilterSettingsByWindowsVersion(settings, applyFilter: false).ToList();

        // Assert - all settings returned (not filtered), but win10only gets a compatibility message
        result.Should().HaveCount(2);
        var win10Setting = result.First(s => s.Id == "win10only");
        win10Setting.VersionCompatibilityMessage.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region Helpers

    private WindowsCompatibilityFilter CreateFilter()
    {
        return new WindowsCompatibilityFilter(
            _mockVersionService.Object,
            _mockLogService.Object);
    }

    private static SettingDefinition CreateSetting(
        string id,
        bool isWindows10Only = false,
        bool isWindows11Only = false,
        int? minimumBuild = null,
        int? maximumBuild = null,
        (int MinBuild, int MaxBuild)[]? supportedRanges = null)
    {
        return new SettingDefinition
        {
            Id = id,
            Name = id,
            Description = $"Test setting {id}",
            IsWindows10Only = isWindows10Only,
            IsWindows11Only = isWindows11Only,
            MinimumBuildNumber = minimumBuild,
            MaximumBuildNumber = maximumBuild,
            SupportedBuildRanges = supportedRanges ?? Array.Empty<(int, int)>()
        };
    }

    #endregion
}
