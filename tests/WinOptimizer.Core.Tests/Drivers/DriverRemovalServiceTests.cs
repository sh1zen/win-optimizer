using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Drivers.Interfaces;
using WinOptimizer.Core.Features.Drivers.Models;
using WinOptimizer.Core.Features.Drivers.Services;
using Xunit;

namespace WinOptimizer.Core.Tests.Drivers;

public sealed class DriverRemovalServiceTests
{
    private readonly Mock<IDriverInventorySource> _inventory = new();
    private readonly Mock<IDriverRemovalBackend> _backend = new(MockBehavior.Strict);
    private readonly Mock<IApplicationModeService> _mode = new();
    private readonly DriverRemovalService _service;
    private readonly DriverPackage _package = new("oem1.inf", "net.inf", "OEM", "Net", "1.0", false, false);
    private readonly DriverDevice _device = new("USB\\TEST\\1", "Device", "USB", "oem2.inf", false, "1.0", ["USB\\TEST"]);
    private DriverRemovalTarget PackageTarget => new(DriverRemovalKind.Package, _package.PublishedName);
    private DriverRemovalTarget DeviceTarget => new(DriverRemovalKind.Device, _device.InstanceId);
    private DriverScanReport Report => new(new([_device], [_package], true, []), [_package], [_device], [], true, 0, [], new());

    public DriverRemovalServiceTests()
    {
        _service = new(_inventory.Object, _backend.Object, _mode.Object);
        _inventory.Setup(source => source.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Report.Inventory);
    }

    [Fact]
    public async Task Batch_ResolvesExactScanCandidates_DeduplicatesAndRemovesDevicesFirst()
    {
        var calls = new List<DriverRemovalTarget>();
        _backend.Setup(backend => backend.RemoveAsync(It.IsAny<DriverRemovalTarget>(), It.IsAny<CancellationToken>()))
            .Callback<DriverRemovalTarget, CancellationToken>((target, _) => calls.Add(target))
            .ReturnsAsync((DriverRemovalTarget target, CancellationToken _) => new DriverRemovalOutcome(target, true, target == PackageTarget));
        var result = await _service.RemoveAsync(Report, [PackageTarget, DeviceTarget, PackageTarget], null, default);
        Assert.Equal(new[] { DeviceTarget, PackageTarget }, calls);
        Assert.Equal(2, result.Outcomes.Count);
        Assert.True(result.Outcomes[1].RestartRequired);
        _inventory.Verify(source => source.ReadAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UnknownSelection_RejectsWholeBatchBeforeReadingOrRemoving()
    {
        await Assert.ThrowsAsync<DriverScanException>(() => _service.RemoveAsync(Report,
            [PackageTarget, new(DriverRemovalKind.Package, "oem999.inf")], null, default));
        _inventory.VerifyNoOtherCalls(); _backend.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("bound")] [InlineData("incomplete")] [InlineData("protected")] [InlineData("changed")] [InlineData("missing")]
    public async Task ChangedPackageEvidence_PreventsRemoval(string change)
    {
        var current = Report.Inventory with
        {
            Devices = change == "bound" ? [_device with { InfName = _package.PublishedName }] : [_device],
            BindingsComplete = change != "incomplete",
            Packages = change switch
            {
                "protected" => [_package with { BootCritical = true }],
                "changed" => [_package with { Version = "2.0" }],
                "missing" => [],
                _ => [_package]
            }
        };
        _inventory.Setup(source => source.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(current);
        var result = await _service.RemoveAsync(Report, [PackageTarget], null, default);
        Assert.False(Assert.Single(result.Outcomes).Success);
        _backend.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReconnectedDevice_IsKeptWhileEligiblePackageIsRemoved()
    {
        _inventory.Setup(source => source.ReadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Report.Inventory with { Devices = [_device with { Present = true }] });
        _backend.Setup(backend => backend.RemoveAsync(PackageTarget, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DriverRemovalOutcome(PackageTarget, true, false));
        var result = await _service.RemoveAsync(Report, [DeviceTarget, PackageTarget], null, default);
        Assert.False(result.Outcomes[0].Success);
        Assert.True(result.Outcomes[1].Success);
    }

    [Fact]
    public async Task Cancellation_RetainsCompletedRemoval_AndStopsBeforeNextItem()
    {
        using var cancellation = new CancellationTokenSource();
        _backend.Setup(backend => backend.RemoveAsync(DeviceTarget, It.IsAny<CancellationToken>()))
            .Returns(() => { cancellation.Cancel(); return Task.FromResult(new DriverRemovalOutcome(DeviceTarget, true, false)); });
        var result = await _service.RemoveAsync(Report, [DeviceTarget, PackageTarget], null, cancellation.Token);
        Assert.True(result.Cancelled);
        Assert.True(Assert.Single(result.Outcomes).Success);
        _backend.Verify(backend => backend.RemoveAsync(PackageTarget, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TestMode_BlocksBeforeInventoryAndWindowsCalls()
    {
        _mode.Setup(mode => mode.BeginSystemOperation()).Throws(new InvalidOperationException("test mode"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.RemoveAsync(Report, [PackageTarget], null, default));
        _inventory.VerifyNoOtherCalls(); _backend.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WindowsFailure_IsRetained_AndRemainingSelectionContinues()
    {
        _backend.Setup(backend => backend.RemoveAsync(DeviceTarget, It.IsAny<CancellationToken>())).ThrowsAsync(new DriverScanException("denied"));
        _backend.Setup(backend => backend.RemoveAsync(PackageTarget, It.IsAny<CancellationToken>())).ReturnsAsync(new DriverRemovalOutcome(PackageTarget, true, false));
        var result = await _service.RemoveAsync(Report, [DeviceTarget, PackageTarget], null, default);
        Assert.Equal("denied", result.Outcomes[0].Error);
        Assert.True(result.Outcomes[1].Success);
    }
}
