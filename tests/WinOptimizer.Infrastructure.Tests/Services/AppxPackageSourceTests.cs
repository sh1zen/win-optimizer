using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

public class AppxPackageSourceTests
{
    [Fact]
    public async Task DeviceInventory_CancellationBeforeDiscoveryDoesNotQueryWindows()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var source = new AppxPackageSource(Mock.Of<ILogService>(), Mock.Of<IPowerShellRunner>(MockBehavior.Strict));
        var query = () => source.GetDevicePackageNamesAsync(cancellation.Token);
        await query.Should().ThrowAsync<OperationCanceledException>();
    }

    private readonly Mock<ILogService> _mockLog = new();
    private readonly Mock<IPowerShellRunner> _mockPowerShellRunner = new();
    private readonly AppxPackageSource _service;

    public AppxPackageSourceTests()
    {
        _service = new AppxPackageSource(_mockLog.Object, _mockPowerShellRunner.Object);
    }

    [Fact]
    public async Task GetInstalledPackageNamesAsync_ReturnsCaseInsensitiveSet()
    {
        var result = await _service.GetInstalledPackageNamesAsync();

        result.Should().NotBeNull();
        // Verify the comparer is case-insensitive
        result.Comparer.Should().Be(StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetInstalledPackageNamesAsync_CancellationRequested_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => _service.GetInstalledPackageNamesAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("not-a-sid")]
    [InlineData("S-1-5-21-1-2-3-1001\\Software")]
    public void ForUser_RejectsInvalidIdentitiesBeforePackageDiscovery(string sid)
    {
        Action act = () => { _ = _service.GetInstalledPackageNamesForUserAsync(sid); };
        act.Should().Throw<ArgumentException>();
        _mockPowerShellRunner.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ForUser_CancellationBeforeDiscoveryNeverFallsBackToAnotherAccount()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var act = () => _service.GetInstalledPackageNamesForUserAsync("S-1-5-21-1-2-3-1001", cancellation.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        _mockPowerShellRunner.VerifyNoOtherCalls();
    }
}
