using FluentAssertions;
using WinOptimizer.Core.Features.Common.Helpers;
using WinOptimizer.Core.Features.Common.Models;
using Xunit;

namespace WinOptimizer.Core.Tests.Helpers;

public class BuildVersionGateTests
{
    [Theory]
    [InlineData(false, 19045, 0, "Compatibility_Windows11Only")]
    [InlineData(true, 22000, 0, "Compatibility_BuildRange|22621-22631 or 26100-26120")]
    [InlineData(true, 22621, 0, null)]
    [InlineData(true, 22631, 0, null)]
    [InlineData(true, 26100, 0, null)]
    [InlineData(true, 26120, 0, null)]
    [InlineData(true, 26121, 0, "Compatibility_BuildRange|22621-22631 or 26100-26120")]
    public void ExplicitRanges_TakePrecedenceOverBounds(bool windows11, int build, int revision, string? message)
    {
        var setting = new SettingDefinition
        {
            Id = "ranges", Name = "Ranges", Description = "Ranges",
            IsWindows11Only = true,
            SupportedBuildRanges = [(22621, 22631), (26100, 26120)],
            MinimumBuildNumber = 30000, MinimumBuildRevision = 100,
            MaximumBuildNumber = 19045
        };
        BuildVersionGate.GetCompatibilityMessage(setting, windows11, build, revision).Should().Be(message);
    }

    [Theory]
    [InlineData(26099, 9999, "Compatibility_MinBuild|26100")]
    [InlineData(26100, 99, "Compatibility_MinBuild|26100.100")]
    [InlineData(26100, 100, null)]
    [InlineData(26101, 0, null)]
    [InlineData(26120, 200, null)]
    [InlineData(26120, 201, "Compatibility_MaxBuild|26120.200")]
    [InlineData(26121, 0, "Compatibility_MaxBuild|26120")]
    public void CompatibilityMessages_RespectBoundaryRevisions(int build, int revision, string? message)
    {
        var setting = new SettingDefinition
        {
            Id = "bounds", Name = "Bounds", Description = "Bounds",
            MinimumBuildNumber = 26100, MinimumBuildRevision = 100,
            MaximumBuildNumber = 26120, MaximumBuildRevision = 200
        };
        BuildVersionGate.GetCompatibilityMessage(setting, true, build, revision).Should().Be(message);
    }

    [Fact]
    public void NoConstraints_IsCompatible()
    {
        BuildVersionGate.IsCompatible(26100, 7171, null, null, null, null).Should().BeTrue();
    }
}
