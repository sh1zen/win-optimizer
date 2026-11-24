using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

public class ExternalAppsServiceTests
{
    private readonly Mock<ILogService> _logService = new();
    private readonly Mock<IWinGetBootstrapper> _winGetBootstrapper = new();
    private readonly Mock<IAppStatusDiscoveryService> _appStatusDiscoveryService = new();
    private readonly Mock<IExternalAppUninstallService> _externalAppUninstallService = new();
    private readonly Mock<ITaskProgressService> _taskProgressService = new();
    private readonly Mock<IChangeHistoryService> _changeHistoryService = new();
    private readonly Mock<IInstalledProgramSource> _installedPrograms = new();

    public ExternalAppsServiceTests() => _installedPrograms.Setup(source => source.ReadAsync())
        .ReturnsAsync(Array.Empty<InstalledProgram>());

    private ExternalAppsService CreateSut(IAppStatusDiscoveryService? status = null) => new(
        _logService.Object,
        _winGetBootstrapper.Object,
        status ?? _appStatusDiscoveryService.Object,
        _externalAppUninstallService.Object,
        _taskProgressService.Object,
        _changeHistoryService.Object,
        _installedPrograms.Object);

    [Fact]
    public async Task GetAppsAsync_IncludesPoeditAndOtherUncataloguedProgramsWithoutDuplicatingCatalogueMatches()
    {
        var root = WindowsInstalledProgramSource.Roots[0];
        _installedPrograms.Setup(source => source.ReadAsync()).ReturnsAsync(new InstalledProgram[]
        {
            new(root + @"\Poedit_is1", "Poedit", @"C:\Apps\Poedit\unins000.exe", WebsiteUrl: "https://poedit.com/"),
            new(root + @"\VendorTool", "Another installed tool", null),
            new(root + @"\Google Chrome", "Google Chrome", "uninstall.exe"),
            new(root + @"\Mozilla Firefox 130.0 (x64 it)", "Mozilla Firefox (x64 it)", "uninstall.exe"),
            new(root + @"\Microsoft Edge", "Microsoft Edge", "uninstall.exe"),
            new(root + @"\Hidden", "Hidden component", "uninstall.exe", IsSystemComponent: true),
            new(root + @"\Child", "Child component", "uninstall.exe", ParentKeyName: "VendorTool"),
            new(root + @"\Update", "An update", "uninstall.exe", ReleaseType: "Security Update"),
            new(root + @"\Unnamed", "", "uninstall.exe"),
        });

        var apps = (await CreateSut().GetAppsAsync()).ToList();

        var poedit = Assert.Single(apps, item => item.Name == "Poedit");
        Assert.Equal(root + @"\Poedit_is1", poedit.RegistryUninstallKeyPath);
        Assert.Equal("https://poedit.com/", poedit.WebsiteUrl);
        Assert.False(poedit.CanBeReinstalled);
        Assert.Single(apps, item => item.Name == "Google Chrome");
        Assert.Single(apps, item => item.Name == "Mozilla Firefox");
        Assert.DoesNotContain(apps, item => item.Name == "Mozilla Firefox (x64 it)");
        Assert.DoesNotContain(apps, item => item.Name == "Microsoft Edge");
        Assert.Contains(apps, item => item.Name == "Another installed tool");
        Assert.Equal(2, apps.Count(item => item.RegistryUninstallKeyPath != null));
    }

    [Fact]
    public async Task GetAppsAsync_RereadsRegistrationsAndKeepsStableIdsAcrossProductUpdates()
    {
        var program = new InstalledProgram(WindowsInstalledProgramSource.Roots[2] + @"\Vendor", "Vendor 1.0", "remove.exe");
        _installedPrograms.SetupSequence(source => source.ReadAsync())
            .ReturnsAsync(new[] { program })
            .ReturnsAsync(new[] { program with { Name = "Vendor 2.0" } })
            .ReturnsAsync(Array.Empty<InstalledProgram>());
        var service = CreateSut();
        var before = Assert.Single(await service.GetAppsAsync(), item => item.RegistryUninstallKeyPath != null);
        var updated = Assert.Single(await service.GetAppsAsync(), item => item.RegistryUninstallKeyPath != null);
        Assert.Equal(before.Id, updated.Id);
        Assert.NotEqual(before.Name, updated.Name);
        Assert.DoesNotContain(await service.GetAppsAsync(), item => item.Id == before.Id);
    }

