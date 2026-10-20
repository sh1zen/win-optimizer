using Microsoft.Extensions.DependencyInjection;
using Moq;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Events;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Customize.Interfaces;
using WinOptimizer.Core.Features.Optimize.Interfaces;
using WinOptimizer.Infrastructure.Extensions.DI;
using WinOptimizer.Infrastructure.Features.Customize.Services;
using WinOptimizer.Infrastructure.Features.Optimize.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

/// <summary>Checks real handler dispatch composition with isolated Windows boundaries.</summary>
public sealed class SettingServicesCompositionTests
{
    [Fact]
    public void Infrastructure_RegistersCompleteDispatchers_AndSharesPowerHandler()
    {
        var services = new ServiceCollection();
        services.AddInfrastructureServices();
        services.AddSingleton(Mock.Of<ILogService>());
        services.AddSingleton(Mock.Of<IPowerSettingsQueryService>());
        services.AddSingleton(Mock.Of<ICompatibleSettingsRegistry>());
        services.AddSingleton(Mock.Of<IEventBus>());
        services.AddSingleton(Mock.Of<IPowerPlanComboBoxService>());
        services.AddSingleton(Mock.Of<IProcessExecutor>());
        services.AddSingleton(Mock.Of<IFileSystemService>());
        services.AddSingleton(Mock.Of<IPowerSchemeOperations>());
        services.AddSingleton(Mock.Of<IConfigImportState>());
        services.AddSingleton(Mock.Of<IWindowsRegistryService>());
        services.AddSingleton(Mock.Of<IPowerShellRunner>());
        services.AddSingleton(Mock.Of<IWallpaperService>());
        using var provider = services.BuildServiceProvider();

        var handlers = provider.GetRequiredService<ISpecialSettingHandlerRegistry>();
        var power = provider.GetRequiredService<PowerService>();
        Assert.Same(power, handlers.TryGet(SettingIds.PowerPlanSelection));
        Assert.Same(power, provider.GetRequiredService<IPowerService>());
        Assert.Same(provider.GetRequiredService<UpdateService>(), handlers.TryGet(SettingIds.UpdatesPolicyMode));
        Assert.Same(provider.GetRequiredService<ThemeWallpaperApplier>(), handlers.TryGet(SettingIds.ThemeModeWindows));
        Assert.Equal(new ISpecialSettingHandler[] { power, provider.GetRequiredService<UpdateService>() },
            provider.GetRequiredService<ISpecialDiscoveryRegistry>().All);
    }
}
