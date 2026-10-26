using FluentAssertions;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Services.WinGet.Utilities;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Utilities;

public class WinGetExitCodesTests
{
    [Theory]
    [InlineData(WinGetExitCodes.Ok)]
    [InlineData(WinGetExitCodes.RestartRequired)]
    [InlineData(WinGetExitCodes.AlreadyInstalled)]
    [InlineData(WinGetExitCodes.UpdateNotApplicable)]
    public void IsSuccess_WithSuccessCodes_ReturnsTrue(int exitCode)
    {
        WinGetExitCodes.IsSuccess(exitCode).Should().BeTrue();
    }

    [Theory]
    [InlineData(WinGetExitCodes.PackageNotFound)]
    [InlineData(WinGetExitCodes.DownloadError)]
    [InlineData(WinGetExitCodes.BlockedByPolicy)]
    [InlineData(WinGetExitCodes.NetworkError)]
    [InlineData(1)]
    [InlineData(-1)]
    public void IsSuccess_WithFailureCodes_ReturnsFalse(int exitCode)
    {
        WinGetExitCodes.IsSuccess(exitCode).Should().BeFalse();
    }

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

    [Theory]
    [InlineData(WinGetExitCodes.Ok, InstallFailureReason.None)]
    [InlineData(WinGetExitCodes.RestartRequired, InstallFailureReason.None)]
    [InlineData(WinGetExitCodes.AlreadyInstalled, InstallFailureReason.None)]
    [InlineData(WinGetExitCodes.PackageNotFound, InstallFailureReason.PackageNotFound)]
    [InlineData(WinGetExitCodes.DownloadError, InstallFailureReason.DownloadError)]
    [InlineData(WinGetExitCodes.NetworkError, InstallFailureReason.NetworkError)]
    [InlineData(WinGetExitCodes.BlockedByPolicy, InstallFailureReason.BlockedByPolicy)]
    [InlineData(WinGetExitCodes.NoApplicableInstallers, InstallFailureReason.NoApplicableInstallers)]
    [InlineData(WinGetExitCodes.PackageAgreementsNotAccepted, InstallFailureReason.AgreementsNotAccepted)]
    [InlineData(WinGetExitCodes.InstallerHashMismatch, InstallFailureReason.HashMismatchOrInstallError)]
    [InlineData(WinGetExitCodes.OperationCancelled, InstallFailureReason.UserCancelled)]
    [InlineData(WinGetExitCodes.ManifestError, InstallFailureReason.Other)]
    public void MapExitCode_ReturnsCorrectReason(int exitCode, InstallFailureReason expected)
    {
        WinGetExitCodes.MapExitCode(exitCode).Should().Be(expected);
    }

    [Fact]
    public void MapExitCode_UnknownCode_ReturnsOther()
    {
        WinGetExitCodes.MapExitCode(99999).Should().Be(InstallFailureReason.Other);
    }

    /// <summary>Literal HRESULTs from the bundled release keep symbolic constants from masking mapping regressions.</summary>
    [Theory]
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
