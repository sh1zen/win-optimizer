using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.SoftwareApps.Enums;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

public sealed class WindowsAppUninstallServiceTests
{
    private readonly Mock<IExternalAppUninstallService> _desktop = new();
    private readonly Mock<IBloatRemovalService> _packages = new();
    private readonly Mock<ITaskProgressService> _tasks = new();
    private readonly Mock<IChangeHistoryService> _history = new();
    private WindowsAppUninstallService Service => new(Mock.Of<ILogService>(), _packages.Object,
        _desktop.Object, _tasks.Object, Mock.Of<ILocalizationService>(), _history.Object);

    [Fact]
    public async Task UnavailableRegistrationDoesNotEraseCompletedRemovalsOrSkipOtherApps()
    {
        var first = new ItemDefinition { Id = "first", Name = "First desktop app", Description = "" };
        var unreadable = new ItemDefinition { Id = "unreadable", Name = "Unreadable app", Description = "",
            AppxPackageName = ["Example.Unreadable"], RegistryDisplayName = "Unreadable" };
        var packaged = new ItemDefinition { Id = "package", Name = "Packaged app", Description = "", AppxPackageName = ["Example.Package"] };
        var commands = new Mock<IUninstallCommandSource>();
        commands.Setup(source => source.GetAsync(first)).ReturnsAsync((true, @"C:\Fixture\Uninstall.exe"));
        commands.Setup(source => source.GetAsync(unreadable)).ThrowsAsync(new UnauthorizedAccessException("Registration denied."));
        var processes = new Mock<IProcessExecutor>();
        processes.Setup(executor => executor.ShellExecuteAsync(@"C:\Fixture\Uninstall.exe", "", true, default)).ReturnsAsync(0);
        var review = new Mock<IUninstallResidueReview>();
        review.Setup(source => source.SuspendForUninstallAsync(default)).ReturnsAsync(Mock.Of<IAsyncDisposable>());
        var desktop = new ExternalAppUninstallService(Mock.Of<IWinGetPackageInstaller>(), Mock.Of<IChocolateyService>(),
            Mock.Of<ILogService>(), commands.Object, _tasks.Object, processes.Object, review.Object,
            Mock.Of<IAppxDeployment>(), Mock.Of<IInteractiveUserService>());
        var service = new WindowsAppUninstallService(Mock.Of<ILogService>(), _packages.Object,
            desktop, _tasks.Object, Mock.Of<ILocalizationService>(), _history.Object);
        _packages.Setup(source => source.ExecuteBloatRemovalAsync(
                It.Is<IReadOnlyList<ItemDefinition>>(items => items.Count == 1 && items[0] == packaged), false, null, default))
            .ReturnsAsync([new(packaged.Id, true, false, null)]);

        var result = await service.UninstallAppsWithOutcomesAsync([first, unreadable, packaged], saveRemovalScripts: false);

        Assert.True(result.Success);
        Assert.Collection(result.Result!,
            outcome => { Assert.Equal(first.Id, outcome.AppId); Assert.True(outcome.Success); },
            outcome => { Assert.Equal(unreadable.Id, outcome.AppId); Assert.False(outcome.Success); Assert.Contains("Registration denied.", outcome.Message); },
            outcome => { Assert.Equal(packaged.Id, outcome.AppId); Assert.True(outcome.Success); });
        _history.Verify(history => history.LogAppChange(first.Name, AppChangeKind.Removed), Times.Once);
        _history.Verify(history => history.LogAppChange(packaged.Name, AppChangeKind.Removed), Times.Once);
        _history.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("windows-app-onedrive", "Microsoft OneDrive")]
    [InlineData("windows-app-edge", "Microsoft Edge")]
    [InlineData("windows-app-onenote", "Microsoft OneNote - {locale}")]
    public async Task DesktopCatalogueEntriesUseTheRegisteredUninstaller(string id, string displayName)
    {
        var app = WindowsAppDefinitions.GetWindowsApps().Items.Single(item => item.Id == id);
        Assert.Equal(displayName, app.RegistryDisplayName);
        _desktop.Setup(service => service.TryUninstallRegisteredAsync(app, default))
            .ReturnsAsync(OperationResult<bool>.DeferredSuccess(true, "Restart Windows."));

        var result = await Service.UninstallAppsWithOutcomesAsync([app], saveRemovalScripts: false);

        Assert.True(result.Success);
        var outcome = Assert.Single(result.Result!);
        Assert.Equal(id, outcome.AppId);
        Assert.True(outcome.Success);
        Assert.True(outcome.Deferred);
        Assert.Equal("Restart Windows.", outcome.Message);
        _packages.VerifyNoOtherCalls();
        _history.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task BatchUsesWindowsPackagesAndReportsAnIndependentNativeFailureInInputOrder()
    {
        var packaged = new ItemDefinition
        {
            Id = "store", Name = "Store app", Description = "", AppxPackageName = ["Example.Store"],
            DetectedVia = DetectionSource.AppX
        };
        var desktop = new ItemDefinition { Id = "desktop", Name = "Desktop app", Description = "", RegistryUninstallKeyPath = "test" };
        var feature = new ItemDefinition { Id = "feature", Name = "Feature", Description = "", OptionalFeatureName = "ExampleFeature" };
        _desktop.Setup(service => service.UninstallAsync(desktop, null, default))
            .ReturnsAsync(OperationResult<bool>.Failed("The native uninstaller denied removal."));
        _packages.Setup(service => service.ExecuteBloatRemovalAsync(
                It.Is<IReadOnlyList<ItemDefinition>>(apps => apps.SequenceEqual(new[] { packaged, feature })), false, null, default))
            .ReturnsAsync([new(feature.Id, false, false, "DISM refused removal."), new(packaged.Id, true, false, null)]);

        var result = await Service.UninstallAppsWithOutcomesAsync([packaged, desktop, feature], saveRemovalScripts: false);

        Assert.True(result.Success);
        Assert.Collection(result.Result!,
            item => { Assert.Equal(packaged.Id, item.AppId); Assert.True(item.Success); },
            item => { Assert.Equal(desktop.Id, item.AppId); Assert.False(item.Success); Assert.Contains("denied", item.Message); },
            item => { Assert.Equal(feature.Id, item.AppId); Assert.False(item.Success); Assert.Contains("DISM", item.Message); });
        _history.Verify(service => service.LogAppChange(packaged.Name, AppChangeKind.Removed), Times.Once);
        _history.VerifyNoOtherCalls();
        _tasks.Verify(service => service.CompleteTask(), Times.Once);
    }

    [Fact]
    public async Task OneTimeDesktopRemovalDoesNotTouchTheSharedPackagePlan()
    {
        var app = new ItemDefinition { Id = "desktop", Name = "Desktop app", Description = "", RegistryUninstallKeyPath = "test" };
        _desktop.Setup(service => service.UninstallAsync(app, null, default)).ReturnsAsync(OperationResult<bool>.Succeeded(true));

        var result = await Service.UninstallAppsWithOutcomesAsync([app], saveRemovalScripts: false);

        Assert.True(Assert.Single(result.Result!).Success);
        _packages.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DesktopOutcomeSurvivesCancellationBeforeOrDuringThePackageBatch(bool cancelDuringPackages)
    {
        using var cancellation = new CancellationTokenSource();
        _tasks.SetupGet(service => service.CurrentTaskCancellationSource).Returns(cancellation);
        var desktop = new ItemDefinition { Id = "desktop", Name = "Desktop app", Description = "" };
        var package = new ItemDefinition { Id = "package", Name = "Packaged app", Description = "", AppxPackageName = ["Example.App"] };
        _desktop.Setup(service => service.UninstallAsync(desktop, null, cancellation.Token))
            .ReturnsAsync(() =>
            {
                if (!cancelDuringPackages) cancellation.Cancel();
                return OperationResult<bool>.Succeeded(true);
            });
        if (cancelDuringPackages)
        {
            _packages.Setup(service => service.ExecuteBloatRemovalAsync(It.IsAny<IReadOnlyList<ItemDefinition>>(), false, null, cancellation.Token))
                .Returns(() =>
                {
                    cancellation.Cancel();
                    return Task.FromCanceled<IReadOnlyList<UninstallNativeOutcome>>(cancellation.Token);
                });
        }

        var result = await Service.UninstallAppsWithOutcomesAsync([desktop, package], saveRemovalScripts: false);

        Assert.True(result.Success);
        Assert.Collection(result.Result!,
            first => { Assert.Equal(desktop.Id, first.AppId); Assert.True(first.Success); Assert.False(first.Cancelled); },
            second => { Assert.Equal(package.Id, second.AppId); Assert.False(second.Success); Assert.True(second.Cancelled); });
        _history.Verify(service => service.LogAppChange(desktop.Name, AppChangeKind.Removed), Times.Once);
        _history.VerifyNoOtherCalls();
        _packages.Verify(service => service.ExecuteBloatRemovalAsync(It.IsAny<IReadOnlyList<ItemDefinition>>(), false, null, cancellation.Token),
            cancelDuringPackages ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task PackagedCatalogueEntryUsesTheSharedPackageBatchWhenNoDesktopCommandExists()
    {
        var app = new ItemDefinition
        {
            Id = "catalogue", Name = "Shared catalogue entry", Description = "", RegistryDisplayName = "Example Desktop",
            AppxPackageName = ["Example.Package"]
        };
        _desktop.Setup(service => service.TryUninstallRegisteredAsync(app, default))
            .ReturnsAsync((OperationResult<bool>?)null);
        _packages.Setup(service => service.ExecuteBloatRemovalAsync(
                It.Is<IReadOnlyList<ItemDefinition>>(apps => apps.Count == 1 && apps[0] == app), false, null, default))
            .ReturnsAsync([new(app.Id, true, false, null)]);

        var result = await Service.UninstallAppsWithOutcomesAsync([app], saveRemovalScripts: false);

        Assert.True(result.Success);
        var outcome = Assert.Single(result.Result!);
        Assert.Equal(app.Id, outcome.AppId);
        Assert.True(outcome.Success);
        _desktop.Verify(service => service.TryUninstallRegisteredAsync(app, default), Times.Once);
    }
}
