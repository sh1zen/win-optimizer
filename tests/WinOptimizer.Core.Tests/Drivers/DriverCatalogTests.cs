using Moq;
using WinOptimizer.Core.Features.Drivers.Interfaces;
using WinOptimizer.Core.Features.Drivers.Models;
using WinOptimizer.Core.Features.Drivers.Services;
using Xunit;

namespace WinOptimizer.Core.Tests.Drivers;

public sealed class DriverCatalogTests
{
    private const string BaseId = "PCI\\VEN_10DE&DEV_2684";
    private const string Subsystem = BaseId + "&SUBSYS_51001462";
    private static readonly DriverDevice Device = new("device", "GPU", "Display", "gpu.inf", true, "31.0.0.0", [Subsystem, BaseId])
    {
        Date = "2025-03-01", CompatibleIds = ["PCI\\CC_0300"], MatchingDeviceId = BaseId, DriverProvider = "NVIDIA"
    };
    private static readonly DriverInventory Inventory = new([Device], [], true, [])
    {
        System = new() { Architecture = "X64", Major = 10, Build = 26100, Manufacturer = "Test OEM", Model = "Test PC" }
    };

    private static DriverComponent Inf(string id, string version = "32.0.0.0", string className = "Display", string date = "2026-01-01") =>
        new("display.inf", className, "NVIDIA", version, date, [new(id, DriverIdentifierKind.Hardware, 0)],
            ["Models.NTamd64.10.0...26100"], "inf", "cat", "Windows", true, DriverCertification.Whql);

    private static DriverUpdate Candidate(string id, DriverSources source = DriverSources.Nvidia, string branch = "Studio",
        string revision = "1", string[]? hashes = null, params DriverComponent[] components) =>
        new(id, "Official package", "NVIDIA", "Display", "", "2026-01-01", "", DriverCertification.Unverified)
        {
            Catalog = new([new(source, id, revision, DateTimeOffset.UnixEpoch, null, null, [])], [])
            {
                Branch = branch, Family = "Graphics", PackageVersion = "621.24", ListedHardwareIds = [Subsystem], Components = components,
                Verification = components.Length == 0 ? DriverVerification.Metadata : DriverVerification.Verified, ContentSha256 = hashes ?? []
            }
        };

    [Fact]
    public void SourceClaimsAndPackageVersionDoNotEstablishCompatibilityOrWhql()
    {
        var update = Candidate("listed");
        update = update with { Catalog = update.Catalog! with { ClaimsWhql = true, PayloadSignatureValid = true } };
        var match = DriverCatalogMatching.Evaluate(update, Inventory);
        Assert.Equal(DriverApplicability.Unknown, match.Applicability);
        Assert.Equal(DriverCertification.Unverified, match.Update.Certification);
        Assert.Equal("621.24", match.Update.Catalog!.PackageVersion);
        Assert.Empty(match.Update.Version);
        Assert.Same(Device, Assert.Single(match.Devices));
        Assert.Empty(match.Comparisons);
    }

    [Fact]
    public void ComponentEvidenceUsesActualInfVersionAndPreservesBundleAndIdentifierOrder()
    {
        var update = Candidate("bundle", components:
            [Inf(Subsystem), Inf("SWC\\GENERIC", "12.0", "SoftwareComponent") with { Inf = "addon.inf" }]);
        var result = DriverCatalogMatching.Evaluate(update, Inventory);
        Assert.Equal(DriverApplicability.PackageMatch, result.Applicability);
        Assert.Equal(DriverCertification.Whql, result.Update.Certification);
        Assert.Equal("32.0.0.0", result.Update.Version);
        Assert.Equal("621.24", result.Update.Catalog!.PackageVersion);
        Assert.Equal(2, result.Update.Catalog.Components.Length);
        var comparison = Assert.Single(result.Comparisons);
        Assert.Equal(DriverVersionRelation.Newer, comparison.VersionRelation);
        Assert.Equal(0, comparison.DeviceIdentifier.Order);
        Assert.True(comparison.MoreSpecificIdentifier);
    }

    [Fact]
    public void SubsystemAndOemAndBuildAreIndependentRestrictions()
    {
        var wrongSubsystem = Candidate("variant", components: [Inf(BaseId + "&SUBSYS_EEEE1462")]);
        Assert.Equal(DriverApplicability.Incompatible, DriverCatalogMatching.Evaluate(wrongSubsystem, Inventory).Applicability);
        var candidate = Candidate("oem", components: [Inf(Subsystem)]);
        var constrained = candidate with { Catalog = candidate.Catalog! with { Target = new("X64", 26200, null, "Test OEM", "Test PC") } };
        Assert.Equal(DriverApplicability.Incompatible, DriverCatalogMatching.Evaluate(constrained, Inventory).Applicability);
        constrained = constrained with { Catalog = constrained.Catalog! with { Target = new("X64", 26100, 26100, "Other OEM") } };
        Assert.Equal(DriverApplicability.Incompatible, DriverCatalogMatching.Evaluate(constrained, Inventory).Applicability);
    }

