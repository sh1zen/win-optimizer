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
        _updates.SetupGet(source => source.Source).Returns(DriverSources.WindowsUpdate);
        _scanner = new(_inventory.Object, [_updates.Object]);
        Inventory([], []);
        Updates();
    }
    private static DriverDevice Device(string inf, bool present = true, string version = "1.0") =>
        new(inf + present, "Device", "Net", inf, present, version, ["PCI\\TEST", "pci\\test"]);
    private static DriverPackage Package(string inf, bool inbox = false, bool boot = false) => new(inf, "driver.inf", "OEM", "Net", "1.0", inbox, boot);
    private static DriverUpdate Update(string id = "offer", DriverCertification certification = DriverCertification.Whql,
        string version = "2.0", string date = "2026-01-01", string className = "Net") =>
        new(id, "Official driver", "OEM", className, "pci\\test", date, version, certification)
        {
            Catalog = new([new(DriverSources.WindowsUpdate, id, "1", DateTimeOffset.UnixEpoch, null, null, [])], [])
            {
                Verification = DriverVerification.Verified,
                Components = [new("driver.inf", className, "OEM", version, date,
                    [new("PCI\\TEST", DriverIdentifierKind.Hardware, 0)], ["Models.NTamd64"], "inf", "cat", "Windows", true, certification)]
            }
        };
    private void Inventory(DriverDevice[] devices, DriverPackage[] packages, bool complete = true) =>
        _inventory.Setup(x => x.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new DriverInventory(devices, packages, complete, complete ? [] : ["binding unavailable"]));
    private void Updates(params DriverUpdate[] updates) =>
        _updates.Setup(x => x.SearchAsync(It.IsAny<DriverDiscoveryContext>(), It.IsAny<IProgress<DriverScanProgress>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new DriverUpdateSearchResult(updates, []));
    private Task<DriverScanReport> Scan(DriverScanOptions? options = null, CancellationToken ct = default) => _scanner.ScanAsync(options ?? new(), null, ct);

    [Fact]
    public async Task ScanReportsMilestonesInWorkflowOrder()
    {
        var phases = new List<DriverScanPhase>();
        var progress = new Mock<IProgress<DriverScanProgress>>();
        progress.Setup(reporter => reporter.Report(It.IsAny<DriverScanProgress>()))
            .Callback<DriverScanProgress>(value => phases.Add(value.Phase));

        await _scanner.ScanAsync(new(), progress.Object, CancellationToken.None);

        Assert.Equal([DriverScanPhase.DetectDevices, DriverScanPhase.CheckSources, DriverScanPhase.PrepareResults], phases);
    }

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
    [InlineData("0.9", DriverVersionRelation.Older)] [InlineData("1.0", DriverVersionRelation.Current)]
    [InlineData("2.0", DriverVersionRelation.Newer)] [InlineData("", DriverVersionRelation.Unknown)]
    public async Task CandidateRetainsKnownOlderOrEqualVersionsWithTheirComparison(string version, DriverVersionRelation relation)
    {
        Inventory([Device("oem1.inf")], []);
        Updates(Update(version: version));
        Assert.Equal(relation, Assert.Single(Assert.Single((await Scan()).Updates).Comparisons).VersionRelation);
    }
    [Fact]
    public async Task ExtensionOffer_IsNotComparedWithTheTargetDevicesNetworkDriverVersion()
    {
        var device = Device("oem1.inf", version: "30.0");
        Inventory([device], []);
        Updates(Update(version: "1.0", className: "Extension"));
        var match = Assert.Single((await Scan()).Updates);
        Assert.Same(device, Assert.Single(match.Devices));
        Assert.Equal(DriverVersionRelation.NotComparable, Assert.Single(match.Comparisons).VersionRelation);
    }
    [Theory]
    [InlineData("2026-01-02", "0.9", DriverVersionRelation.Newer)]
    [InlineData("2025-12-31", "2.0", DriverVersionRelation.Older)]
    [InlineData("2025-12-31", "0.9", DriverVersionRelation.Older)]
    [InlineData("2026-01-01", "1.0.0.0", DriverVersionRelation.Current)]
    [InlineData("2026-01-01", "1.0.0.1", DriverVersionRelation.Newer)]
    [InlineData("unknown", "2.0", DriverVersionRelation.Newer)]
    public async Task DriverComparison_UsesDateThenVersionAndNormalizesTrailingZeros(string date, string version, DriverVersionRelation relation)
    {
        Inventory([Device("oem1.inf") with { Date = "2026-01-01" }], []);
        Updates(Update(version: version, date: date));
        Assert.Equal(relation, Assert.Single(Assert.Single((await Scan()).Updates).Comparisons).VersionRelation);
    }
    [Fact]
    public async Task OfflineUpdateFailure_PreservesLocalInventory_AndDoesNotClaimSuccessfulEmptySearch()
    {
        Inventory([Device("absent.inf", false)], [Package("unused.inf")]);
        _updates.Setup(x => x.SearchAsync(It.IsAny<DriverDiscoveryContext>(), It.IsAny<IProgress<DriverScanProgress>?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new DriverScanException("offline"));
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
        _updates.Setup(x => x.SearchAsync(It.IsAny<DriverDiscoveryContext>(), It.IsAny<IProgress<DriverScanProgress>?>(), It.IsAny<CancellationToken>()))
            .Returns(() => { cancellation.Cancel(); return Task.FromResult(new DriverUpdateSearchResult([], [])); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Scan(ct: cancellation.Token));
    }
}
