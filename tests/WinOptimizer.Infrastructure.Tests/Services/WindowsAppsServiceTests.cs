using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

public class WindowsAppsServiceTests
{
    private readonly Mock<ILogService> _logService = new();
    private readonly Mock<IWinGetPackageInstaller> _winGetPackageInstaller = new();
    private readonly Mock<IWinGetBootstrapper> _winGetBootstrapper = new();
    private readonly Mock<IAppStatusDiscoveryService> _appStatusDiscoveryService = new();
    private readonly Mock<IStoreDownloadService> _storeDownloadService = new();
    private readonly Mock<IDialogService> _dialogService = new();
    private readonly Mock<IUserPreferencesService> _userPreferencesService = new();
    private readonly Mock<ITaskProgressService> _taskProgressService = new();
    private readonly Mock<ILocalizationService> _localizationService = new();
    private readonly Mock<ISettingApplicationService> _settingApplicationService = new();
    private readonly Mock<ISystemSettingsDiscoveryService> _systemSettingsDiscoveryService = new();
    private readonly Mock<IAppxPackageSource> _appxPackageSource = new();

    private WindowsAppsService CreateSut() => new(
        _logService.Object,
        _winGetPackageInstaller.Object,
        _winGetBootstrapper.Object,
        _appStatusDiscoveryService.Object,
        _storeDownloadService.Object,
        _dialogService.Object,
        _userPreferencesService.Object,
        _taskProgressService.Object,
        _localizationService.Object,
        _settingApplicationService.Object,
        _systemSettingsDiscoveryService.Object,
        _appxPackageSource.Object);

    [Fact]
    public async Task GetDevicePackageNamesAsync_UsesDeviceInventory()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Microsoft.Hidden" };
        _appxPackageSource.Setup(s => s.GetDevicePackageNamesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(names);
        (await CreateSut().GetDevicePackageNamesAsync()).Should().BeSameAs(names);
    }

    // --- InstallAppAsync: success via WinGet ---

