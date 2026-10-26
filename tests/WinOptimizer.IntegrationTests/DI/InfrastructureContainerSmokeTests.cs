using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using WinOptimizer.Core.Features.Common.Events;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Infrastructure.Extensions.DI;
using Xunit;

namespace WinOptimizer.IntegrationTests.DI;

[Trait("Category", "Integration")]
public class InfrastructureContainerSmokeTests
{
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

    [Theory]
    [InlineData(typeof(ILogService))]
    [InlineData(typeof(IWindowsRegistryService))]
    [InlineData(typeof(IFileSystemService))]
    [InlineData(typeof(IWindowsVersionService))]
    [InlineData(typeof(IEventBus))]
    [InlineData(typeof(ILocalizationService))]
    [InlineData(typeof(IInteractiveUserService))]
    [InlineData(typeof(IProcessExecutor))]
    [InlineData(typeof(WinOptimizer.Core.Features.Cleaner.Interfaces.ICleanerService))]
    [InlineData(typeof(IUserPreferencesService))]
    [InlineData(typeof(ICompatibleSettingsRegistry))]
    [InlineData(typeof(IWindowsCompatibilityFilter))]
    [InlineData(typeof(IHardwareCompatibilityFilter))]
    [InlineData(typeof(IHardwareDetectionService))]
    [InlineData(typeof(IPowerSettingsQueryService))]
    [InlineData(typeof(IPowerSettingsValidationService))]
    [InlineData(typeof(IComboBoxResolver))]
    [InlineData(typeof(IComboBoxSetupService))]
    [InlineData(typeof(ISystemSettingsDiscoveryService))]
    [InlineData(typeof(ISettingApplicationService))]
    [InlineData(typeof(IConfigImportState))]
    [InlineData(typeof(IDependencyManager))]
    [InlineData(typeof(IInitializationService))]
    [InlineData(typeof(IGlobalSettingsRegistry))]
    [InlineData(typeof(IGlobalSettingsPreloader))]
    [InlineData(typeof(IScheduledTaskService))]
    [InlineData(typeof(IVersionService))]
    [InlineData(typeof(ISponsorsService))]
    [InlineData(typeof(ITooltipDataService))]
    [InlineData(typeof(IConfigurationApplicationBridgeService))]
    [InlineData(typeof(IPolicyCleanupService))]
    [InlineData(typeof(IChangeHistoryService))]
    public void Resolve_CoreInfrastructureServices_AllNonNull(Type serviceType)
    {
        // Arrange
        using var provider = BuildProvider();

        // Act
        var service = provider.GetService(serviceType);

        // Assert
        service.Should().NotBeNull($"service {serviceType.Name} should be resolvable from the DI container");
    }

    [Fact]
    public void Resolve_TaskProgressService_SharedInstance()
    {
        // Arrange
        using var provider = BuildProvider();

        // Act
        var taskProgress = provider.GetService<ITaskProgressService>();
        var multiScript = provider.GetService<IMultiScriptProgressService>();

        // Assert
        taskProgress.Should().NotBeNull();
        multiScript.Should().NotBeNull();
        taskProgress.Should().BeSameAs(multiScript,
            "ITaskProgressService and IMultiScriptProgressService should resolve to the same TaskProgressService instance");
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

    [Fact]
    public void Resolve_AllSingletons_ReturnSameInstance()
    {
        // Arrange
        using var provider = BuildProvider();

        // Act
        var log1 = provider.GetService<ILogService>();
        var log2 = provider.GetService<ILogService>();
        var registry1 = provider.GetService<IWindowsRegistryService>();
        var registry2 = provider.GetService<IWindowsRegistryService>();

        // Assert
        log1.Should().BeSameAs(log2, "singleton ILogService should return same instance");
        registry1.Should().BeSameAs(registry2, "singleton IWindowsRegistryService should return same instance");
    }

}
