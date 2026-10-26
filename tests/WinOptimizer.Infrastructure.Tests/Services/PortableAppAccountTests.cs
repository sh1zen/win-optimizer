using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Enums;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

/// <summary>Exercises portable-app discovery and removal through isolated account and provider boundaries.</summary>
public sealed class PortableAppAccountTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "win-optimizer-portable-tests", Guid.NewGuid().ToString("N"));
    private readonly Mock<IInteractiveUserService> _user = new(MockBehavior.Strict);
    private readonly Mock<IChocolateyService> _choco = new();
    private readonly Mock<IUninstallResidueReview> _review = new();

    public PortableAppAccountTests()
    {
        Directory.CreateDirectory(_root);
        _choco.Setup(source => source.GetInstalledPackageIdsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new HashSet<string>());
        _review.Setup(review => review.ConfirmAutomaticUninstallAsync(It.IsAny<ItemDefinition>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
    }

    private AppStatusDiscoveryService Discovery()
    {
        var installed = new Mock<IInstalledProgramSource>();
        installed.Setup(source => source.ReadAsync()).ReturnsAsync(Array.Empty<InstalledProgram>());
        var metadata = new Mock<IAppMetadataSource>();
        metadata.Setup(source => source.ReadAsync(It.IsAny<IReadOnlyList<ItemDefinition>>())).ReturnsAsync(new Dictionary<string, AppMetadata>());
        return new(Mock.Of<ILogService>(), Mock.Of<IWinGetBootstrapper>(), Mock.Of<IWinGetDetectionService>(),
            _choco.Object, installed.Object, Mock.Of<IAppxPackageSource>(), Mock.Of<IWindowsComponentStatusSource>(), metadata.Object, _user.Object);
    }

    private ExternalAppUninstallService Uninstaller()
    {
        var commands = new Mock<IUninstallCommandSource>();
        commands.Setup(source => source.GetAsync(It.IsAny<ItemDefinition>())).ReturnsAsync((false, ""));
        return new(Mock.Of<IWinGetPackageInstaller>(), _choco.Object, Mock.Of<ILogService>(), commands.Object,
            Mock.Of<ITaskProgressService>(), Mock.Of<IProcessExecutor>(), _review.Object, Mock.Of<IAppxDeployment>(), _user.Object);
    }

    /// <summary>Normal and alternate-account launches discover and remove the same interactive-account installation.</summary>
    [Theory]
    [InlineData("%USERPROFILE%", Environment.SpecialFolder.UserProfile, false)]
    [InlineData("%userprofile%", Environment.SpecialFolder.UserProfile, true)]
    [InlineData("%LOCALAPPDATA%", Environment.SpecialFolder.LocalApplicationData, false)]
    [InlineData("%localappdata%", Environment.SpecialFolder.LocalApplicationData, true)]
    [InlineData("%APPDATA%", Environment.SpecialFolder.ApplicationData, false)]
    [InlineData("%appdata%", Environment.SpecialFolder.ApplicationData, true)]
    public async Task DetectionAndRemovalUseInteractiveFolders(string variable, Environment.SpecialFolder folder, bool alternateAccount)
    {
        var accountRoot = Path.Combine(_root, "interactive", folder.ToString());
        var app = Path.Combine(accountRoot, "Desktop", "Tor Browser");
        var otherAccountApp = Path.Combine(_root, "administrator", "Desktop", "Tor Browser");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(otherAccountApp);
        File.WriteAllText(Path.Combine(app, "browser.exe"), "isolated app");
        File.WriteAllText(Path.Combine(otherAccountApp, "browser.exe"), "other account");
        _user.SetupGet(source => source.IsOtsElevation).Returns(alternateAccount);
        _user.Setup(source => source.GetInteractiveUserFolderPath(folder)).Returns(accountRoot);
        var item = ExternalAppDefinitions.GetExternalApps().Items.Single(item => item.Id == "external-app-tor") with
            { DetectionPaths = [variable + @"\Desktop\Tor Browser"] };

        var status = await Discovery().GetExternalAppsInstallationStatusAsync([item]);

        status[item.Id].Should().BeTrue();
        item.DetectedVia.Should().Be(DetectionSource.FileSystem);
        var result = await Uninstaller().UninstallAsync(item);
        result.Success.Should().BeTrue();
        Directory.Exists(app).Should().BeFalse();
        File.ReadAllText(Path.Combine(otherAccountApp, "browser.exe")).Should().Be("other account");
        _user.Verify(source => source.GetInteractiveUserFolderPath(folder), Times.Exactly(2));
    }

    /// <summary>Missing or unresolved account folders fail closed before deleting any selected path.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData("%USERPROFILE%")]
    public async Task UnavailableAccountFoldersPreventDetectionAndPartialDeletion(string folder)
    {
        _user.Setup(source => source.GetInteractiveUserFolderPath(Environment.SpecialFolder.UserProfile)).Returns(folder);
        var item = new ItemDefinition { Id = "portable", Name = "Portable", Description = "Test",
            DetectedVia = DetectionSource.FileSystem, DetectionPaths = [@"%USERPROFILE%\Desktop\Portable"] };
        (await Discovery().GetExternalAppsInstallationStatusAsync([item]))[item.Id].Should().BeFalse();
        var preserved = Path.Combine(_root, "preserved.exe");
        File.WriteAllText(preserved, "preserve on resolution failure");

        var result = await Uninstaller().UninstallAsync(item with { DetectionPaths = [preserved, .. item.DetectionPaths!] });

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("interactive account");
        File.ReadAllText(preserved).Should().Be("preserve on resolution failure");
    }

    /// <summary>Absolute machine paths need no account-folder lookup.</summary>
    [Fact]
    public void AbsoluteDetectionPathsPreserveTheirLocation()
    {
        var path = Path.Combine(_root, "machine-app.exe");
        AppPaths.ResolveDetectionPath(path, _user.Object).Should().Be(path);
        _user.VerifyNoOtherCalls();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
