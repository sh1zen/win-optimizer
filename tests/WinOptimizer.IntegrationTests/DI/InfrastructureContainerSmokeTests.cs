using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using WinOptimizer.Application.Extensions.DI;
using WinOptimizer.Core.Features.AdvancedTools.Experimental;
using WinOptimizer.Core.Features.Common.Events;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Disks;
using WinOptimizer.Core.Features.Optimize.Interfaces;
using WinOptimizer.Infrastructure.Extensions.DI;
using Xunit;

namespace WinOptimizer.IntegrationTests.DI;

[Trait("Category", "Integration")]
public class InfrastructureContainerSmokeTests
{
    [Fact]
    public void ExperimentalFeaturesResolveThroughApplicationAndInfrastructure()
    {
        var services = new ServiceCollection();
        services.AddInfrastructureServices().AddApplicationServices();
        services.AddSingleton(Moq.Mock.Of<IInteractiveUserService>());
        using var provider = services.BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<IExperimentalFeatures>(), provider.GetRequiredService<IExperimentalFeatures>());
        Assert.NotNull(provider.GetRequiredService<IExperimentalFeatureBackend>());
        Assert.NotNull(provider.GetRequiredService<IExperimentalRecoveryStore>());
    }

    [Fact]
    public void AppDiscoveryAndRemovalResolveThroughTheAccountBoundaries()
    {
        var services = new ServiceCollection();
        services.AddInfrastructureServices();
        services.AddSingleton(Moq.Mock.Of<IInteractiveUserService>());
        services.AddSingleton(Moq.Mock.Of<IProcessExecutor>());
        services.AddSingleton(Moq.Mock.Of<WinOptimizer.Core.Features.SoftwareApps.Interfaces.IUninstallResidueReview>());
        using var provider = services.BuildServiceProvider();

        Assert.IsType<WinOptimizer.Infrastructure.Features.SoftwareApps.Services.WindowsAppxDeployment>(
            provider.GetRequiredService<WinOptimizer.Infrastructure.Features.SoftwareApps.Services.IAppxDeployment>());
        Assert.IsType<WinOptimizer.Infrastructure.Features.SoftwareApps.Services.AppxPackageSource>(
            provider.GetRequiredService<WinOptimizer.Core.Features.SoftwareApps.Interfaces.IAppxPackageSource>());
        Assert.IsType<WinOptimizer.Infrastructure.Features.SoftwareApps.Services.ExternalAppUninstallService>(
            provider.GetRequiredService<WinOptimizer.Core.Features.SoftwareApps.Interfaces.IExternalAppUninstallService>());
        Assert.IsType<WinOptimizer.Infrastructure.Features.SoftwareApps.Services.AppStatusDiscoveryService>(
            provider.GetRequiredService<WinOptimizer.Core.Features.SoftwareApps.Interfaces.IAppStatusDiscoveryService>());
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddInfrastructureServices();
        // Keep package inventory isolated while exercising the real infrastructure composition.
        services.AddSingleton(Moq.Mock.Of<WinOptimizer.Core.Features.SoftwareApps.Interfaces.IAppxPackageSource>());
        return services.BuildServiceProvider();
    }

    [Fact]
    public void CoreInfrastructureServicesResolveFromTheSameContainer()
    {
        using var provider = BuildProvider();
        Type[] serviceTypes =
        [
            typeof(IFileSystemService),
            typeof(IWindowsVersionService),
            typeof(IEventBus),
            typeof(ILocalizationService),
            typeof(IInteractiveUserService),
            typeof(IProcessExecutor),
            typeof(IInternetSpeedTest),
            typeof(IDiskToolsService),
            typeof(WinOptimizer.Core.Features.Cleaner.Interfaces.ICleanerService),
            typeof(IUserPreferencesService),
            typeof(ICompatibleSettingsRegistry),
            typeof(IWindowsCompatibilityFilter),
            typeof(IHardwareCompatibilityFilter),
            typeof(IHardwareDetectionService),
            typeof(IPowerSettingsQueryService),
            typeof(IPowerSettingsValidationService),
            typeof(IComboBoxResolver),
            typeof(IComboBoxSetupService),
            typeof(ISystemSettingsDiscoveryService),
            typeof(ISettingApplicationService),
            typeof(IConfigImportState),
            typeof(IDependencyManager),
            typeof(IInitializationService),
            typeof(IGlobalSettingsRegistry),
            typeof(IGlobalSettingsPreloader),
            typeof(IScheduledTaskService),
            typeof(IVersionService),
            typeof(ITooltipDataService),
            typeof(IConfigurationApplicationBridgeService),
            typeof(IPolicyCleanupService),
            typeof(IChangeHistoryService),
        ];

        foreach (var serviceType in serviceTypes)
            provider.GetRequiredService(serviceType);

        provider.GetRequiredService<ILogService>().Should().BeSameAs(provider.GetRequiredService<ILogService>());
        provider.GetRequiredService<IWindowsRegistryService>().Should().BeSameAs(provider.GetRequiredService<IWindowsRegistryService>());
        provider.GetRequiredService<ITaskProgressService>().Should().BeSameAs(provider.GetRequiredService<ITaskProgressService>());
    }

    [Fact]
    public void Resolve_ApplicationModes_ShareOneDomainStateOwner()
    {
        using var provider = BuildProvider();
        var mode = provider.GetRequiredService<IApplicationModeService>();
        mode.Should().BeSameAs(provider.GetRequiredService<IConfigReviewService>());
        mode.Should().BeSameAs(provider.GetRequiredService<IConfigReviewModeService>());
        mode.Should().BeSameAs(provider.GetRequiredService<IConfigReviewDiffService>());
        mode.Should().BeSameAs(provider.GetRequiredService<IConfigReviewBadgeService>());
    }

    [Fact]
    public void Resolve_CleanerSessions_ShareTheNativeSessionOwner()
    {
        using var provider = BuildProvider();
        provider.GetRequiredService<WinOptimizer.Infrastructure.Features.Cleaner.ICleanerRegistrySessions>()
            .Should().BeSameAs(provider.GetRequiredService<WinOptimizer.Infrastructure.Features.Cleaner.WindowsCleanerRegistrySessions>());
    }

}
