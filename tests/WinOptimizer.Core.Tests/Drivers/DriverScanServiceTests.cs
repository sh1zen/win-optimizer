using Moq;
using WinOptimizer.Core.Features.Drivers.Interfaces;
using WinOptimizer.Core.Features.Drivers.Models;
using WinOptimizer.Core.Features.Drivers.Services;
using Xunit;

namespace WinOptimizer.Core.Tests.Drivers;

public sealed class DriverScanServiceTests
{
    private readonly Mock<IDriverInventorySource> _inventory = new();
    private readonly Mock<IDriverUpdateSource> _updates = new();
    private readonly DriverScanService _scanner;
    public DriverScanServiceTests()
    {
        _scanner = new(_inventory.Object, _updates.Object);
        Inventory([], []);
        Updates();
    }
    private static DriverDevice Device(string inf, bool present = true, string version = "1.0") =>
        new(inf + present, "Device", "Net", inf, present, version, ["PCI\\TEST", "pci\\test"]);
    private static DriverPackage Package(string inf, bool inbox = false, bool boot = false) => new(inf, "driver.inf", "OEM", "Net", "1.0", inbox, boot);
    private static DriverUpdate Update(string id = "offer", DriverCertification certification = DriverCertification.Whql, string version = "2.0") =>
        new(id, "Official driver", "OEM", "Net", "pci\\test", "2026-01-01", version, certification);
    private void Inventory(DriverDevice[] devices, DriverPackage[] packages, bool complete = true) =>
        _inventory.Setup(x => x.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new DriverInventory(devices, packages, complete, complete ? [] : ["binding unavailable"]));
    private void Updates(params DriverUpdate[] updates) =>
        _updates.Setup(x => x.SearchAsync(It.IsAny<IProgress<DriverScanProgress>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new DriverUpdateSearchResult(updates, []));
    private Task<DriverScanReport> Scan(DriverScanOptions? options = null, CancellationToken ct = default) => _scanner.ScanAsync(options ?? new(), null, ct);

    [Fact]
    public async Task Unreferenced_ExcludesAllDeviceBindingsIncludingDisconnected_AndProtectedPackages()
    {
        var disconnected = Device("oem2.inf", false);
        var unused = Package("oem3.inf");
        Inventory([Device("OEM1.INF"), disconnected], [Package("oem1.inf"), Package("oem2.inf"), unused, Package("inbox.inf", inbox: true), Package("boot.inf", boot: true)]);
        var result = await Scan();
        Assert.Same(unused, Assert.Single(result.UnreferencedPackages));
        Assert.Same(disconnected, Assert.Single(result.DisconnectedDevices));
    }
    [Fact]
    public async Task IncompleteBindings_SuppressUnusedClaims_AndPreserveEvidenceAndNotice()
    {
        Inventory([Device("", false)], [Package("oem1.inf")], false);
        var result = await Scan();
        Assert.Empty(result.UnreferencedPackages);
        Assert.Single(result.DisconnectedDevices);
        Assert.Contains("binding unavailable", result.Notices);
    }
    [Theory]
    [InlineData(true, 1, 1)]
    [InlineData(false, 2, 0)]
    public async Task SavedPolicy_RequiresPositiveWhqlEvidenceOnlyWhenEnabled(bool onlyWhql, int count, int excluded)
    {
        Updates(Update(), Update("unverified", DriverCertification.Unverified));
        var options = new DriverScanOptions { OnlyWhqlUpdates = onlyWhql };
        var result = await Scan(options);
        Assert.Equal(count, result.Updates.Count);
        Assert.Equal(excluded, result.ExcludedUpdates);
        Assert.Same(options, result.Options);
        Assert.True(result.UpdatesChecked);
    }
    [Fact]
    public async Task Matching_IsCaseInsensitive_DeduplicatesOffersAndHardwareIds_AndPreservesDeviceIdentity()
    {
        var device = Device("oem1.inf");
        Inventory([device, Device("absent.inf", false)], []);
        Updates(Update(), Update("OFFER"));
        var result = await Scan();
        var match = Assert.Single(result.Updates);
        Assert.Same(device, Assert.Single(match.Devices));
    }
    [Theory]
    [InlineData("0.9", 0)] [InlineData("1.0", 0)] [InlineData("2.0", 1)] [InlineData("", 1)]
    public async Task KnownEqualOrOlderVersions_AreNotReportedAsUpgrades(string version, int count)
    {
        Inventory([Device("oem1.inf")], []);
        Updates(Update(version: version));
        Assert.Equal(count, (await Scan()).Updates.Count);
    }
    [Fact]
    public async Task OfflineUpdateFailure_PreservesLocalInventory_AndDoesNotClaimSuccessfulEmptySearch()
    {
        Inventory([Device("absent.inf", false)], [Package("unused.inf")]);
        _updates.Setup(x => x.SearchAsync(It.IsAny<IProgress<DriverScanProgress>?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new DriverScanException("offline"));
        var result = await Scan();
        Assert.False(result.UpdatesChecked);
        Assert.Single(result.UnreferencedPackages);
        Assert.Single(result.DisconnectedDevices);
        Assert.Contains("offline", result.Notices);
    }
    [Fact]
    public async Task Cancellation_IsNotConvertedToAnEmptySuccessfulReport()
    {
        using var cancellation = new CancellationTokenSource();
        _updates.Setup(x => x.SearchAsync(It.IsAny<IProgress<DriverScanProgress>?>(), It.IsAny<CancellationToken>()))
            .Returns(() => { cancellation.Cancel(); return Task.FromResult(new DriverUpdateSearchResult([], [])); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Scan(ct: cancellation.Token));
    }
}
