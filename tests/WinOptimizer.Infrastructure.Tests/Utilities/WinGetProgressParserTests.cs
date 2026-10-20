using FluentAssertions;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Services.WinGet.Utilities;
using Xunit;
using static WinOptimizer.Infrastructure.Features.SoftwareApps.Services.WinGet.Utilities.WinGetProgressParser;

namespace WinOptimizer.Infrastructure.Tests.Utilities;

public class WinGetProgressParserTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("InstallationDisclaimer1")]
    [InlineData("InstallationDisclaimer2")]
    [InlineData("PackageDependencies")]
    [InlineData("(1/3) InstallationDisclaimer1")]
    public void TranslateLine_EmptyOrSuppressedOutput_ReturnsNull(string? input)
    {
        WinGetProgressParser.TranslateLine(input).Should().BeNull();
    }

    [Theory]
    [InlineData("PackageRequiresDependencies", "Installing dependencies...")]
    [InlineData("InstallerHashVerified", "Hash verified")]
    [InlineData("ExtractingArchive", "Extracting archive...")]
    [InlineData("ExtractArchiveSucceeded", "Archive extracted")]
    [InlineData("InstallFlowStartingPackageInstall", "Installing...")]
    [InlineData("InstallFlowInstallSuccess", "Installation successful")]
    [InlineData("SourceOpenFailedSuggestion", "WinGet source unavailable")]
    [InlineData("UnexpectedErrorExecutingCommand", "Unexpected error")]
    [InlineData("InstallingDependencies", "Installing dependencies...")]
    [InlineData("installerhashverified", "Hash verified")]
    [InlineData("INSTALLERHASHVERIFIED", "Hash verified")]
    [InlineData("(1/2) PackageRequiresDependencies", "[1/2] Installing dependencies...")]
    [InlineData("(2/3) Some normal text", "[2/3] Some normal text")]
    [InlineData("Downloading https://example.com/packages/installer-v1.2.3.exe", "Downloading installer-v1.2.3.exe...")]
    [InlineData("Downloading https://example.com/", "Downloading...")]
    [InlineData("ReportIdentityFound Google Chrome [Google.Chrome] ShowVersion 120.0.1", "Found: Google Chrome v120.0.1")]
    [InlineData("0x8a15000f : Something went wrong", "Error: Something went wrong")]
    [InlineData("Some random winget output text", "Some random winget output text")]
    [InlineData("   Some random winget output text   ", "Some random winget output text")]
    [InlineData("  - Microsoft.VCRedist.2015+.x64", "- Microsoft.VCRedist.2015+.x64")]
    public void TranslateLine_NativeOutput_ReturnsExpectedText(string input, string expected)
    {
        WinGetProgressParser.TranslateLine(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("Some random unrecognized output")]
    public void ParseLine_EmptyOrUnrecognizedOutput_ReturnsNull(string? input)
    {
        WinGetProgressParser.ParseLine(input).Should().BeNull();
    }

    [Theory]
    [InlineData("Found Google Chrome [Google.Chrome]", WinGetPhase.Found, null)]
    [InlineData("package found: Google Chrome", WinGetPhase.Found, null)]
    [InlineData("ReportIdentityFound Chrome [Google.Chrome] ShowVersion 120.0", WinGetPhase.Found, null)]
    [InlineData("Downloading 52%", WinGetPhase.Downloading, 52d)]
    [InlineData("██████████████████████████████  100%", WinGetPhase.Downloading, 100d)]
    [InlineData("Download progress: 52.3%", WinGetPhase.Downloading, 52.3d)]
    [InlineData("1.2 MB / 2.4 MB", WinGetPhase.Downloading, 50d)]
    [InlineData("0.5 GB / 1.0 GB", WinGetPhase.Downloading, 50d)]
    [InlineData("Installing package...", WinGetPhase.Installing, null)]
    [InlineData("Starting package install...", WinGetPhase.Installing, null)]
    [InlineData("InstallFlowStartingPackageInstall", WinGetPhase.Installing, null)]
    [InlineData("InstallerHashVerified", WinGetPhase.Installing, null)]
    [InlineData("ExtractingArchive", WinGetPhase.Installing, null)]
    [InlineData("ExtractArchiveSucceeded", WinGetPhase.Installing, null)]
    [InlineData("Installing 75%", WinGetPhase.Installing, 75d)]
    [InlineData("Uninstalling package...", WinGetPhase.Uninstalling, null)]
    [InlineData("Successfully installed", WinGetPhase.Complete, 100d)]
    [InlineData("Installation successful", WinGetPhase.Complete, 100d)]
    [InlineData("Successfully uninstalled", WinGetPhase.Complete, 100d)]
    [InlineData("Uninstall successful", WinGetPhase.Complete, 100d)]
    [InlineData("InstallFlowInstallSuccess", WinGetPhase.Complete, 100d)]
    [InlineData("No applicable installer found", WinGetPhase.Error, null)]
    [InlineData("Downloading...", WinGetPhase.Downloading, null)]
    [InlineData("Progress: 45%", WinGetPhase.Downloading, 45d)]
    [InlineData("No package found matching input criteria", WinGetPhase.Found, null)]
    [InlineData("No installed package found", WinGetPhase.Found, null)]
    public void ParseLine_RecognizedOutput_ReturnsPhaseAndProgress(string input, WinGetPhase phase, double? percent)
    {
        var result = WinGetProgressParser.ParseLine(input);

        result.Should().NotBeNull();
        result!.Phase.Should().Be(phase);
        result.Percent.Should().Be(percent);
    }

    [Fact]
    public void WinGetProgressInfo_EqualityUsesRecordValues()
    {
        var progress = new WinGetProgressInfo(WinGetPhase.Downloading, 50);

        progress.Should().Be(new WinGetProgressInfo(WinGetPhase.Downloading, 50));
        progress.Should().NotBe(new WinGetProgressInfo(WinGetPhase.Installing, 50));
    }
}
