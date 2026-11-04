using FluentAssertions;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Services.WinGet.Utilities;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Utilities;

public class WinGetExitCodesTests
{
    [Fact]
    public void IsUninstallVerifiable_ExecUninstallCommandFailed_ReturnsTrue()
    {
        WinGetExitCodes.IsUninstallVerifiable(WinGetExitCodes.ExecUninstallCommandFailed)
            .Should().BeTrue();
    }

    [Fact]
    public void IsUninstallVerifiable_NoUninstallInfoFound_ReturnsTrue()
    {
        WinGetExitCodes.IsUninstallVerifiable(WinGetExitCodes.NoUninstallInfoFound)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(WinGetExitCodes.Ok)]
    [InlineData(WinGetExitCodes.PackageNotFound)]
    [InlineData(1)]
    public void IsUninstallVerifiable_OtherCodes_ReturnsFalse(int exitCode)
    {
        WinGetExitCodes.IsUninstallVerifiable(exitCode).Should().BeFalse();
    }

    /// <summary>Checks classification and failure reasons using literal process return codes.</summary>
    [Theory]
    [InlineData(0u, true, InstallFailureReason.None)]
    [InlineData(1u, false, InstallFailureReason.Other)]
    [InlineData(uint.MaxValue, false, InstallFailureReason.Other)]
    [InlineData(0x8A150019u, false, InstallFailureReason.Other)]
    [InlineData(0x8A150015u, false, InstallFailureReason.Other)]
    [InlineData(0x8A15002Au, false, InstallFailureReason.Other)]
    [InlineData(0x8A150011u, false, InstallFailureReason.HashMismatchOrInstallError)]
    [InlineData(0x8A150008u, false, InstallFailureReason.DownloadError)]
    [InlineData(0x8A15003Au, false, InstallFailureReason.BlockedByPolicy)]
    [InlineData(0x8A150010u, false, InstallFailureReason.NoApplicableInstallers)]
    [InlineData(0x8A150041u, false, InstallFailureReason.AgreementsNotAccepted)]
    [InlineData(0x8A150107u, false, InstallFailureReason.NetworkError)]
    [InlineData(0x8A150005u, false, InstallFailureReason.UserCancelled)]
    [InlineData(0x8A15010Cu, false, InstallFailureReason.UserCancelled)]
    [InlineData(0x8A150014u, false, InstallFailureReason.PackageNotFound)]
    [InlineData(0x8A150013u, false, InstallFailureReason.Other)]
    [InlineData(0x8A150061u, true, InstallFailureReason.None)]
    [InlineData(0x8A15002Bu, true, InstallFailureReason.None)]
    [InlineData(0x8A150109u, true, InstallFailureReason.None)]
    public void BundledReleaseReturnCodesHaveTheirActualMeaning(uint raw, bool success, InstallFailureReason reason)
    {
        var exitCode = unchecked((int)raw);
        Assert.Equal(success, WinGetExitCodes.IsSuccess(exitCode));
        Assert.Equal(reason, WinGetExitCodes.MapExitCode(exitCode));
    }
}
