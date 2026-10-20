using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Infrastructure.Features.Utilities;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Utilities;

public sealed class ConnectionServiceTests
{
    private readonly AdapterSource _adapters = new();
    private readonly Mock<IInteractiveUserService> _user = new();
    private readonly Mock<IApplicationModeService> _mode = new();

    public ConnectionServiceTests()
    {
        _mode.Setup(mode => mode.BeginSystemOperation()).Returns(Mock.Of<IDisposable>());
    }

    private void VpnResult(int code, string output, string error = "") => _user.Setup(user => user.RunProcessAsInteractiveUserAsync(
        It.IsAny<string>(), It.IsAny<string>(), null, null, It.IsAny<CancellationToken>(), 15000, null))
        .ReturnsAsync(new InteractiveProcessResult(code, output, error));

    [Fact]
    public async Task ReadsBothProfileScopesAndSplitTunnelStatusWithoutLaunchingSettings()
    {
        VpnResult(0, """{"Profiles":[{"Name":"Work","Server":"vpn.example.test","Status":"Connected","AllUsers":true,"SplitTunneling":false},{"Name":"Personal","Server":"private.example.test","Status":"Disconnected","AllUsers":false,"SplitTunneling":true}],"Diagnostics":[]}""");
        var snapshot = await new ConnectionService(_adapters, _user.Object, _mode.Object).ReadAsync();
        Assert.Equal(2, snapshot.VpnProfiles.Count);
        Assert.True(snapshot.VpnProfiles[0].AllUsers); Assert.False(snapshot.VpnProfiles[0].SplitTunneling);
        Assert.Equal("Disconnected", snapshot.VpnProfiles[1].Status); Assert.Empty(snapshot.VpnDiagnostic);
        _mode.Verify(mode => mode.BeginSystemOperation(), Times.Never);
        _user.Verify(user => user.LaunchProcessAsInteractiveUser(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        Assert.Contains("@($false, $true)", ConnectionService.VpnScript);
        Assert.Contains("Get-VpnConnection -AllUserConnection:$all", ConnectionService.VpnScript);
    }

    [Theory]
    [InlineData(1, "", "Access denied")]
    [InlineData(0, "not-json", "")]
    [InlineData(0, "{}", "")]
    public async Task VpnFailureRetainsAdapterCountersAndIsNotReportedAsAnEmptySuccess(int code, string output, string error)
    {
        _adapters.Adapters = [new("a", "Ethernet", "", "Ethernet", "Up", ["192.0.2.1"], new(10, 20, 1, 2), "")];
        VpnResult(code, output, error);
        var snapshot = await new ConnectionService(_adapters, _user.Object, _mode.Object).ReadAsync();
        Assert.Single(snapshot.Adapters); Assert.Equal(10, snapshot.BytesSent);
        Assert.NotEmpty(snapshot.VpnDiagnostic); Assert.Empty(snapshot.VpnProfiles);
    }

    [Fact]
    public async Task PartialPhoneBookFailureKeepsProfilesAndReportsTheUnavailableScope()
    {
        VpnResult(0, """{"Profiles":[{"Name":"Work","Server":"vpn.example.test","Status":"Connected","AllUsers":false,"SplitTunneling":true}],"Diagnostics":["AllUsers=True: denied"]}""");
        var snapshot = await new ConnectionService(_adapters, _user.Object, _mode.Object).ReadAsync();
        Assert.Single(snapshot.VpnProfiles); Assert.Contains("AllUsers=True", snapshot.VpnDiagnostic);
    }

    [Fact]
    public async Task OtsWithoutATokenDoesNotReadOrOpenTheAdministratorsPhoneBook()
    {
        _user.SetupGet(user => user.IsOtsElevation).Returns(true);
        var service = new ConnectionService(_adapters, _user.Object, _mode.Object);
        Assert.Contains("interactive user token", (await service.ReadAsync()).VpnDiagnostic);
        await Assert.ThrowsAsync<WinOptimizer.Core.Features.Utilities.UtilityOperationException>(service.OpenVpnSettingsAsync);
        _user.Verify(user => user.RunProcessAsInteractiveUserAsync(It.IsAny<string>(), It.IsAny<string>(), null, null,
            It.IsAny<CancellationToken>(), It.IsAny<int>(), null), Times.Never);
        _user.Verify(user => user.LaunchProcessAsInteractiveUser(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(WinOptimizerMode.Builder)]
    [InlineData(WinOptimizerMode.ConfigReview)]
    public async Task VpnManagementIsGuardedAtTheServiceBoundary(WinOptimizerMode mode)
    {
        _mode.SetupGet(value => value.CurrentMode).Returns(mode);
        await Assert.ThrowsAsync<InvalidOperationException>(new ConnectionService(_adapters, _user.Object, _mode.Object).OpenVpnSettingsAsync);
        _user.Verify(user => user.LaunchProcessAsInteractiveUser(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ManageVpnLaunchesOnlyTheVpnSettingsRoute()
    {
        await new ConnectionService(_adapters, _user.Object, _mode.Object).OpenVpnSettingsAsync();
        _user.Verify(user => user.LaunchProcessAsInteractiveUser("explorer.exe", "ms-settings:network-vpn"), Times.Once);
    }

    [Fact]
    public async Task DiscoveryTimeoutKeepsTheSnapshotAndReportsAnActionableDiagnostic()
    {
        _user.Setup(user => user.RunProcessAsInteractiveUserAsync(It.IsAny<string>(), It.IsAny<string>(), null, null,
            It.IsAny<CancellationToken>(), 15000, null)).ThrowsAsync(new OperationCanceledException());
        var snapshot = await new ConnectionService(_adapters, _user.Object, _mode.Object).ReadAsync();
        Assert.Contains("timed out", snapshot.VpnDiagnostic);
    }

    private sealed class AdapterSource : IConnectionAdapterSource
    {
        public IReadOnlyList<WinOptimizer.Core.Features.Utilities.ConnectionAdapter> Adapters { get; set; } = [];
        public IReadOnlyList<WinOptimizer.Core.Features.Utilities.ConnectionAdapter> Read() => Adapters;
    }
}
