using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;
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
        var source = new AppxPackageSource(Mock.Of<ILogService>(), Mock.Of<IPowerShellRunner>(MockBehavior.Strict), Mock.Of<IAppxDeployment>());
        var query = () => source.GetDevicePackageNamesAsync(cancellation.Token);
        await query.Should().ThrowAsync<OperationCanceledException>();
    }

    private readonly Mock<ILogService> _mockLog = new();
    private readonly Mock<IPowerShellRunner> _mockPowerShellRunner = new();
    private readonly Mock<IAppxDeployment> _deployment = new(MockBehavior.Strict);
    private readonly AppxPackageSource _service;

    public AppxPackageSourceTests()
    {
        _service = new AppxPackageSource(_mockLog.Object, _mockPowerShellRunner.Object, _deployment.Object);
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

    [Fact]
    public async Task AlternateAccountInventoryUsesTheInteractiveSid()
    {
        const string sid = "S-1-5-21-1-2-3-1001";
        _deployment.SetupGet(source => source.InteractiveAccountSid).Returns(sid);
        _deployment.Setup(source => source.GetPackagesAsync(sid, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AppxPackageIdentity("Interactive.App", "Interactive.App_1.0_x64__publisher")]);

        var result = await _service.GetInstalledPackageNamesAsync();

        Assert.Equal(new[] { "Interactive.App" }, result);
        result.Comparer.Should().Be(StringComparer.OrdinalIgnoreCase);
        _deployment.Verify(source => source.GetPackagesAsync("", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RegistrationSnapshotUsesOnlyTheInteractiveAccountAndDefersMetadata()
    {
        const string sid = "S-1-5-21-1-2-3-1001";
        IReadOnlyList<AppxPackageIdentity> packages = [new("Interactive.App", "Interactive.App_1.0_x64__publisher")];
        IReadOnlyList<AppxRegistration> registrations = [new("Interactive.App", () => throw new InvalidOperationException("Read later."))];
        _deployment.SetupGet(source => source.InteractiveAccountSid).Returns(sid);
        _deployment.Setup(source => source.ReadAccountInventoryAsync(sid, It.IsAny<CancellationToken>())).ReturnsAsync((packages, registrations));

        var result = await _service.ReadInstalledInventoryAsync();

        Assert.Contains("interactive.app", result.Names);
        Assert.Same(registrations, result.Registrations);
        _deployment.Verify(source => source.GetPackagesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExplicitAccountRemainsIndependentOfTheInteractiveAccount()
    {
        const string sid = "S-1-5-21-1-2-3-1002";
        _deployment.Setup(source => source.GetPackagesAsync(sid, It.IsAny<CancellationToken>())).ReturnsAsync([]);

        Assert.Empty(await _service.GetInstalledPackageNamesForUserAsync(sid));

        _deployment.VerifyGet(source => source.InteractiveAccountSid, Times.Never);
        _deployment.Verify(source => source.GetPackagesAsync("", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FailedAlternateAccountInventoryCannotFallBackToCallingAccountProviders()
    {
        const string sid = "S-1-5-21-1-2-3-1001";
        _deployment.SetupGet(source => source.InteractiveAccountSid).Returns(sid);
        _deployment.Setup(source => source.GetPackagesAsync(sid, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("User inventory denied"));

        await Assert.ThrowsAsync<IOException>(() => _service.GetInstalledPackageNamesAsync());

        _deployment.Verify(source => source.GetPackagesAsync("", It.IsAny<CancellationToken>()), Times.Never);
        _mockPowerShellRunner.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MissingInteractiveIdentityDoesNotQueryAnotherAccount()
    {
        _deployment.SetupGet(source => source.InteractiveAccountSid).Throws(new IOException("Unknown account"));

        await Assert.ThrowsAsync<IOException>(() => _service.GetInstalledPackageNamesAsync());

        _deployment.Verify(source => source.GetPackagesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
