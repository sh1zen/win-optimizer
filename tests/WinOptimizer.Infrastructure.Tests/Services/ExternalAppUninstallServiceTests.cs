using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.SoftwareApps.Enums;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

public class ExternalAppUninstallServiceTests
{
    private readonly Mock<IWinGetPackageInstaller> _winGetPackageInstaller = new();
    private readonly Mock<IChocolateyService> _chocolateyService = new();
    private readonly Mock<ILogService> _logService = new();
    private readonly Mock<IUninstallCommandSource> _uninstallCommands = new();
    private readonly Mock<ITaskProgressService> _taskProgressService = new();
    private readonly Mock<IProcessExecutor> _processExecutor = new();
    private readonly Mock<IUninstallResidueReview> _review = new();
    private readonly Mock<IAppxDeployment> _deployment = new(MockBehavior.Strict);

    public ExternalAppUninstallServiceTests()
    {
        _review.Setup(review => review.ConfirmAutomaticUninstallAsync(It.IsAny<ItemDefinition>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
    }

    private ExternalAppUninstallService CreateSut() => new(
        _winGetPackageInstaller.Object,
        _chocolateyService.Object,
        _logService.Object,
        _uninstallCommands.Object,
        _taskProgressService.Object,
        _processExecutor.Object,
        _review.Object,
        _deployment.Object,
        Mock.Of<IInteractiveUserService>());

    [Theory]
    [InlineData("")]
    [InlineData("S-1-5-21-1-2-3-1001")]
    public async Task AppxRemovalKeepsTheAccountAndFiltersPackageIdentities(string sid)
    {
        var item = new ItemDefinition { Id = "appx", Name = "App", Description = "Test", DetectedVia = DetectionSource.AppX, AppxPackageName = ["Wanted.App"] };
        _deployment.SetupGet(source => source.InteractiveAccountSid).Returns(sid);
        _deployment.Setup(source => source.GetPackagesAsync(sid, It.IsAny<CancellationToken>())).ReturnsAsync([
            new AppxPackageIdentity("Wanted.App", "Wanted.App_1_x64__publisher"),
            new AppxPackageIdentity("Other.App", "Other.App_1_x64__publisher")]);
        _deployment.Setup(source => source.RemovePackageAsync("Wanted.App_1_x64__publisher", sid, It.IsAny<Action<int>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, Action<int>, CancellationToken>((_, _, report, _) => report(50)).Returns(Task.CompletedTask);

        Assert.True((await CreateSut().UninstallAsync(item)).Success);

        _deployment.Verify(source => source.RemovePackageAsync("Wanted.App_1_x64__publisher", sid, It.IsAny<Action<int>>(), It.IsAny<CancellationToken>()), Times.Once);
        _deployment.Verify(source => source.RemovePackageAsync("Other.App_1_x64__publisher", It.IsAny<string>(), It.IsAny<Action<int>>(), It.IsAny<CancellationToken>()), Times.Never);
        _taskProgressService.Verify(service => service.UpdateProgress(50, It.IsAny<string>()), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledAppxRemovalDoesNotReportSuccess(bool cancelled)
    {
        var item = new ItemDefinition { Id = "appx", Name = "App", Description = "Test", DetectedVia = DetectionSource.AppX, AppxPackageName = ["Wanted.App"] };
        _deployment.SetupGet(source => source.InteractiveAccountSid).Returns("S-1-5-21-1-2-3-1001");
        _deployment.Setup(source => source.GetPackagesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AppxPackageIdentity("Wanted.App", "Wanted.App_1_x64__publisher")]);
        _deployment.Setup(source => source.RemovePackageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Action<int>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(cancelled ? new OperationCanceledException() : new IOException("Deployment denied"));

        Assert.False((await CreateSut().UninstallAsync(item)).Success);

        _taskProgressService.Verify(service => service.UpdateProgress(100, It.IsAny<string>()), Times.Never);
        _chocolateyService.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(0, true, false)]
    [InlineData(3010, true, true)]
    [InlineData(1641, true, true)]
    [InlineData(1603, false, false)]
    [InlineData(null, false, false)]
    public async Task RegistryUninstallAwaitsProcessAndHonorsExitCode(int? exitCode, bool success, bool deferred)
    {
        var item = new ItemDefinition { Id = "registry-app", Name = "Registry app", Description = "Test", DetectedVia = DetectionSource.Registry };
        _uninstallCommands.Setup(source => source.GetAsync(item)).ReturnsAsync((true, @"C:\Test\uninstall.exe /quiet"));
        _processExecutor.Setup(executor => executor.ShellExecuteAsync(@"C:\Test\uninstall.exe", "", true, CancellationToken.None)).ReturnsAsync(exitCode);
        var result = await CreateSut().UninstallAsync(item);
        Assert.Equal(success, result.Success);
        Assert.Equal(deferred, result.InfoMessage != null);
        _processExecutor.Verify(executor => executor.ShellExecuteAsync(@"C:\Test\uninstall.exe", "", true, CancellationToken.None), Times.Once);
        _review.Verify(review => review.ConfirmAutomaticUninstallAsync(It.IsAny<ItemDefinition>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\unins000.exe\" /VERYSILENT /NORESTART", @"C:\Program Files\App\unins000.exe", "/NORESTART")]
    [InlineData(@"C:\Program Files\App\setup.exe --uninstall /S", @"C:\Program Files\App\setup.exe", "--uninstall")]
    [InlineData("MsiExec.exe /I{01234567-89AB-CDEF-0123-456789ABCDEF} /qn", "MsiExec.exe", "/X{01234567-89AB-CDEF-0123-456789ABCDEF}")]
    [InlineData("msiexec /i {01234567-89AB-CDEF-0123-456789ABCDEF} /QUIET", "msiexec", "/X {01234567-89AB-CDEF-0123-456789ABCDEF}")]
    [InlineData("\"C:\\App\\remove.exe\" --uninstall \"C:\\Data\\silent folder\"", @"C:\App\remove.exe", "--uninstall \"C:\\Data\\silent folder\"")]
    [InlineData("\"C:\\App\\remove.exe\" --log \"C:\\Data /S folder\\log.txt\" /silent", @"C:\App\remove.exe", "--log \"C:\\Data /S folder\\log.txt\"")]
    public async Task NativeUninstallEnablesUiAndPreservesRequiredArguments(string command, string executable, string arguments)
    {
        var item = new ItemDefinition { Id = "app", Name = "App", Description = "Test", WinGetPackageId = ["Publisher.App"] };
        _uninstallCommands.Setup(source => source.GetAsync(item)).ReturnsAsync((true, command));
        _processExecutor.Setup(executor => executor.ShellExecuteAsync(executable, arguments, true, CancellationToken.None)).ReturnsAsync(0);

        Assert.True((await CreateSut().UninstallAsync(item)).Success);

        _processExecutor.Verify(executor => executor.ShellExecuteAsync(executable, arguments, true, CancellationToken.None), Times.Once);
        _uninstallCommands.Verify(source => source.GetAsync(item), Times.Once);
        _winGetPackageInstaller.VerifyNoOtherCalls();
        _review.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task NativeUninstallerMustExitBeforeCompletionEvenWhenCancellationIsRequested()
    {
        var item = new ItemDefinition { Id = "app", Name = "App", Description = "Test", WinGetPackageId = ["Publisher.App"] };
        using var cancellation = new CancellationTokenSource();
        var exited = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _uninstallCommands.Setup(source => source.GetAsync(item)).ReturnsAsync((true, @"C:\App\remove.exe"));
        _processExecutor.Setup(executor => executor.ShellExecuteAsync(@"C:\App\remove.exe", "", true, CancellationToken.None)).Returns(exited.Task);

        var pending = CreateSut().UninstallAsync(item, cancellationToken: cancellation.Token);
        Assert.False(pending.IsCompleted);
        cancellation.Cancel();
        Assert.False(pending.IsCompleted);
        exited.SetResult(0);

        var result = await pending;
        Assert.False(result.Success);
        Assert.Contains("cancelled", result.ErrorMessage);
        _review.VerifyNoOtherCalls();
        _winGetPackageInstaller.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(1602)]
    [InlineData(1603)]
    public async Task NativeCancellationOrFailureDoesNotStartAutomaticUninstall(int exitCode)
    {
        var item = new ItemDefinition { Id = "app", Name = "App", Description = "Test", WinGetPackageId = ["Publisher.App"] };
        _uninstallCommands.Setup(source => source.GetAsync(item)).ReturnsAsync((true, @"C:\App\remove.exe"));
        _processExecutor.Setup(executor => executor.ShellExecuteAsync(@"C:\App\remove.exe", "", true, CancellationToken.None)).ReturnsAsync(exitCode);

        Assert.False((await CreateSut().UninstallAsync(item)).Success);

        _review.VerifyNoOtherCalls();
        _winGetPackageInstaller.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingNativeUninstallerWaitsForExplicitAutomaticConsent(bool consent)
    {
        var item = new ItemDefinition { Id = "app", Name = "App", Description = "Test", WinGetPackageId = ["Publisher.App"] };
        var response = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _review.Setup(review => review.ConfirmAutomaticUninstallAsync(item, It.IsAny<CancellationToken>())).Returns(response.Task);
        _winGetPackageInstaller.Setup(installer => installer.UninstallPackageAsync("Publisher.App", "winget", "App", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var pending = CreateSut().UninstallAsync(item);
        Assert.False(pending.IsCompleted);
        _winGetPackageInstaller.VerifyNoOtherCalls();
        response.SetResult(consent);
        Assert.Equal(consent, (await pending).Success);

        _winGetPackageInstaller.Verify(installer => installer.UninstallPackageAsync("Publisher.App", "winget", "App", It.IsAny<CancellationToken>()), consent ? Times.Once() : Times.Never());
        _processExecutor.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DecliningAutomaticFileRemovalPreservesDetectedData()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"WinOptimizerTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "retained.txt");
        await File.WriteAllTextAsync(file, "Retain this data");
        try
        {
            var item = new ItemDefinition { Id = "app", Name = "App", Description = "Test", DetectedVia = DetectionSource.FileSystem, DetectionPaths = [folder] };
            _review.Setup(review => review.ConfirmAutomaticUninstallAsync(item, It.IsAny<CancellationToken>())).ReturnsAsync(false);
            Assert.False((await CreateSut().UninstallAsync(item)).Success);
            Assert.Equal("Retain this data", await File.ReadAllTextAsync(file));
        }
        finally { Directory.Delete(folder, true); }
    }

    // --- UninstallAsync: FileSystem method ---

    [Fact]
    public async Task UninstallAsync_FileSystemDetectedWithExistingDirectory_ReturnsSuccess()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"WinOptimizerTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var item = new ItemDefinition
            {
                Id = "fs-app",
                Name = "FileSystem App",
                Description = "App detected via filesystem",
                DetectedVia = DetectionSource.FileSystem,
                DetectionPaths = new[] { tempDir }
            };

            var sut = CreateSut();
            var result = await sut.UninstallAsync(item);

            result.Success.Should().BeTrue();
            Directory.Exists(tempDir).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task UninstallAsync_FileSystemDetectedWithNonExistentPath_ReturnsFailed()
    {
        var item = new ItemDefinition
        {
            Id = "fs-app",
            Name = "FileSystem App",
            Description = "App detected via filesystem",
            DetectedVia = DetectionSource.FileSystem,
            DetectionPaths = new[] { @"C:\NonExistent\Path\That\Does\Not\Exist_" + Guid.NewGuid() }
        };

        var sut = CreateSut();
        var result = await sut.UninstallAsync(item);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("No files found to remove");
    }

    [Fact]
    public async Task UninstallAsync_FileSystemDetectedWithNoDetectionPaths_ReturnsFailed()
    {
        var item = new ItemDefinition
        {
            Id = "fs-app",
            Name = "FileSystem App",
            Description = "App detected via filesystem",
            DetectedVia = DetectionSource.FileSystem,
            DetectionPaths = Array.Empty<string>()
        };

        var sut = CreateSut();
        var result = await sut.UninstallAsync(item);

        result.Success.Should().BeFalse();
        // Empty DetectionPaths means FileSystem guard clause is not matched,
        // so it falls through to "No uninstall method available"
        result.ErrorMessage.Should().Contain("No uninstall method available");
    }

    [Fact]
    public async Task UninstallAsync_FileSystemDetectedWithExistingFile_DeletesFileAndReturnsSuccess()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"WinOptimizerTest_{Guid.NewGuid():N}.txt");
        File.WriteAllText(tempFile, "test");

        try
        {
            var item = new ItemDefinition
            {
                Id = "fs-app",
                Name = "FileSystem App",
                Description = "App detected via filesystem",
                DetectedVia = DetectionSource.FileSystem,
                DetectionPaths = new[] { tempFile }
            };

            var sut = CreateSut();
            var result = await sut.UninstallAsync(item);

            result.Success.Should().BeTrue();
            File.Exists(tempFile).Should().BeFalse();
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task UninstallAsync_FileSystemCancellation_ReturnsCancelled()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var item = new ItemDefinition
        {
            Id = "fs-app",
            Name = "FileSystem App",
            Description = "App detected via filesystem",
            DetectedVia = DetectionSource.FileSystem,
            DetectionPaths = new[] { Path.GetTempPath() }
        };

        var sut = CreateSut();
        var result = await sut.UninstallAsync(item, cancellationToken: cts.Token);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("cancelled");
    }

    // --- UninstallAsync: WinGet method ---

    [Fact]
    public async Task UninstallAsync_WinGetDetected_UsesWinGetUninstall()
    {
        var item = new ItemDefinition
        {
            Id = "winget-app",
            Name = "WinGet App",
            Description = "App detected via WinGet",
            DetectedVia = DetectionSource.WinGet,
            WinGetPackageId = new[] { "Publisher.App" }
        };

        _winGetPackageInstaller
            .Setup(w => w.UninstallPackageAsync("Publisher.App", "winget", "WinGet App", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sut = CreateSut();
        var result = await sut.UninstallAsync(item);

        result.Success.Should().BeTrue();
        _winGetPackageInstaller.Verify(
            w => w.UninstallPackageAsync("Publisher.App", "winget", "WinGet App", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // --- UninstallAsync: Chocolatey method ---

    [Fact]
    public async Task UninstallAsync_ChocolateyDetected_UsesChocolateyUninstall()
    {
        var item = new ItemDefinition
        {
            Id = "choco-app",
            Name = "Choco App",
            Description = "App detected via Chocolatey",
            DetectedVia = DetectionSource.Chocolatey,
            ChocoPackageId = "chocoapp"
        };

        _chocolateyService
            .Setup(c => c.IsChocolateyInstalledAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _chocolateyService
            .Setup(c => c.UninstallPackageAsync("chocoapp", "Choco App", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sut = CreateSut();
        var result = await sut.UninstallAsync(item);

        result.Success.Should().BeTrue();
        _chocolateyService.Verify(
            c => c.UninstallPackageAsync("chocoapp", "Choco App", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // --- UninstallAsync: No method available ---

    [Fact]
    public async Task UninstallAsync_NoUninstallMethodAvailable_ReturnsFailed()
    {
        var item = new ItemDefinition
        {
            Id = "no-method-app",
            Name = "No Method App",
            Description = "App with no uninstall method"
        };

        var sut = CreateSut();
        var result = await sut.UninstallAsync(item);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("No uninstall method available");
    }
}
