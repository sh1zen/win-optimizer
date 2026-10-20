using FluentAssertions;
using WinOptimizer.Core.Features.Common.Models;
using Xunit;

namespace WinOptimizer.Core.Tests.Models;

public class VersionInfoTests
{
    [Theory]
    [InlineData("v26.10.16", 2026, 10, 16)]
    [InlineData("v24.02.29", 2024, 2, 29)]
    public void FromTag_StableVersion_PreservesTagAndReleaseDate(string tag, int year, int month, int day)
    {
        var version = VersionInfo.FromTag(tag);

        version.Version.Should().Be(tag);
        version.ReleaseDate.Should().Be(new DateTime(year, month, day));
    }

    [Theory]
    [InlineData("v26.10.16-beta")]
    [InlineData("v26.10.16-rc.1")]
    [InlineData("v26.10.16-preview")]
    [InlineData("v26.10.16-alpha")]
    [InlineData("v26.02.29")]
    [InlineData("26.10.16")]
    [InlineData("")]
    public void FromTag_UnsupportedTag_ReturnsEmptyVersion(string tag)
    {
        var version = VersionInfo.FromTag(tag);

        version.Version.Should().BeEmpty();
        version.ReleaseDate.Should().Be(DateTime.MinValue);
        version.IsUpdateAvailable.Should().BeFalse();
    }
}
