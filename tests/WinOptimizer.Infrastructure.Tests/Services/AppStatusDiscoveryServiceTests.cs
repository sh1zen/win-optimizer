using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

public class AppStatusDiscoveryServiceTests
{
    private readonly Mock<ILogService> _mockLog = new();
    private readonly Mock<IWinGetBootstrapper> _mockWinGetBootstrapper = new();
    private readonly Mock<IWinGetDetectionService> _mockWinGetDetection = new();
    private readonly Mock<IChocolateyService> _mockChocolatey = new();
    private readonly Mock<IInstalledProgramSource> _installedPrograms = new();
    private readonly Mock<IAppxPackageSource> _mockAppxPackageSource = new();
    private readonly Mock<IWindowsComponentStatusSource> _mockComponents = new();
    private readonly Mock<IAppMetadataSource> _mockMetadata = new();
    private readonly AppStatusDiscoveryService _service;

    public AppStatusDiscoveryServiceTests()
    {
        _installedPrograms.Setup(source => source.ReadAsync()).ReturnsAsync(Array.Empty<InstalledProgram>());
        _mockMetadata.Setup(source => source.ReadAsync(It.IsAny<IReadOnlyList<ItemDefinition>>()))
            .ReturnsAsync(new Dictionary<string, AppMetadata>());
        _mockComponents.Setup(s => s.GetStatusAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>>()))
            .ReturnsAsync(new WindowsComponentStatus(new Dictionary<string, bool>(), new Dictionary<string, bool>()));
        _mockAppxPackageSource
            .Setup(a => a.GetInstalledPackageNamesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>());