    [Fact]
    public async Task InstallAppAsync_WinGetSucceeds_ReturnsSuccess()
    {
        var sut = CreateSut();
        var item = new ItemDefinition
        {
            Id = "test-app",
            Name = "Test App",
            Description = "A test app",
            MsStoreId = "9NBLGGH4NNS1"
        };

        _winGetPackageInstaller
            .Setup(x => x.InstallPackageAsync(
                "9NBLGGH4NNS1", "msstore", "Test App", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PackageInstallResult.Succeeded());

        var result = await sut.InstallAppAsync(item);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task InstallAppAsync_WinGetSucceedsWithWinGetPackageId_ReturnsSuccess()
    {
        var sut = CreateSut();
        var item = new ItemDefinition
        {
            Id = "test-app",
            Name = "Test App",
            Description = "A test app",
            WinGetPackageId = new[] { "Publisher.TestApp" }
        };

        _winGetPackageInstaller
            .Setup(x => x.InstallPackageAsync(
                "Publisher.TestApp", "winget", "Test App", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PackageInstallResult.Succeeded());

        var result = await sut.InstallAppAsync(item);

        result.Success.Should().BeTrue();
    }

    // --- InstallAppAsync: failure / unsupported ---

    [Fact]
    public async Task InstallAppAsync_NoPackageInfo_ReturnsFailed()
    {
        var sut = CreateSut();
        var item = new ItemDefinition
        {
            Id = "unsupported",
            Name = "Unsupported",
            Description = "No IDs"
        };

        var result = await sut.InstallAppAsync(item);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not supported");
    }

    [Fact]
    public async Task InstallAppAsync_WinGetFailsAndUserDeclinesConsent_ReturnsFailed()
    {
        var sut = CreateSut();
        var item = new ItemDefinition
        {
            Id = "test-app",
            Name = "Test App",
            Description = "A test app",
            MsStoreId = "9NBLGGH4NNS1"
        };

        _winGetPackageInstaller
            .Setup(x => x.InstallPackageAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PackageInstallResult.Failed(InstallFailureReason.Other, "Install failed"));

        // Update policy is not disabled (no blocking)
        _systemSettingsDiscoveryService
            .Setup(x => x.GetSettingStatesAsync(It.IsAny<IEnumerable<SettingDefinition>>()))
            .ReturnsAsync(new Dictionary<string, SettingStateResult>());

        // User has NOT opted to skip confirmation
        _userPreferencesService
            .Setup(x => x.GetPreferenceAsync("StoreDownloadFallback_DontShowAgain", false))
            .ReturnsAsync(false);

        // User declines fallback dialog
        _dialogService
            .Setup(x => x.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()))
            .ReturnsAsync(new ConfirmationResponse { Confirmed = false, CheckboxChecked = false });

        var result = await sut.InstallAppAsync(item);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("cancelled by user");
    }

    [Fact]
    public async Task InstallAppAsync_WinGetFailsAndFallbackSucceeds_ReturnsSuccess()
    {
        var sut = CreateSut();
        var item = new ItemDefinition
        {
            Id = "test-app",
            Name = "Test App",
            Description = "A test app",
            MsStoreId = "9NBLGGH4NNS1"
        };

        _winGetPackageInstaller
            .Setup(x => x.InstallPackageAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PackageInstallResult.Failed(InstallFailureReason.Other, "Install failed"));

        _systemSettingsDiscoveryService
            .Setup(x => x.GetSettingStatesAsync(It.IsAny<IEnumerable<SettingDefinition>>()))
            .ReturnsAsync(new Dictionary<string, SettingStateResult>());

        // User has previously opted to skip confirmation
        _userPreferencesService
            .Setup(x => x.GetPreferenceAsync("StoreDownloadFallback_DontShowAgain", false))
            .ReturnsAsync(true);

        // Fallback download succeeds
        _storeDownloadService
            .Setup(x => x.DownloadAndInstallPackageAsync(
                "9NBLGGH4NNS1", "Test App", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await sut.InstallAppAsync(item);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task InstallAppAsync_ExceptionThrown_ReturnsFailed()
    {
        var sut = CreateSut();
        var item = new ItemDefinition
        {
            Id = "test-app",
            Name = "Test App",
            Description = "A test app",
            MsStoreId = "9NBLGGH4NNS1"
        };

        _winGetPackageInstaller
            .Setup(x => x.InstallPackageAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Unexpected error"));

        var result = await sut.InstallAppAsync(item);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Unexpected error");
    }

    [Fact]
    public async Task InstallAppAsync_OperationCancelled_ReturnsCancelled()
    {
        var sut = CreateSut();
        var item = new ItemDefinition
        {
            Id = "test-app",
            Name = "Test App",
            Description = "A test app",
            MsStoreId = "9NBLGGH4NNS1"
        };

        _winGetPackageInstaller
            .Setup(x => x.InstallPackageAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var result = await sut.InstallAppAsync(item);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("cancelled");
    }

    // --- InstallAppAsync with AppxPackageName (no MsStoreId/WinGetPackageId) ---

    [Fact]
    public async Task InstallAppAsync_OnlyAppxPackageName_UsesItAsPackageId()
    {
        var sut = CreateSut();
        var item = new ItemDefinition
        {
            Id = "test-appx",
            Name = "Test Appx",
            Description = "An appx app",
            AppxPackageName = new[] { "Microsoft.TestApp" }
        };

        _winGetPackageInstaller
            .Setup(x => x.InstallPackageAsync(
                "Microsoft.TestApp", null, "Test Appx", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PackageInstallResult.Succeeded());

        var result = await sut.InstallAppAsync(item);

        result.Success.Should().BeTrue();
        _winGetPackageInstaller.Verify(x => x.InstallPackageAsync(
            "Microsoft.TestApp", null, "Test Appx", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
