using System.Collections.Concurrent;
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

    [Theory]
    [InlineData("40.0.0.0", "31.0.0.0", false)]
    [InlineData("20.0.0.0", "32.0.0.0", true)]
    public void UsesDriverVerFromTheInstallSectionForTheMatchedDevice(string global, string effective, bool upgrade)
    {
        var component = Inf(Subsystem, global, date: "2025-03-01") with
        {
            InstallSections =
            [
                new("Other.NTamd64", "99.0.0.0", "2026-01-01", [new(BaseId, DriverIdentifierKind.Hardware, 0)]),
                new("Device.NTamd64", effective, "2025-03-01", [new(Subsystem, DriverIdentifierKind.Hardware, 0)])
            ]
        };
        var update = Candidate("per-device", components: [component]);

        var comparisons = DriverCatalogMatching.Evaluate(update, Inventory).Comparisons;
        var matched = Assert.Single(comparisons, item => item.DeviceIdentifier.Value == Subsystem);
        Assert.Equal(effective, matched.Version);
        Assert.Equal(1, matched.InfIdentifier.Order + 1);
        var (updates, _) = DriverCatalogMatching.ClassifyCandidates([update], Inventory, new(), default);
        if (upgrade) Assert.Equal(effective, Assert.Single(updates).Update.Version);
        else Assert.Empty(updates);
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
    public void NewestVerifiedUpgradeIsSelectedPerChannelAndDeviceMatch()
    {
        var studio = Candidate("studio", components: [Inf(Subsystem, "32.1", date: "2026-03-01")]);
        var older = Candidate("studio-old", components: [Inf(Subsystem, "34.0", date: "2026-01-01")]);
        var gaming = Candidate("grd", branch: "Game Ready", components: [Inf(Subsystem, "32.0", date: "2026-02-01")]);
        var (results, excluded) = DriverCatalogMatching.ClassifyCandidates([studio, older, gaming], Inventory, new(), default);
        Assert.Equal(["studio", "grd"], results.Select(result => result.Update.Id));
        Assert.Equal(1, excluded);
        Assert.True(Assert.Single(results[0].Comparisons).NewestFound);
        Assert.True(Assert.Single(results[1].Comparisons).NewestFound);
    }

    [Fact]
    public void GenericUpgradeCannotDisplaceAnInstalledSubsystemMatch()
    {
        var inventory = Inventory with { Devices = [Device with { MatchingDeviceId = Subsystem }] };
        var generic = Candidate("generic", components: [Inf(BaseId)]);

        var (results, _) = DriverCatalogMatching.ClassifyCandidates([generic], inventory, new() { OnlyWhqlUpdates = false }, default);

        Assert.Empty(results);
    }

    [Fact]
    public void EquivalentOffersFromTheMicrosoftFeedsPreferTheNativeOffer()
    {
        var catalog = Candidate("catalog", DriverSources.MicrosoftCatalog, branch: "", components: [Inf(Subsystem)]);
        var native = Candidate("native", DriverSources.WindowsUpdate, branch: "Windows Update", components: [Inf(Subsystem)]);

        var (results, _) = DriverCatalogMatching.ClassifyCandidates([catalog, native], Inventory, new(), default);

        Assert.Equal(native.Id, Assert.Single(results).Update.Id);
    }

    [Fact]
    public async Task AutomaticVerificationPrioritizesMatchingHardwareAndRetriesTheGroupWithWhqlDisabled()
    {
        var inventory = new Mock<IDriverInventorySource>();
        inventory.Setup(source => source.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Inventory);
        var source = new Mock<IDriverUpdateSource>();
        source.SetupGet(item => item.Source).Returns(DriverSources.Nvidia);
        var payload = new DriverPayload(new("https://example.test/display.cab"), []);
        DriverUpdate Listing(string id, int day, string[] hardware)
        {
            var update = Candidate(id);
            return update with { Catalog = update.Catalog! with
            { ListedHardwareIds = hardware, ReleaseDate = new(2026, 3, day), Payloads = [payload] } };
        }
        var unrelated = Listing("unrelated", 3, [@"PCI\OTHER"]);
        var newest = Listing("newest", 2, [Subsystem]);
        var fallback = Listing("fallback", 1, [Subsystem]);
        var installer = Listing("installer", 4, [Subsystem]);
        installer = installer with { Catalog = installer.Catalog! with { Payloads = [new(new("https://example.test/display.msi"), [])] } };
        source.Setup(item => item.SearchAsync(It.IsAny<DriverDiscoveryContext>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DriverUpdateSearchResult([installer, unrelated, newest, fallback], []));
        var packages = new Mock<IDriverPackageService>(MockBehavior.Strict);
        var attempts = new ConcurrentBag<string>();
        packages.Setup(item => item.AcquireAsync(newest, Inventory, false, null, It.IsAny<CancellationToken>(), false))
            .Callback(() => attempts.Add(newest.Id)).ThrowsAsync(new DriverScanException("Unavailable payload"));
        packages.Setup(item => item.AcquireAsync(fallback, Inventory, false, null, It.IsAny<CancellationToken>(), false))
            .Callback(() => attempts.Add(fallback.Id)).ReturnsAsync(new DriverPackageResult(fallback with
            {
                Catalog = fallback.Catalog! with { Verification = DriverVerification.Verified, Components =
                    [Inf(Subsystem) with { Certification = DriverCertification.Unverified }] }
            }));

        var report = await new DriverScanService(inventory.Object, [source.Object], packages.Object)
            .ScanAsync(new() { OnlyWhqlUpdates = false, AutomaticVerificationLimit = 2 }, null, default);

        Assert.Equal(["fallback", "newest"], attempts.Order());
        Assert.Equal(fallback.Id, Assert.Single(report.Updates).Update.Id);
        Assert.Equal(DriverSourceState.VerificationFailed, Assert.Single(report.Sources).State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LargeNvidiaPackagesAreVerifiedOnlyAfterScanConsent(bool accepted)
    {
        var inventory = new Mock<IDriverInventorySource>();
        inventory.Setup(item => item.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Inventory);
        var source = new Mock<IDriverUpdateSource>();
        source.SetupGet(item => item.Source).Returns(DriverSources.Nvidia);
        var update = Candidate("large");
        var license = new Uri("https://www.nvidia.com/license");
        update = update with { SizeBytes = 1100L << 20, Catalog = update.Catalog! with
        {
            Payloads = [new(new("https://download.nvidia.com/driver.exe"), [])], LicenseRequired = true,
            ScanLicensePage = license, Verification = DriverVerification.LicenseRequired
        } };
        source.Setup(item => item.SearchAsync(It.IsAny<DriverDiscoveryContext>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DriverUpdateSearchResult([update], []));
        var calls = 0;
        var packages = new Mock<IDriverPackageService>(MockBehavior.Strict);
        packages.Setup(item => item.AcquireAsync(update, Inventory, false, null, It.IsAny<CancellationToken>(), true))
            .ReturnsAsync(() =>
            {
                Assert.Equal(1, calls);
                return new DriverPackageResult(update with { Catalog = update.Catalog! with
                { Verification = DriverVerification.Verified, Components = [Inf(Subsystem)] } });
            });

        var report = await new DriverScanService(inventory.Object, [source.Object], packages.Object)
            .ScanAsync(new(), null, default, (page, _) =>
            {
                Assert.Equal(license, page);
                calls++;
                return Task.FromResult(accepted);
            });

        Assert.Equal(1, calls);
        packages.Verify(item => item.AcquireAsync(update, Inventory, false, null, It.IsAny<CancellationToken>(), true),
            accepted ? Times.Once() : Times.Never());
        var outcome = Assert.Single(report.Sources);
        if (accepted)
        {
            Assert.Equal(update.Id, Assert.Single(report.Updates).Update.Id);
            Assert.Equal(DriverSourceState.Success, outcome.State);
        }
        else
        {
            Assert.Empty(report.Updates);
            Assert.Equal(DriverSourceState.Partial, outcome.State);
            Assert.Contains("license", Assert.Single(outcome.Notices));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerificationRunsTwoPackagesAndWaitsForCompletionOrCancellation(bool cancel)
    {
        var inventory = new Mock<IDriverInventorySource>();
        inventory.Setup(item => item.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Inventory);
        var source = new Mock<IDriverUpdateSource>();
        source.SetupGet(item => item.Source).Returns(DriverSources.Nvidia);
        var failed = Candidate("unsupported");
        failed = failed with { Catalog = failed.Catalog! with { Payloads = [new(new("https://download.nvidia.com/one.exe"), [])] } };
        var blocked = failed with { Id = "blocked", Catalog = failed.Catalog! with
        { References = [failed.Catalog.References[0] with { Id = "blocked" }] } };
        var reporter = new Mock<IProgress<DriverScanProgress>>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reporter.Setup(item => item.Report(It.Is<DriverScanProgress>(value => value.Completed == 1 && value.Total == 2)))
            .Callback(() => completed.TrySetResult());
        source.Setup(item => item.SearchAsync(It.IsAny<DriverDiscoveryContext>(), reporter.Object, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DriverUpdateSearchResult([blocked, failed], []));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var packages = new Mock<IDriverPackageService>(MockBehavior.Strict);
        packages.Setup(item => item.AcquireAsync(blocked, Inventory, false, It.IsAny<IProgress<DriverScanProgress>>(), It.IsAny<CancellationToken>(), false))
            .Returns(async (DriverUpdate _, DriverInventory _, bool _, IProgress<DriverScanProgress> progress, CancellationToken ct, bool _) =>
            {
                progress.Report(new("Drivers_Downloading") { BytesTransferred = 1024 });
                progress.Report(new("Drivers_ExtractingPackage"));
                started.SetResult();
                await release.Task.WaitAsync(ct);
                progress.Report(new("Drivers_VerifyingInfs"));
                return new DriverPackageResult(blocked with { Catalog = blocked.Catalog! with
                { Verification = DriverVerification.Verified, Components = [Inf(Subsystem)] } });
            });
        packages.Setup(item => item.AcquireAsync(failed, Inventory, false, It.IsAny<IProgress<DriverScanProgress>>(), It.IsAny<CancellationToken>(), false))
            .Returns(async (DriverUpdate _, DriverInventory _, bool _, IProgress<DriverScanProgress> progress, CancellationToken ct, bool _) =>
            {
                await started.Task.WaitAsync(ct);
                progress.Report(new("Drivers_VerifyingInfs"));
                return new DriverPackageResult(failed with { Catalog = failed.Catalog! with
                { Verification = DriverVerification.Unsupported, VerificationDetail = "Requires executing its installer." } });
            });
        var scan = new DriverScanService(inventory.Object, [source.Object], packages.Object)
            .ScanAsync(new(), reporter.Object, cancellation.Token);

        try
        {
            await Task.WhenAll(started.Task, completed.Task).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(scan.IsCompleted);
            reporter.Verify(item => item.Report(It.Is<DriverScanProgress>(value => value.BytesTransferred == 1024)), Times.Once);
            reporter.Verify(item => item.Report(It.Is<DriverScanProgress>(value =>
                value.ActivePackageStages.Contains("Drivers_ExtractingPackage") && value.ActivePackageStages.Contains("Drivers_VerifyingInfs"))), Times.Once);
            reporter.Verify(item => item.Report(It.Is<DriverScanProgress>(value => value.Completed == 1 &&
                value.ActivePackageStages.SequenceEqual(new[] { "Drivers_ExtractingPackage" }))), Times.Once);
            if (cancel)
            {
                await cancellation.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan.WaitAsync(TimeSpan.FromSeconds(10)));
                return;
            }

            release.SetResult();
            var result = await scan.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(blocked.Id, Assert.Single(result.Updates).Update.Id);
            var outcome = Assert.Single(result.Sources);
            Assert.Equal(DriverSourceState.Partial, outcome.State);
            Assert.Contains(outcome.Notices, note => note.Contains("Requires executing", StringComparison.Ordinal));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task SourcesShareSnapshotAndFailureDoesNotHideVerifiedUpgrades()
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
        Assert.Equal(1, report.ExcludedUpdates);
    }
}