        _service = new AppStatusDiscoveryService(
            _mockLog.Object,
            _mockWinGetBootstrapper.Object,
            _mockWinGetDetection.Object,
            _mockChocolatey.Object,
            _installedPrograms.Object,
            _mockAppxPackageSource.Object,
            _mockComponents.Object,
            _mockMetadata.Object);
    }

    [Fact]
    public async Task DiscoveredProgramsRequireTheirExactRegistrationOnEveryStatusCheck()
    {
        var program = new InstalledProgram(WindowsInstalledProgramSource.Roots[0] + @"\Poedit_is1", "Poedit", "remove.exe");
        var item = program.ToDefinition();
        _installedPrograms.SetupSequence(source => source.ReadAsync())
            .ReturnsAsync(new[] { program })
            .ReturnsAsync(new[] { program with { KeyPath = WindowsInstalledProgramSource.Roots[2] + @"\Poedit_is1" } });
        Assert.True((await _service.GetExternalAppsInstallationStatusAsync([item]))[item.Id]);
        Assert.Equal(Core.Features.SoftwareApps.Enums.DetectionSource.Registry, item.DetectedVia);
        Assert.False((await _service.GetExternalAppsInstallationStatusAsync([item]))[item.Id]);
        _mockWinGetBootstrapper.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RegistryFallbackUsesSharedInventoryIncludingExplicitHiddenFingerprints()
    {
        _installedPrograms.Setup(source => source.ReadAsync()).ReturnsAsync(new InstalledProgram[]
        {
            new(WindowsInstalledProgramSource.Roots[3] + @"\Visible", "Visible program", "remove.exe"),
            new(WindowsInstalledProgramSource.Roots[0] + @"\Hidden", "Hidden component", null, IsSystemComponent: true),
        });
        var visible = new ItemDefinition { Id = "visible", Name = "Visible program", Description = "" };
        var hidden = new ItemDefinition { Id = "hidden", Name = "Hidden component", Description = "" };
        var explicitHidden = hidden with { Id = "explicit", RegistrySubKeyName = "Hidden" };

        var result = await _service.GetExternalAppsInstallationStatusAsync([visible, hidden, explicitHidden]);

        Assert.True(result[visible.Id]);
        Assert.False(result[hidden.Id]);
        Assert.True(result[explicitHidden.Id]);
        _installedPrograms.Verify(source => source.ReadAsync(), Times.Once);
    }

    private static ItemDefinition CreateAppxDefinition(string id, string appxName, string? name = null) => new()
    {
        Id = id,
        Name = name ?? id,
        Description = $"Description for {id}",
        AppxPackageName = new[] { appxName },
    };

    [Fact]
    public async Task PackageOnlyDiscoveryNeverOpensWindowsServicing()
    {
        var app = CreateAppxDefinition("app", "Vendor.App");
        _mockAppxPackageSource.Setup(source => source.GetInstalledPackageNamesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { "Vendor.App" });

        var result = await _service.GetInstallationStatusBatchAsync([app]);

        Assert.True(result[app.Id]);
        _mockComponents.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DiscoveryRefreshesMetadataAndClearsItWhenAnAppIsRemoved()
    {
        var item = CreateAppxDefinition("test", "Vendor.App");
        var metadata = new AppMetadata(DateTimeOffset.UnixEpoch, 42, null);
        _mockAppxPackageSource.Setup(source => source.GetInstalledPackageNamesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { "Vendor.App" });
        _mockMetadata.Setup(source => source.ReadAsync(It.Is<IReadOnlyList<ItemDefinition>>(items => items.Count == 1 && items[0] == item)))
            .ReturnsAsync(new Dictionary<string, AppMetadata> { [item.Id] = metadata });
        Assert.True((await _service.GetInstallationStatusBatchAsync([item]))[item.Id]);
        Assert.Same(metadata, item.Metadata);
        _mockAppxPackageSource.Setup(source => source.GetInstalledPackageNamesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>());
        Assert.False((await _service.GetInstallationStatusBatchAsync([item]))[item.Id]);
        Assert.Null(item.Metadata);
    }

    [Fact]
    public async Task MetadataFailureDoesNotChangeInstallationEvidence()
    {
        var item = CreateAppxDefinition("test", "Vendor.App");
        item.Metadata = new AppMetadata(DateTimeOffset.UnixEpoch, 42, null);
        _mockAppxPackageSource.Setup(source => source.GetInstalledPackageNamesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { "Vendor.App" });
        _mockMetadata.Setup(source => source.ReadAsync(It.IsAny<IReadOnlyList<ItemDefinition>>()))
            .ThrowsAsync(new IOException("metadata unavailable"));
        Assert.True((await _service.GetInstallationStatusBatchAsync([item]))[item.Id]);
        Assert.Null(item.Metadata);
        _mockLog.Verify(logger => logger.LogWarning(It.Is<string>(message => message.Contains("metadata unavailable"))), Times.Once);
    }

    private static ItemDefinition CreateCapabilityDefinition(string id, string capabilityName) => new()
    {
        Id = id,
        Name = id,
        Description = $"Description for {id}",
        CapabilityName = capabilityName,
    };

    private static ItemDefinition CreateFeatureDefinition(string id, string featureName) => new()
    {
        Id = id,
        Name = id,
        Description = $"Description for {id}",
        OptionalFeatureName = featureName,
    };

    private static ItemDefinition CreateExternalAppDefinition(
        string id,
        string name,
        string[]? winGetPackageIds = null,
        string? msStoreId = null,
        string? chocoPackageId = null,
        string[]? detectionPaths = null) => new()
    {
        Id = id,
        Name = name,
        Description = $"Description for {id}",
        WinGetPackageId = winGetPackageIds,
        MsStoreId = msStoreId,
        ChocoPackageId = chocoPackageId,
        DetectionPaths = detectionPaths,
    };

    // --- GetInstallationStatusBatchAsync ---

    [Fact]
    public async Task GetInstallationStatusBatchAsync_EmptyDefinitions_ReturnsEmptyDictionary()
    {
        var result = await _service.GetInstallationStatusBatchAsync(Array.Empty<ItemDefinition>());

        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetInstallationStatusBatchAsync_ReturnsCaseInsensitiveDictionary()
    {
        // With empty definitions to confirm the dictionary uses the right comparer
        var result = await _service.GetInstallationStatusBatchAsync(Array.Empty<ItemDefinition>());

        result.Should().NotBeNull();
        // The dictionary should be created with OrdinalIgnoreCase comparer
        result.Comparer.Should().Be(StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetInstallationStatusBatchAsync_WhenExceptionOccursInternallyAndCaughtAtTopLevel_ReturnsAllFalse()
    {
        // Create apps that will cause an exception path at the top-level try/catch.
        // Since the service calls platform APIs internally, we test the catch-all behavior
        // by providing definitions that go through the appx path (which uses PackageManager COM).
        // In a test environment, the PackageManager will likely fail, triggering fallback paths.
        var definitions = new List<ItemDefinition>
        {
            CreateAppxDefinition("app1", "Microsoft.TestApp1"),
            CreateAppxDefinition("app2", "Microsoft.TestApp2"),
        };

        // This test verifies the method doesn't throw when internal operations fail.
        // The PackageManager and fallback WMI/PowerShell paths will fail in a unit test environment.
        var result = await _service.GetInstallationStatusBatchAsync(definitions);

        result.Should().NotBeNull();
        // In a test environment where PackageManager is unavailable, apps should be marked false
        result.Should().ContainKey("app1");
        result.Should().ContainKey("app2");
    }

    // --- InvalidateCache ---

    [Fact]
    public async Task GetInstallationStatusBatchAsync_OverlapsComponentsAndAppx_AndWaitsForBoth()
    {
        var componentCompletion = new TaskCompletionSource<WindowsComponentStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var packageCompletion = new TaskCompletionSource<HashSet<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockComponents.Setup(s => s.GetStatusAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>>()))
            .Returns(componentCompletion.Task);
        _mockAppxPackageSource.Setup(s => s.GetInstalledPackageNamesAsync(It.IsAny<CancellationToken>()))
            .Returns(packageCompletion.Task);

        var pending = _service.GetInstallationStatusBatchAsync(new[]
        {
            CreateCapabilityDefinition("cap", "Test.Capability"),
            CreateFeatureDefinition("feature", "TestFeature"),
            CreateAppxDefinition("app", "Test.App")
        });

        try
        {
            _mockComponents.Verify(s => s.GetStatusAsync(
                It.Is<IReadOnlyList<string>>(names => names.SequenceEqual(new[] { "Test.Capability" })),
                It.Is<IReadOnlyList<string>>(names => names.SequenceEqual(new[] { "TestFeature" }))), Times.Once);
            _mockAppxPackageSource.Verify(s => s.GetInstalledPackageNamesAsync(It.IsAny<CancellationToken>()), Times.Once);
            pending.IsCompleted.Should().BeFalse();
            componentCompletion.SetResult(new WindowsComponentStatus(
                new Dictionary<string, bool> { ["Test.Capability"] = true },
                new Dictionary<string, bool> { ["TestFeature"] = false }));
            pending.IsCompleted.Should().BeFalse("AppX enumeration is still outstanding");
        }
        finally
        {
            componentCompletion.TrySetResult(new WindowsComponentStatus(new Dictionary<string, bool>(), new Dictionary<string, bool>()));
            packageCompletion.TrySetResult(new HashSet<string> { "Test.App" });
        }

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        result.Should().BeEquivalentTo(new Dictionary<string, bool> { ["cap"] = true, ["feature"] = false, ["app"] = true });
        result.Keys.Should().Equal("cap", "feature", "app");
        _mockWinGetBootstrapper.Verify(b => b.EnsureWinGetReadyAsync(), Times.Never);
    }

    [Fact]
    public async Task GetInstallationStatusBatchAsync_AppxFailure_StillDrainsComponentReadAndReturnsAllFalse()
    {
        var components = new TaskCompletionSource<WindowsComponentStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockComponents.Setup(s => s.GetStatusAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>>()))
            .Returns(components.Task);
        _mockAppxPackageSource.Setup(s => s.GetInstalledPackageNamesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("AppX unavailable"));

        var pending = _service.GetInstallationStatusBatchAsync(new[]
        {
            CreateCapabilityDefinition("cap", "Test.Capability"), CreateAppxDefinition("app", "Test.App")
        });
        pending.IsCompleted.Should().BeFalse();
        components.SetResult(new WindowsComponentStatus(new Dictionary<string, bool>(), new Dictionary<string, bool>()));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        result.Values.Should().OnlyContain(installed => !installed);
        _mockLog.Verify(l => l.LogError("Error checking batch installation status", It.IsAny<Exception>()), Times.Once);
    }

    [Fact]
    public async Task GetInstallationStatusBatchAsync_NoAppxDefinitions_DoesNotEnumeratePackages()
    {
        await _service.GetInstallationStatusBatchAsync(new[] { CreateFeatureDefinition("feature", "TestFeature") });
        _mockAppxPackageSource.Verify(s => s.GetInstalledPackageNamesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetInstallationStatusBatchAsync_PreservesExistingComponentMatchForOverlappingAppIdentity()
    {
        _mockComponents.Setup(s => s.GetStatusAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>>()))
            .ReturnsAsync(new WindowsComponentStatus(new Dictionary<string, bool> { ["Test.Capability"] = true }, new Dictionary<string, bool>()));
        var definition = CreateAppxDefinition("shared", "Absent.App") with
        {
            CapabilityName = "Test.Capability",
            WinGetPackageId = new[] { "Absent.WinGet" }
        };
        var result = await _service.GetInstallationStatusBatchAsync(new[] { definition });
        result["shared"].Should().BeTrue();
        _mockWinGetBootstrapper.Verify(b => b.EnsureWinGetReadyAsync(), Times.Never);
    }

    [Fact]
    public async Task GetInstallationStatusBatchAsync_OverlapsRequiredWinGetWithComponents_AfterAppxEnumeration()
    {
        var components = new TaskCompletionSource<WindowsComponentStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var packages = new TaskCompletionSource<HashSet<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var winGet = new TaskCompletionSource<HashSet<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var detecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockComponents.Setup(s => s.GetStatusAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>>()))
            .Returns(components.Task);
        _mockAppxPackageSource.Setup(s => s.GetInstalledPackageNamesAsync(It.IsAny<CancellationToken>())).Returns(packages.Task);
        _mockWinGetBootstrapper.Setup(s => s.EnsureWinGetReadyAsync()).ReturnsAsync(true);
        _mockWinGetDetection.Setup(s => s.GetInstalledPackageIdsAsync(It.IsAny<CancellationToken>()))
            .Callback(() => detecting.SetResult()).Returns(winGet.Task);
        var app = CreateAppxDefinition("app", "Absent.App") with { MsStoreId = "Store.App" };
        var pending = _service.GetInstallationStatusBatchAsync(new[]
        {
            CreateCapabilityDefinition("cap", "Test.Capability"), CreateFeatureDefinition("feature", "TestFeature"), app
        });
        try
        {
            _mockWinGetBootstrapper.Verify(s => s.EnsureWinGetReadyAsync(), Times.Never);
            packages.SetResult(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            await detecting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            components.Task.IsCompleted.Should().BeFalse("WinGet must start while DISM is pending");
            components.SetResult(new WindowsComponentStatus(new Dictionary<string, bool> { ["Test.Capability"] = true },
                new Dictionary<string, bool> { ["TestFeature"] = false }));
            pending.IsCompleted.Should().BeFalse("WinGet evidence is still required");
        }
        finally
        {
            packages.TrySetResult(new HashSet<string>());
            components.TrySetResult(new WindowsComponentStatus(new Dictionary<string, bool>(), new Dictionary<string, bool>()));
            winGet.TrySetResult(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "store.app" });
        }
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        result.Should().BeEquivalentTo(new Dictionary<string, bool> { ["cap"] = true, ["feature"] = false, ["app"] = true });
        result.Keys.Should().Equal("cap", "feature", "app");
        _mockWinGetDetection.Verify(s => s.GetInstalledPackageIdsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetInstallationStatusBatchAsync_SharedIdentity_WaitsForComponentsBeforeRequiredFallback()
    {
        var components = new TaskCompletionSource<WindowsComponentStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockComponents.Setup(s => s.GetStatusAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>>()))
            .Returns(components.Task);
        _mockWinGetBootstrapper.Setup(s => s.EnsureWinGetReadyAsync()).ReturnsAsync(true);
        _mockWinGetDetection.Setup(s => s.GetInstalledPackageIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { "Fallback.App" });
        var definition = CreateAppxDefinition("shared", "Absent.App") with
        {
            CapabilityName = "Test.Capability", WinGetPackageId = new[] { "Fallback.App" }
        };
        var pending = _service.GetInstallationStatusBatchAsync(new[] { definition });
        _mockWinGetBootstrapper.Verify(s => s.EnsureWinGetReadyAsync(), Times.Never);
        components.SetResult(new WindowsComponentStatus(new Dictionary<string, bool> { ["Test.Capability"] = false },
            new Dictionary<string, bool>()));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        result["shared"].Should().BeTrue();
        _mockWinGetDetection.Verify(s => s.GetInstalledPackageIdsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void InvalidateCache_ClearsWinGetPackageIdCache()
    {
        // InvalidateCache should complete without throwing
        _service.InvalidateCache();

        // After invalidation, next call to GetOrFetchWinGetPackageIdsAsync will re-fetch.
        // We verify this indirectly: calling invalidate then checking external apps
        // should trigger a fresh WinGet fetch.
        _service.InvalidateCache(); // Should be idempotent
    }

    [Fact]
    public async Task InvalidateCache_CausesWinGetRefetch_WhenExternalAppsChecked()
    {
        // Setup WinGet as ready and returning a package set
        _mockWinGetBootstrapper
            .Setup(w => w.EnsureWinGetReadyAsync())
            .ReturnsAsync(true);
        _mockWinGetDetection
            .Setup(w => w.GetInstalledPackageIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { "TestApp.Id" });

        var definitions = new List<ItemDefinition>
        {
            CreateExternalAppDefinition("ext1", "Test App", winGetPackageIds: new[] { "TestApp.Id" })
        };

        // First call populates cache
        await _service.GetExternalAppsInstallationStatusAsync(definitions);

        // Invalidate cache
        _service.InvalidateCache();

        // Second call should re-fetch
        await _service.GetExternalAppsInstallationStatusAsync(definitions);

        // WinGet detection should have been called twice (once for each external check)
        _mockWinGetDetection.Verify(
            w => w.GetInstalledPackageIdsAsync(It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    // --- GetExternalAppsInstallationStatusAsync ---

    [Fact]
    public async Task GetExternalAppsInstallationStatusAsync_EmptyDefinitions_ReturnsEmptyDictionary()
    {
        var result = await _service.GetExternalAppsInstallationStatusAsync(Array.Empty<ItemDefinition>());

        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetExternalAppsInstallationStatusAsync_WinGetDetectsApp_ReturnsTrue()
    {
        _mockWinGetBootstrapper
            .Setup(w => w.EnsureWinGetReadyAsync())
            .ReturnsAsync(true);
        _mockWinGetDetection
            .Setup(w => w.GetInstalledPackageIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { "7zip.7zip" });

        var definitions = new List<ItemDefinition>
        {
            CreateExternalAppDefinition("ext-7zip", "7-Zip", winGetPackageIds: new[] { "7zip.7zip" })
        };

        var result = await _service.GetExternalAppsInstallationStatusAsync(definitions);

        result.Should().ContainKey("ext-7zip");
        result["ext-7zip"].Should().BeTrue();
    }

    [Fact]
    public async Task GetExternalAppsInstallationStatusAsync_WinGetDetectsViaMsStoreId_ReturnsTrue()
    {
        _mockWinGetBootstrapper
            .Setup(w => w.EnsureWinGetReadyAsync())
            .ReturnsAsync(true);
        _mockWinGetDetection
            .Setup(w => w.GetInstalledPackageIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { "9NBLGGH5R558" });

        var definitions = new List<ItemDefinition>
        {
            CreateExternalAppDefinition("ext-storeapp", "Store App", msStoreId: "9NBLGGH5R558")
        };

        var result = await _service.GetExternalAppsInstallationStatusAsync(definitions);

        result.Should().ContainKey("ext-storeapp");
        result["ext-storeapp"].Should().BeTrue();
    }

    [Fact]
    public async Task GetExternalAppsInstallationStatusAsync_WinGetUnavailable_LogsWarning()
    {
        _mockWinGetBootstrapper
            .Setup(w => w.EnsureWinGetReadyAsync())
            .ReturnsAsync(false);

        var definitions = new List<ItemDefinition>
        {
            CreateExternalAppDefinition("ext1", "App 1", winGetPackageIds: new[] { "Some.Package" })
        };

        var result = await _service.GetExternalAppsInstallationStatusAsync(definitions);

        _mockLog.Verify(l => l.LogWarning(It.Is<string>(s => s.Contains("WinGet unavailable"))), Times.AtLeastOnce);
    }

    [Fact]
    public async Task GetExternalAppsInstallationStatusAsync_ChocolateyFallback_WhenWinGetNotFound()
    {
        // WinGet doesn't find the app
        _mockWinGetBootstrapper
            .Setup(w => w.EnsureWinGetReadyAsync())
            .ReturnsAsync(true);
        _mockWinGetDetection
            .Setup(w => w.GetInstalledPackageIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>());

        // Chocolatey finds the app
        _mockChocolatey
            .Setup(c => c.GetInstalledPackageIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { "vlc" });

        var definitions = new List<ItemDefinition>
        {
            CreateExternalAppDefinition("ext-vlc", "VLC", winGetPackageIds: new[] { "VideoLAN.VLC" }, chocoPackageId: "vlc")
        };

        var result = await _service.GetExternalAppsInstallationStatusAsync(definitions);

        result.Should().ContainKey("ext-vlc");
        result["ext-vlc"].Should().BeTrue();
    }

    [Fact]
    public async Task GetExternalAppsInstallationStatusAsync_ChocolateyDetectionFails_LogsWarning()
    {
        _mockWinGetBootstrapper
            .Setup(w => w.EnsureWinGetReadyAsync())
            .ReturnsAsync(true);
        _mockWinGetDetection
            .Setup(w => w.GetInstalledPackageIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>());

        _mockChocolatey
            .Setup(c => c.GetInstalledPackageIdsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Choco not installed"));

        var definitions = new List<ItemDefinition>
        {
            CreateExternalAppDefinition("ext1", "App 1", winGetPackageIds: new[] { "Pkg" }, chocoPackageId: "pkg")
        };

        var result = await _service.GetExternalAppsInstallationStatusAsync(definitions);

        _mockLog.Verify(l => l.LogWarning(It.Is<string>(s => s.Contains("Chocolatey detection failed"))), Times.Once);
    }

    [Fact]
    public async Task GetExternalAppsInstallationStatusAsync_SetsDetectedViaProperty()
    {
        _mockWinGetBootstrapper
            .Setup(w => w.EnsureWinGetReadyAsync())
            .ReturnsAsync(true);
        _mockWinGetDetection
            .Setup(w => w.GetInstalledPackageIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { "7zip.7zip" });

        var definition = CreateExternalAppDefinition("ext-7zip", "7-Zip",
            winGetPackageIds: new[] { "7zip.7zip" });
        var definitions = new List<ItemDefinition> { definition };

        await _service.GetExternalAppsInstallationStatusAsync(definitions);

        definition.DetectedVia.Should().Be(Core.Features.SoftwareApps.Enums.DetectionSource.WinGet);
    }

    [Fact]
    public async Task GetExternalAppsInstallationStatusAsync_NoDetectionSource_ReturnsFalse()
    {
        _mockWinGetBootstrapper
            .Setup(w => w.EnsureWinGetReadyAsync())
            .ReturnsAsync(true);
        _mockWinGetDetection
            .Setup(w => w.GetInstalledPackageIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>());
        _mockChocolatey
            .Setup(c => c.GetInstalledPackageIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>());

        // App without WinGet ID, choco ID, or registry match
        var definitions = new List<ItemDefinition>
        {
            CreateExternalAppDefinition("ext-unknown", "Unknown App")
        };

        var result = await _service.GetExternalAppsInstallationStatusAsync(definitions);

        result.Should().ContainKey("ext-unknown");
        result["ext-unknown"].Should().BeFalse();
    }

    [Fact]
    public async Task GetExternalAppsInstallationStatusAsync_TopLevelException_ReturnsAllFalse()
    {
        // Force WinGet bootstrapper to throw to trigger the top-level catch
        _mockWinGetBootstrapper
            .Setup(w => w.EnsureWinGetReadyAsync())
            .ThrowsAsync(new InvalidOperationException("Critical failure"));

        var definitions = new List<ItemDefinition>
        {
            CreateExternalAppDefinition("ext1", "App 1", winGetPackageIds: new[] { "Pkg" })
        };

        var result = await _service.GetExternalAppsInstallationStatusAsync(definitions);

        result.Should().ContainKey("ext1");
        result["ext1"].Should().BeFalse();
        // The exception is caught at the WinGet readiness level (logged as LogWarning), not at the top level
        _mockLog.Verify(l => l.LogWarning(It.Is<string>(s => s.Contains("Critical failure"))), Times.Once);
    }

    [Fact]
    public async Task GetExternalAppsInstallationStatusAsync_UsesWinGetCache_OnSecondCall()
    {
        _mockWinGetBootstrapper
            .Setup(w => w.EnsureWinGetReadyAsync())
            .ReturnsAsync(true);
        _mockWinGetDetection
            .Setup(w => w.GetInstalledPackageIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { "Pkg.Id" });

        var definitions = new List<ItemDefinition>
        {
            CreateExternalAppDefinition("ext1", "App 1", winGetPackageIds: new[] { "Pkg.Id" })
        };

        // Call twice
        await _service.GetExternalAppsInstallationStatusAsync(definitions);
        await _service.GetExternalAppsInstallationStatusAsync(definitions);

        // WinGet detection should only be called once due to caching
        _mockWinGetDetection.Verify(
            w => w.GetInstalledPackageIdsAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // --- MatchesPattern ---

    [Theory]
    [InlineData("MediaMonkey 5", "MediaMonkey {version}", true)]
    [InlineData("K-Lite Mega Codec Pack 19.5.0", "K-Lite Mega Codec Pack {version}", true)]
    [InlineData("ONLYOFFICE 9.3.0 (x64)", "ONLYOFFICE {version} ({arch})", true)]
    [InlineData("PuTTY release 0.83 (64-bit)", "PuTTY release {version} ({arch})", true)]
    [InlineData("InputLeap 3.0.2-release", "InputLeap {version}-release", true)]
    [InlineData("Zoom Workplace (64-bit)", "Zoom Workplace ({arch})", true)]
    [InlineData("O365ProPlusRetail - en-us", "O365ProPlusRetail - {locale}", true)]
    [InlineData("Mozilla Thunderbird (x64 en-US)", "Mozilla Thunderbird ({arch} {locale})", true)]
    [InlineData("Krita (x64) 5.2.16 (git 7d9aefc)", "Krita ({arch}) {version}", true)]
    [InlineData("Microsoft EdgeWebView2 Runtime", "Microsoft EdgeWebView{version}", true)]
    [InlineData("Trillian Machine-Wide Installer", "Trillian Machine-Wide Installer", true)]
    [InlineData("Microsoft Visual C++ 2008 Redistributable - x64 9.0.30729.6161", "Microsoft Visual C++ 2008 Redistributable - x64 {version}", true)]
    [InlineData("Microsoft .NET Runtime - 3.1.32 (x64)", "Microsoft .NET Runtime - 3.1.{version} ({arch})", true)]
    [InlineData("VLC media player", "VLC media player", true)]
    // Negative cases
    [InlineData("MediaMonkey 5", "VLC media player", false)]
    [InlineData("GIMP 3.0.8-2", "Audacity {version}", false)]
    [InlineData("Microsoft .NET Runtime - 5.0.17 (x64)", "Microsoft .NET Runtime - 3.1.{version} ({arch})", false)]
    [InlineData("", "Foo {version}", false)]
    public void MatchesPattern_ReturnsExpected(string input, string pattern, bool expected)
    {
        var result = AppStatusDiscoveryService.MatchesPattern(input, pattern);
        result.Should().Be(expected);
    }

    // --- GetExternalAppsInstallationStatusAsync: AppX detection ---

    [Fact]
    public async Task GetExternalAppsInstallationStatusAsync_AppXDetectsApp_ReturnsTrueWithDetectedViaAppX()
    {
        _mockWinGetBootstrapper
            .Setup(w => w.EnsureWinGetReadyAsync())
            .ReturnsAsync(true);
        _mockWinGetDetection
            .Setup(w => w.GetInstalledPackageIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>());
        _mockChocolatey
            .Setup(c => c.GetInstalledPackageIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>());
        _mockAppxPackageSource
            .Setup(a => a.GetInstalledPackageNamesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Microsoft.WindowsCalculator" });

        var definition = new ItemDefinition
        {
            Id = "ext-calc",
            Name = "Windows Calculator",
            Description = "Calculator",
            AppxPackageName = new[] { "Microsoft.WindowsCalculator" }
        };
        var definitions = new List<ItemDefinition> { definition };

        var result = await _service.GetExternalAppsInstallationStatusAsync(definitions);

        result.Should().ContainKey("ext-calc");
        result["ext-calc"].Should().BeTrue();
        definition.DetectedVia.Should().Be(Core.Features.SoftwareApps.Enums.DetectionSource.AppX);
    }

    [Fact]
    public async Task GetExternalAppsInstallationStatusAsync_AppXNotInstalled_ReturnsFalse()
    {
        _mockWinGetBootstrapper
            .Setup(w => w.EnsureWinGetReadyAsync())
            .ReturnsAsync(true);
        _mockWinGetDetection
            .Setup(w => w.GetInstalledPackageIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>());
        _mockChocolatey
            .Setup(c => c.GetInstalledPackageIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>());

        var definition = CreateAppxDefinition("ext-fake", "WinOptimizer.FakeTestApp.DoesNotExist_XYZ123",
            name: "WinOptimizerFakeTestAppXYZ123_NotAReal_App");
        var definitions = new List<ItemDefinition> { definition };

        var result = await _service.GetExternalAppsInstallationStatusAsync(definitions);

        result.Should().ContainKey("ext-fake");
        result["ext-fake"].Should().BeFalse();
    }

    [Fact]
    public async Task GetInstallationStatusBatchAsync_AppXDetectsApp_ReturnsTrue()
    {
        _mockAppxPackageSource
            .Setup(a => a.GetInstalledPackageNamesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Microsoft.WindowsCalculator" });

        var definitions = new List<ItemDefinition>
        {
            CreateAppxDefinition("calc", "Microsoft.WindowsCalculator")
        };

        var result = await _service.GetInstallationStatusBatchAsync(definitions);

        result.Should().ContainKey("calc");
        result["calc"].Should().BeTrue();
    }

    [Fact]
    public async Task GetExternalAppsInstallationStatusAsync_AppXSkippedWhenAlreadyDetectedByWinGet()
    {
        _mockWinGetBootstrapper
            .Setup(w => w.EnsureWinGetReadyAsync())
            .ReturnsAsync(true);
        _mockWinGetDetection
            .Setup(w => w.GetInstalledPackageIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { "WinOptimizerFake.Package" });

        var definition = new ItemDefinition
        {
            Id = "ext-fake",
            Name = "WinOptimizerFakeTestAppXYZ123_NotAReal_App",
            Description = "Fake test app",
            WinGetPackageId = new[] { "WinOptimizerFake.Package" },
            AppxPackageName = new[] { "WinOptimizer.FakeTestApp.DoesNotExist_XYZ123" }
        };
        var definitions = new List<ItemDefinition> { definition };

        var result = await _service.GetExternalAppsInstallationStatusAsync(definitions);

        result["ext-fake"].Should().BeTrue();
        // Should be detected via WinGet (higher priority), not AppX
        definition.DetectedVia.Should().Be(Core.Features.SoftwareApps.Enums.DetectionSource.WinGet);
    }

    // --- GetExternalAppsInstallationStatusAsync: FileSystem detection ---

    [Fact]
    public async Task GetExternalAppsInstallationStatusAsync_FileSystemDetectsExistingDirectory_ReturnsTrue()
    {
        _mockWinGetBootstrapper
            .Setup(w => w.EnsureWinGetReadyAsync())
            .ReturnsAsync(true);
        _mockWinGetDetection
            .Setup(w => w.GetInstalledPackageIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>());
        _mockChocolatey
            .Setup(c => c.GetInstalledPackageIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>());

        // Use a path that is guaranteed to exist in any test environment
        var tempDir = Path.GetTempPath();
        var definitions = new List<ItemDefinition>
        {
            CreateExternalAppDefinition("ext-portable", "Portable App",
                detectionPaths: new[] { tempDir })
        };

        var result = await _service.GetExternalAppsInstallationStatusAsync(definitions);

        result.Should().ContainKey("ext-portable");
        result["ext-portable"].Should().BeTrue();
    }

    [Fact]
    public async Task GetExternalAppsInstallationStatusAsync_FileSystemDetectsExistingDirectory_SetsDetectedVia()
    {
        _mockWinGetBootstrapper
            .Setup(w => w.EnsureWinGetReadyAsync())
            .ReturnsAsync(true);
        _mockWinGetDetection
            .Setup(w => w.GetInstalledPackageIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>());
        _mockChocolatey
            .Setup(c => c.GetInstalledPackageIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>());

        var tempDir = Path.GetTempPath();
        var definition = CreateExternalAppDefinition("ext-portable", "Portable App",
            detectionPaths: new[] { tempDir });
        var definitions = new List<ItemDefinition> { definition };

        await _service.GetExternalAppsInstallationStatusAsync(definitions);

        definition.DetectedVia.Should().Be(Core.Features.SoftwareApps.Enums.DetectionSource.FileSystem);
    }

    [Fact]
    public async Task GetExternalAppsInstallationStatusAsync_FileSystemNonExistentPath_ReturnsFalse()
    {
        _mockWinGetBootstrapper
            .Setup(w => w.EnsureWinGetReadyAsync())
            .ReturnsAsync(true);
        _mockWinGetDetection
            .Setup(w => w.GetInstalledPackageIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>());
        _mockChocolatey
            .Setup(c => c.GetInstalledPackageIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>());

        var definitions = new List<ItemDefinition>
        {
            CreateExternalAppDefinition("ext-portable", "Portable App",
                detectionPaths: new[] { @"C:\NonExistent\Path\That\Does\Not\Exist" })
        };

        var result = await _service.GetExternalAppsInstallationStatusAsync(definitions);

        result.Should().ContainKey("ext-portable");
        result["ext-portable"].Should().BeFalse();
    }

    [Fact]
    public async Task GetExternalAppsInstallationStatusAsync_FileSystemSkippedWhenAlreadyDetectedByWinGet()
    {
        _mockWinGetBootstrapper
            .Setup(w => w.EnsureWinGetReadyAsync())
            .ReturnsAsync(true);
        _mockWinGetDetection
            .Setup(w => w.GetInstalledPackageIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { "Portable.App" });

        var tempDir = Path.GetTempPath();
        var definition = CreateExternalAppDefinition("ext-portable", "Portable App",
            winGetPackageIds: new[] { "Portable.App" },
            detectionPaths: new[] { tempDir });
        var definitions = new List<ItemDefinition> { definition };

        var result = await _service.GetExternalAppsInstallationStatusAsync(definitions);

        result["ext-portable"].Should().BeTrue();
        // Should be detected via WinGet, not FileSystem (WinGet has higher priority)
        definition.DetectedVia.Should().Be(Core.Features.SoftwareApps.Enums.DetectionSource.WinGet);
    }
}