    [Fact]
    public async Task InventorySharesOneRegistrationReadWithItsStatusCheckAndRefreshReadsAgain()
    {
        var program = new InstalledProgram(WindowsInstalledProgramSource.Roots[2] + @"\FixtureEdit", "Fixture Edit", "remove.exe");
        _installedPrograms.SetupSequence(source => source.ReadAsync())
            .ReturnsAsync(new[] { program }).ReturnsAsync(Array.Empty<InstalledProgram>());
        var metadata = new Mock<IAppMetadataSource>();
        metadata.Setup(source => source.ReadAsync(It.IsAny<IReadOnlyList<ItemDefinition>>()))
            .ReturnsAsync(new Dictionary<string, AppMetadata>());
        var discovery = new AppStatusDiscoveryService(_logService.Object, _winGetBootstrapper.Object,
            Mock.Of<IWinGetDetectionService>(), Mock.Of<IChocolateyService>(), _installedPrograms.Object,
            Mock.Of<IAppxPackageSource>(), Mock.Of<IWindowsComponentStatusSource>(), metadata.Object, Mock.Of<IInteractiveUserService>());
        var service = CreateSut(discovery);

        var inventory = await service.ReadInventoryAsync();
        Assert.Same(program, Assert.Single(inventory.DesktopRegistrations!));
        var app = Assert.Single(inventory.Applications, item => item.RegistryUninstallKeyPath == program.KeyPath);
        Assert.True((await inventory.ReadStatusAsync([app]))[app.Id]);
        _installedPrograms.Verify(source => source.ReadAsync(), Times.Once);

        var refreshed = await service.ReadInventoryAsync();
        Assert.DoesNotContain(refreshed.Applications, item => item.Id == app.Id);
        Assert.False((await refreshed.ReadStatusAsync([app]))[app.Id]);
        _installedPrograms.Verify(source => source.ReadAsync(), Times.Exactly(2));
    }

    // --- UninstallAppAsync ---

    [Fact]
    public async Task UninstallAppAsync_Success_ReturnsSuccessAndLogsRemoval()
    {
        var sut = CreateSut();
        var item = new ItemDefinition
        {
            Id = "ext-app",
            Name = "External App",
            Description = "An external app"
        };

        _externalAppUninstallService
            .Setup(x => x.UninstallAsync(item, It.IsAny<IProgress<TaskProgressDetail>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperationResult<bool>.Succeeded(true));

        var result = await sut.UninstallAppAsync(item);

        result.Success.Should().BeTrue();
        _externalAppUninstallService.Verify(
            x => x.UninstallAsync(item, It.IsAny<IProgress<TaskProgressDetail>?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _changeHistoryService.Verify(
            x => x.LogAppChange(It.IsAny<string>(), AppChangeKind.Removed), Times.Once);
    }

    [Fact]
    public async Task UninstallAppAsync_PendingRestartDoesNotRecordCompletedRemoval()
    {
        var item = new ItemDefinition { Id = "desktop", Name = "Desktop app", Description = "" };
        var pending = OperationResult<bool>.DeferredSuccess(true, "Restart Windows.");
        _externalAppUninstallService.Setup(service => service.UninstallAsync(item, null, default)).ReturnsAsync(pending);

        var result = await CreateSut().UninstallAppAsync(item);

        Assert.Same(pending, result);
        _changeHistoryService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UninstallAppAsync_Failure_ReturnsFailure()
    {
        var sut = CreateSut();
        var item = new ItemDefinition
        {
            Id = "ext-app",
            Name = "External App",
            Description = "An external app"
        };

        _externalAppUninstallService
            .Setup(x => x.UninstallAsync(item, It.IsAny<IProgress<TaskProgressDetail>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperationResult<bool>.Failed("Uninstall failed"));

        var result = await sut.UninstallAppAsync(item);

        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task UninstallAppAsync_OperationCancelled_ReturnsCancelled()
    {
        var sut = CreateSut();
        var item = new ItemDefinition
        {
            Id = "ext-app",
            Name = "External App",
            Description = "An external app"
        };

        _externalAppUninstallService
            .Setup(x => x.UninstallAsync(item, It.IsAny<IProgress<TaskProgressDetail>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var result = await sut.UninstallAppAsync(item);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("cancelled");
    }
}
