using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

public class WindowsAppsServiceTests
{
    private readonly Mock<IWinGetBootstrapper> _winGetBootstrapper = new();
    private readonly Mock<IAppStatusDiscoveryService> _appStatusDiscoveryService = new();
    private readonly Mock<IAppxPackageSource> _appxPackageSource = new();

    private WindowsAppsService CreateSut() => new(
        _winGetBootstrapper.Object, _appStatusDiscoveryService.Object, _appxPackageSource.Object);

    [Fact]
    public async Task GetDevicePackageNamesAsync_UsesDeviceInventory()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Microsoft.Hidden" };
        _appxPackageSource.Setup(s => s.GetDevicePackageNamesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(names);
        (await CreateSut().GetDevicePackageNamesAsync()).Should().BeSameAs(names);
    }
}