    [Theory]
    [InlineData("Extension", "32.0", DriverVersionRelation.NotComparable)]
    [InlineData("SoftwareComponent", "32.0", DriverVersionRelation.NotComparable)]
    [InlineData("Display", "Marketing Release X", DriverVersionRelation.Unknown)]
    [InlineData("Display", "31.0", DriverVersionRelation.Older)]
    public void VersionComparisonRetainsUnknownRelatedAndOlderCandidates(string className, string version, DriverVersionRelation relation)
    {
        var update = Candidate("candidate", components: [Inf(Subsystem, version, className, "2024-01-01")]);
        Assert.Equal(relation, Assert.Single(DriverCatalogMatching.Evaluate(update, Inventory).Comparisons).VersionRelation);
    }

    [Fact]
    public void ExactFilesAndSourceRevisionsAreDeduplicatedAndSharedInfBundlesAreKept()
    {
        var first = Candidate("first", hashes: ["A", "B"], components: [Inf(Subsystem)]);
        var listing = Candidate("first", hashes: ["A", "B"], components: [Inf(Subsystem)]);
        var sameFiles = Candidate("mirror", DriverSources.MicrosoftCatalog, hashes: ["A", "B"], components: [Inf(Subsystem)]);
        var sharedInf = Candidate("different", hashes: ["A", "C"], components: [Inf(Subsystem)]);
        var alternate = Candidate("channel", branch: "Game Ready", hashes: ["A", "B"], components: [Inf(Subsystem)]);
        var revised = Candidate("first", revision: "2", hashes: ["D"], components: [Inf(Subsystem)]);
        var candidates = DriverCatalogMatching.Deduplicate([first, listing, sameFiles, sharedInf, alternate, revised]);
        Assert.Equal(4, candidates.Count);
        Assert.Equal([DriverSources.Nvidia, DriverSources.MicrosoftCatalog], candidates[0].Catalog!.References.Select(reference => reference.Source));
        Assert.Contains(candidates, item => item.Id == "different");
        Assert.Contains(candidates, item => item.Id == "channel");
        Assert.Contains(candidates, item => item.Catalog!.References.Any(reference => reference.Revision == "2"));
    }

    [Fact]
    public void NewestVerifiedVersionIsCalculatedPerChannelAndDeviceMatch()
    {
        var studio = Candidate("studio", components: [Inf(Subsystem, "32.1", date: "2026-03-01")]);
        var older = Candidate("studio-old", components: [Inf(Subsystem, "34.0", date: "2026-01-01")]);
        var gaming = Candidate("grd", branch: "Game Ready", components: [Inf(Subsystem, "32.0", date: "2026-02-01")]);
        var results = DriverCatalogMatching.MarkNewest(new[] { studio, older, gaming }.Select(update => DriverCatalogMatching.Evaluate(update, Inventory)).ToArray());
        Assert.True(Assert.Single(results[0].Comparisons).NewestFound);
        Assert.False(Assert.Single(results[1].Comparisons).NewestFound);
        Assert.True(Assert.Single(results[2].Comparisons).NewestFound);
    }

    [Fact]
    public async Task SourcesShareSnapshotAndFailureDoesNotHideCandidatesOrFilteredEvidence()
    {
        var inventory = new Mock<IDriverInventorySource>();
        inventory.Setup(source => source.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Inventory);
        var offline = new Mock<IDriverUpdateSource>();
        offline.SetupGet(source => source.Source).Returns(DriverSources.Intel);
        offline.Setup(source => source.SearchAsync(It.IsAny<DriverDiscoveryContext>(), null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DriverScanException("Intel unavailable"));
        var available = new Mock<IDriverUpdateSource>();
        available.SetupGet(source => source.Source).Returns(DriverSources.Nvidia);
        var options = new DriverScanOptions { Sources = DriverSources.Intel | DriverSources.Nvidia };
        available.Setup(source => source.SearchAsync(It.Is<DriverDiscoveryContext>(context =>
                ReferenceEquals(context.Inventory, Inventory) && ReferenceEquals(context.Options, options)), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DriverUpdateSearchResult([Candidate("metadata"), Candidate("verified", components: [Inf(Subsystem)])], []));
        var report = await new DriverScanService(inventory.Object, [offline.Object, available.Object]).ScanAsync(options, null, default);
        Assert.True(report.UpdatesChecked);
        Assert.Contains(report.Sources, item => item.Source == DriverSources.Intel && item.State == DriverSourceState.Unavailable);
        Assert.Equal("verified", Assert.Single(report.Updates).Update.Id);
        Assert.Equal("metadata", Assert.Single(report.Filtered).Update.Id);
        Assert.Equal(1, report.ExcludedUpdates);
    }
}
