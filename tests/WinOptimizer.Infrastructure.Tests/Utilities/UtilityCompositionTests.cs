using Microsoft.Extensions.DependencyInjection;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.Infrastructure.Extensions.DI;
using WinOptimizer.Infrastructure.Features.Utilities;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Utilities;

public sealed class UtilityCompositionTests
{
    [Fact]
    public void ProductionRegistrationsConstructAllToolsWithoutStartingOperations()
    {
        var user = new Mock<IInteractiveUserService>(MockBehavior.Strict);
        var mode = new Mock<IApplicationModeService>(MockBehavior.Strict);
        var services = new ServiceCollection();
        services.AddInfrastructureServices();
        services.AddSingleton(user.Object);
        services.AddSingleton(mode.Object);
        using var provider = services.BuildServiceProvider();
        Assert.IsType<EnvironmentEditorService>(provider.GetRequiredService<IEnvironmentEditorService>());
        Assert.IsType<WindowsEnvironmentVariableStore>(provider.GetRequiredService<IEnvironmentVariableStore>());
        Assert.IsType<HostsEditorService>(provider.GetRequiredService<IHostsEditorService>());
        Assert.IsType<BatteryChargeService>(provider.GetRequiredService<IBatteryChargeService>());
        Assert.IsType<ConnectionService>(provider.GetRequiredService<IConnectionService>());
        Assert.IsType<SavedWifiService>(provider.GetRequiredService<ISavedWifiService>());
        Assert.IsType<WindowsSavedWifi>(provider.GetRequiredService<ISavedWifiSource>());
        Assert.IsType<WindowsConnectionAdapters>(provider.GetRequiredService<IConnectionAdapterSource>());
        Assert.IsType<WindowsConnectionHistory>(provider.GetRequiredService<IConnectionHistorySource>());
        Assert.IsType<ConnectionTrafficService>(provider.GetRequiredService<IConnectionTrafficService>());
        Assert.Collection(provider.GetServices<IBatteryFirmwareProvider>(),
            item => Assert.IsType<DellBatteryFirmware>(item), item => Assert.IsType<HpBatteryFirmware>(item),
            item => Assert.IsType<AsusBatteryFirmware>(item), item => Assert.IsType<AcerBatteryFirmware>(item));
        user.VerifyNoOtherCalls(); mode.VerifyNoOtherCalls();
    }
}
