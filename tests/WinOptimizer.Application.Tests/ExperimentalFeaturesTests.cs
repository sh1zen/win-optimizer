using System.Runtime.InteropServices;
using Moq;
using WinOptimizer.Application.AdvancedTools.Experimental;
using WinOptimizer.Core.Features.AdvancedTools.Experimental;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using Xunit;

namespace WinOptimizer.Application.Tests;

public sealed class ExperimentalFeaturesTests
{
    private const uint Pointer = 27829265, Shared = 58381341, FirstRun = 58527096, SecondRun = 57156807;
    private static readonly FeatureOverride On = new FeatureOverride(FeatureOverrideState.Enabled).AsRuntime();
    private static readonly FeatureOverride Off = new FeatureOverride(FeatureOverrideState.Disabled).AsRuntime();
    private readonly MemoryRecovery _recovery = new();
    private readonly Backend _backend = new();
    private readonly Mock<IExperimentalProfileFile> _profiles = new();
    private readonly Mock<IApplicationModeService> _mode = new();
    private readonly Mock<IChangeHistoryService> _history = new();

    private ExperimentalFeaturesService CreateService()
    {
        _mode.Setup(mode => mode.BeginNormalModeSystemOperation()).Returns(Mock.Of<IDisposable>());
        var log = new Mock<ILogService>();
        log.Setup(item => item.BeginTopic(LogTopic.WindowsTools)).Returns(Mock.Of<IDisposable>());
        var localization = new Mock<ILocalizationService>();
        localization.Setup(item => item.GetString(It.IsAny<string>())).Returns((string key) => key);
        return new(_backend, _recovery, _mode.Object, _history.Object, log.Object, localization.Object, _profiles.Object);
    }

    [Fact]
    public async Task DiscoveryJoinsUnmappedIdsByStoreAndKeepsPrioritiesWithoutWriting()
    {
        const uint both = 40000001, startupOnly = 40000002, runtimeOnly = 40000003;
        _backend.Windows = _backend.Windows with { UsesEnglish = false, CanWrite = false };
        _backend.FlagInventory = new(new([
            new(Pointer, 9, FeatureOverrideState.Enabled), new(Shared, 9, FeatureOverrideState.Enabled),
            new(runtimeOnly, 4, FeatureOverrideState.Default), new(both, 4, FeatureOverrideState.Disabled),
            new(both, 9, FeatureOverrideState.Enabled)
        ]), new([
            new(startupOnly, 9, null), new(both, 4, FeatureOverrideState.Enabled)
        ]));
        var service = CreateService();

        var discovery = Assert.IsType<ExperimentalFlagDiscovery>((await service.DiscoverAsync()).FlagDiscovery);
        Assert.Equal(new[] { both, startupOnly, runtimeOnly }, discovery.Flags.Select(flag => flag.Id));
        Assert.Equal(new uint[] { 9, 4 }, discovery.Flags[0].Runtime.Select(item => item.Priority));
        Assert.Equal(FeatureOverrideState.Enabled, Assert.Single(discovery.Flags[0].Startup).State);
        Assert.Empty(discovery.Flags[1].Runtime);
        Assert.Null(Assert.Single(discovery.Flags[1].Startup).State);
        Assert.Empty(discovery.Flags[2].Startup);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ChangeAsync(both.ToString(), ExperimentalAction.Enable));
        Assert.Empty(_backend.Writes);
    }

    [Fact]
    public async Task RefreshUsesCurrentBuildAndPreservesPartialInventoryErrors()
    {
        var service = CreateService();
        Assert.Empty((await service.DiscoverAsync()).FlagDiscovery!.Flags);
        _backend.Windows = _backend.Windows with { Revision = 10076 };
        _backend.FlagInventory = new(new([], "Runtime unavailable"), new([new(Pointer, 9, FeatureOverrideState.Disabled)]));

        var snapshot = await service.DiscoverAsync();
        var discovery = Assert.IsType<ExperimentalFlagDiscovery>(snapshot.FlagDiscovery);
        Assert.Equal("Runtime unavailable", discovery.RuntimeError);
        Assert.Null(discovery.StartupError);
        Assert.Equal(Pointer, Assert.Single(discovery.Flags).Id);
        Assert.All(snapshot.Features, feature => Assert.Equal("Experimental_UnverifiedBuild", feature.EnableBlocked));

        _backend.Windows = _backend.Windows with { Build = 18363 };
        Assert.Null((await service.DiscoverAsync()).FlagDiscovery);
        Assert.Equal(2, _backend.InventoryReads);
    }

    [Fact]
    public async Task RechecksExactRevisionAndPrerequisitesAtWriteBoundary()
    {
        var service = CreateService();
        var discovery = await service.DiscoverAsync();
        Assert.All(discovery.Features, feature => Assert.Equal(ExperimentalCompatibility.Supported, feature.Availability.Compatibility));
        _backend.Windows = _backend.Windows with { UsesEnglish = false };
        Assert.Equal("Experimental_EnglishWindows", (await service.ChangeAsync("Search", ExperimentalAction.Enable)).Error);
        _backend.Windows = _backend.Windows with { UsesEnglish = null };
        Assert.Equal(ExperimentalCompatibility.Unknown, (await service.DiscoverAsync()).Features[0].Availability.Compatibility);
        _backend.Windows = _backend.Windows with { Revision = 10076 };
        Assert.Equal("Experimental_UnverifiedBuild", (await service.ChangeAsync("Run", ExperimentalAction.Enable)).Error);
        _backend.Windows = _backend.Windows with { Revision = 10075, Architecture = Architecture.Arm64 };
        Assert.Equal("Experimental_UnverifiedBuild", (await service.ChangeAsync("Pointer", ExperimentalAction.Enable)).Error);
        _backend.Windows = _backend.Windows with { Build = 19045 };
        Assert.Equal(ExperimentalCompatibility.Unknown, (await service.DiscoverAsync()).Features[0].Availability.Compatibility);
        _backend.Windows = _backend.Windows with { Build = 18363 };
        Assert.Equal(ExperimentalCompatibility.Unsupported, (await service.DiscoverAsync()).Features[0].Availability.Compatibility);
        Assert.Empty(_backend.Writes);
    }

    [Theory]
    [InlineData(26200, 10066, "Run")]
    [InlineData(26300, 10066, "Search")]
    [InlineData(26220, 9606, "Pointer")]
    public async Task BuiltInProfilesCoverDocumentedHistoricalReleases(int build, int revision, string feature)
    {
        _backend.Windows = _backend.Windows with { Build = build, Revision = revision };
        Assert.False((await CreateService().ChangeAsync(feature, ExperimentalAction.Enable)).RecoveryRequired);
        Assert.NotEmpty(_backend.Writes);
    }

    [Fact]
    public async Task ImportSelectsTheInstalledReleaseBundleAndSurvivesServiceRestart()
    {
        const uint replacement = 58989092;
        _backend.Windows = _backend.Windows with { Build = 28120, Revision = 3542 };
        var profile = new ExperimentalBuildProfile("Run", new(28120, 3542, [Architecture.X64]),
            [new(Shared, On, null, Dependency: true), new(replacement, On with { Variant = 3 }, Off)],
            [new("https://learn.microsoft.com/en-us/windows-insider/release-notes/experimental-26-h1/preview-build-28120-3542")]);
        _profiles.Setup(reader => reader.Read("selected.json")).Returns([profile]);
        var service = CreateService();

        Assert.Equal(1, await service.ImportProfilesAsync("selected.json"));
        Assert.Empty(_backend.Writes);
        var reloaded = CreateService();
        var run = (await reloaded.DiscoverAsync()).Features.Single(item => item.Feature.Key == "Run");
        Assert.True(run.Feature.Imported);
        Assert.Equal(new uint[] { Shared, replacement }, run.Feature.Requirements.Select(item => item.Id));

        var result = await reloaded.ChangeAsync("Run", ExperimentalAction.Enable);
        Assert.False(result.RecoveryRequired);
        Assert.Equal(On with { Variant = 3 }, _backend.Read(replacement).Persistent);
        Assert.DoesNotContain(_backend.Writes, write => write.Id == FirstRun);

        _backend.Windows = _backend.Windows with { Revision = 3543 };
        Assert.Equal("Experimental_UnverifiedBuild", (await reloaded.ChangeAsync("Run", ExperimentalAction.Enable)).Error);
    }

    [Fact]
    public async Task InvalidOrOverlappingImportPreservesTheCurrentProfiles()
    {
        var valid = new ExperimentalBuildProfile("Pointer", new(28000, 1, [Architecture.X64]),
            [new(Pointer, On, Off)], [new("https://example.org/evidence")]);
        var selected = new[] { valid };
        _profiles.Setup(reader => reader.Read("selected.json")).Returns(() => selected);
        var service = CreateService();
        await service.ImportProfilesAsync("selected.json");
        selected = [valid, valid];
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportProfilesAsync("selected.json"));
        selected = [valid with { Target = new(26200, 10075, [Architecture.X64]) }];
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportProfilesAsync("selected.json"));
        Assert.Equal(valid, Assert.Single(_recovery.State.Profiles!));
        Assert.Empty(_backend.Writes);
    }

    [Fact]
    public async Task UnfinishedRecoveryIsRetainedAcrossBuildChangesAndResumesOnTheOriginalBuild()
    {
        var service = CreateService();
        var original = _backend.Windows;
        const uint newBuildId = 58989092;
        _profiles.Setup(reader => reader.Read("selected.json")).Returns(
            [new("Run", new(28120, 3542, [Architecture.X64]), [new(newBuildId, On, Off)], [new("https://example.org/evidence")])]);
        await service.ImportProfilesAsync("selected.json");
        _backend.BeforeWrite = (_, store) =>
        {
            if (store == ExperimentalStore.Persistence) throw new IOException("Boot write failed.");
        };
        Assert.True((await service.ChangeAsync("Pointer", ExperimentalAction.Enable)).RecoveryRequired);

        _backend.BeforeWrite = null;
        _backend.Windows = original with { Build = 28120, Revision = 3542 };
        Assert.False((await service.DiscoverAsync()).RecoveryRequired);
        Assert.Equal("Experimental_RecoveryBuild", (await service.ChangeAsync("Pointer", ExperimentalAction.Undo)).Error);
        Assert.False((await service.ChangeAsync("Run", ExperimentalAction.Enable)).RecoveryRequired);
        Assert.Single(_recovery.State.Deferred!);

        _backend.Windows = original;
        Assert.True((await service.DiscoverAsync()).RecoveryRequired);
        Assert.False((await service.ChangeAsync("Pointer", ExperimentalAction.Undo)).RecoveryRequired);
        Assert.Null(_backend.Read(Pointer).Runtime);
        Assert.Equal(On, _backend.Read(newBuildId).Persistent);
    }

    [Fact]
    public async Task UndoRestoresDifferentRuntimeAndPersistentVariantsExactly()
    {
        var service = CreateService();
        var runtime = On with { Variant = 12, Options = 1, Payload = uint.MaxValue, PayloadKind = 2 };
        var persisted = new FeatureOverride(FeatureOverrideState.Default, Variant: 7, PayloadKind: 1, Payload: 42);
        _backend.Seed(Pointer, runtime, persisted);
        _backend.BeforeWrite = (_, _) =>
        {
            var item = Assert.Single((_recovery.State.Undo ?? _recovery.State.Last)!.Items);
            Assert.Equal(Pointer, item.Before.Id);
            Assert.True(item.RuntimeStage == ExperimentalWriteStage.Started || item.PersistenceStage == ExperimentalWriteStage.Started);
        };

        Assert.False((await service.ChangeAsync("Pointer", ExperimentalAction.Disable)).RecoveryRequired);
        Assert.Equal(Off, _backend.Read(Pointer).Persistent);
        Assert.Equal(Off, _backend.Read(Pointer).Runtime?.UserOverride);
        var snapshot = await service.DiscoverAsync();
        Assert.Equal(ExperimentalRestart.Pending, snapshot.Restarts[Pointer]);
        Assert.Null(snapshot.Observations[Pointer].Startup);

        Assert.False((await service.ChangeAsync("Pointer", ExperimentalAction.Undo)).RecoveryRequired);
        Assert.Equal(persisted, _backend.Read(Pointer).Persistent);
        Assert.Equal(runtime, _backend.Read(Pointer).Runtime?.UserOverride);
        Assert.Null((await service.DiscoverAsync()).UndoFeature);
    }

    [Fact]
    public async Task RestoreDefaultResetsOnlySelectedIdAndUndoRestoresAbsence()
    {
        var service = CreateService();
        _backend.Seed(FirstRun, Off, On);
        Assert.False((await service.ChangeAsync("Pointer", ExperimentalAction.Enable)).RecoveryRequired);
        Assert.False((await service.ChangeAsync("Pointer", ExperimentalAction.Undo)).RecoveryRequired);
        Assert.Null(_backend.Read(Pointer).Persistent);
        Assert.Null(_backend.Read(Pointer).Runtime);

        _backend.Seed(Pointer, Off, Off);
        _backend.Writes.Clear();
        var restore = await service.ChangeAsync("Pointer", ExperimentalAction.RestoreDefault);
        Assert.Equal(ExperimentalWriteStage.Verified, Assert.Single(restore.Items).Persistence);
        Assert.All(_backend.Writes, write => Assert.Equal((Pointer, (FeatureOverride?)null), (write.Id, write.Value)));
        Assert.Equal(On, _backend.Read(FirstRun).Persistent);
    }

    [Fact]
    public async Task SharedDependenciesAreDetectedAndInUseIdsAreProtected()
    {
        var service = CreateService();
        Assert.False((await service.ChangeAsync("Run", ExperimentalAction.Enable)).RecoveryRequired);
        var snapshot = await service.DiscoverAsync();
        Assert.Equal(Shared, Assert.Single(snapshot.Features.Single(feature => feature.Feature.Key == "Search").SharedIds));
        var blocked = await service.ChangeAsync("Search", ExperimentalAction.Disable);
        Assert.Equal("Experimental_SharedConflict", blocked.Error);
        var restore = await service.ChangeAsync("Search", ExperimentalAction.RestoreDefault);
        Assert.Equal("Experimental_SharedConflict", restore.Error);
        Assert.Equal(On, _backend.Read(Shared).Persistent);
    }

    [Fact]
    public async Task EnablingRunCannotReplaceASharedOverrideUsedToDisableSearch()
    {
        var service = CreateService();
        Assert.False((await service.ChangeAsync("Search", ExperimentalAction.Disable)).RecoveryRequired);
        _backend.Writes.Clear();
        var blocked = await service.ChangeAsync("Run", ExperimentalAction.Enable);
        Assert.Equal("Experimental_SharedConflict", blocked.Error);
        Assert.Empty(_backend.Writes);
    }

    [Fact]
    public void UnverifiedEntryAndConflictingRequirementsAreRejected()
    {
        var feature = ExperimentalCatalog.Features.Single(item => item.Key == "Pointer");
        var unverified = feature with { Verified = false };
        var policy = new ExperimentalFeaturePolicy([unverified]);
        Assert.Equal(ExperimentalCompatibility.Unknown, policy.Compatibility(unverified, _backend.Windows).Compatibility);
        var contradictory = feature with { Requirements = [new(Pointer, On, Off), new(Pointer, Off, On)] };
        Assert.Equal("Experimental_ConflictingRequirements", policy.Plan(contradictory, ExperimentalAction.Enable,
            new Dictionary<uint, ExperimentalObservation>(), out _));
    }

    [Fact]
    public async Task HigherPriorityOrQueryErrorsPreventWrites()
    {
        var service = CreateService();
        _backend.Observations[Pointer] = new(Pointer, new(10, On), null, Off, 1);
        Assert.Equal("Experimental_HigherPriority", (await service.ChangeAsync("Pointer", ExperimentalAction.Enable)).Error);
        _backend.Observations[Pointer] = new(Pointer, null, null, null, 1, PersistenceError: "access denied");
        Assert.Equal("Experimental_QueryFailed", (await service.ChangeAsync("Pointer", ExperimentalAction.Enable)).Error);
        Assert.Empty(_backend.Writes);
    }

    [Fact]
    public async Task PartialPersistenceFailureIsSeparatedAndRecoverable()
    {
        var service = CreateService();
        _backend.BeforeWrite = (id, store) =>
        {
            if (id != FirstRun || store != ExperimentalStore.Persistence) return;
            _backend.Observations[id] = _backend.Read(id) with
            {
                Persistent = new(FeatureOverrideState.Enabled, Options: 0)
            };
            throw new IOException("Boot storage failed.");
        };
        var result = await service.ChangeAsync("Run", ExperimentalAction.Enable);
        Assert.True(result.RecoveryRequired);
        Assert.Equal(ExperimentalWriteStage.Verified, result.Items.Single(item => item.Id == FirstRun).Runtime);
        Assert.Equal("Boot storage failed.", result.Items.Single(item => item.Id == FirstRun).PersistenceError);
        Assert.Equal(ExperimentalWriteStage.Ready, result.Items.Single(item => item.Id == SecondRun).Runtime);
        Assert.Equal("Experimental_RecoveryRequired", (await service.ChangeAsync("Pointer", ExperimentalAction.Enable)).Error);

        _backend.BeforeWrite = null;
        var undo = await CreateService().ChangeAsync("Run", ExperimentalAction.Undo);
        Assert.False(undo.RecoveryRequired);
        Assert.All(new[] { Shared, FirstRun, SecondRun }, id =>
        {
            Assert.Null(_backend.Read(id).Persistent);
            Assert.Null(_backend.Read(id).Runtime);
        });
    }

    [Fact]
    public async Task CancellationBetweenStoresLeavesRecoverableRuntimeChange()
    {
        var service = CreateService();
        using var cancellation = new CancellationTokenSource();
        _backend.BeforeWrite = (_, _) => cancellation.Cancel();
        var result = await service.ChangeAsync("Pointer", ExperimentalAction.Enable, cancellation.Token);
        Assert.True(result.Cancelled);
        Assert.True(result.RecoveryRequired);
        Assert.Equal(ExperimentalStore.Runtime, Assert.Single(_backend.Writes).Store);
        Assert.Null(_backend.Read(Pointer).Persistent);
        _backend.BeforeWrite = null;
        Assert.False((await service.ChangeAsync("Pointer", ExperimentalAction.Undo)).RecoveryRequired);
        Assert.Null(_backend.Read(Pointer).Runtime);
    }

    [Fact]
    public async Task RuntimeFailureStopsBeforePersistenceAndUndoCanFinishWithoutAWastefulWrite()
    {
        var service = CreateService();
        _backend.BeforeWrite = (_, _) => throw new IOException("Runtime rejected.");
        var result = await service.ChangeAsync("Pointer", ExperimentalAction.Enable);
        var item = Assert.Single(result.Items);
        Assert.Equal("Runtime rejected.", item.RuntimeError);
        Assert.Equal(ExperimentalWriteStage.Ready, item.Persistence);
        Assert.True(result.RecoveryRequired);

        _backend.BeforeWrite = null;
        Assert.False((await service.ChangeAsync("Pointer", ExperimentalAction.Undo)).RecoveryRequired);
        Assert.Empty(_backend.Writes);
        Assert.Equal(ExperimentalRestart.None, (await service.DiscoverAsync()).Restarts[Pointer]);
    }

    [Fact]
    public async Task InterruptedUndoResumesTheStoresThatHaveNotBeenVerified()
    {
        var service = CreateService();
        await service.ChangeAsync("Pointer", ExperimentalAction.Enable);
        _backend.BeforeWrite = (_, store) =>
        {
            if (store == ExperimentalStore.Persistence) throw new IOException("Boot scheduling failed.");
        };
        Assert.True((await service.ChangeAsync("Pointer", ExperimentalAction.Undo)).RecoveryRequired);
        Assert.Null(_backend.Read(Pointer).Runtime);
        Assert.Equal(On, _backend.Read(Pointer).Persistent);

        _backend.BeforeWrite = null;
        _backend.Writes.Clear();
        Assert.False((await CreateService().ChangeAsync("Pointer", ExperimentalAction.Undo)).RecoveryRequired);
        Assert.Equal(ExperimentalStore.Persistence, Assert.Single(_backend.Writes).Store);
        Assert.Null(_backend.Read(Pointer).Persistent);
    }

    [Fact]
    public async Task RecoveryFileFailurePreventsFirstWrite()
    {
        var service = CreateService();
        _recovery.RejectSave = true;
        var result = await service.ChangeAsync("Pointer", ExperimentalAction.Enable);
        Assert.Equal("Recovery disk full.", result.Error);
        Assert.Empty(_backend.Writes);
        _history.Verify(history => history.LogMaintenance(LogTopic.WindowsTools, "Experimental_Pointer_Name",
            It.Is<string>(text => text.Contains("Experimental_ChangeFailed")), null, null), Times.Once);
    }

    [Fact]
    public async Task UndoDetectsAnExternalChangeBeforeTouchingEitherStore()
    {
        var service = CreateService();
        await service.ChangeAsync("Run", ExperimentalAction.Enable);
        _backend.Observations[SecondRun] = _backend.Read(SecondRun) with { Persistent = Off };
        _backend.Writes.Clear();
        var result = await service.ChangeAsync("Run", ExperimentalAction.Undo);
        Assert.Equal("Experimental_ExternalChange", result.Error);
        Assert.Empty(_backend.Writes);
    }

    [Fact]
    public async Task RestartRequiresNewBootAndMatchingStartupSnapshot()
    {
        var service = CreateService();
        await service.ChangeAsync("Pointer", ExperimentalAction.Enable);
        _backend.Observations[Pointer] = _backend.Read(Pointer) with { Startup = new(8, On) };
        Assert.Equal(ExperimentalRestart.Pending, (await service.DiscoverAsync()).Restarts[Pointer]);
        _backend.Windows = _backend.Windows with { BootTime = 2 };
        _backend.Observations[Pointer] = _backend.Read(Pointer) with { Startup = new(8, Off) };
        Assert.Equal(ExperimentalRestart.Unverified, (await service.DiscoverAsync()).Restarts[Pointer]);
        _backend.Observations[Pointer] = _backend.Read(Pointer) with { Startup = new(8, On) };
        Assert.Equal(ExperimentalRestart.None, (await service.DiscoverAsync()).Restarts[Pointer]);
    }

    [Fact]
    public async Task DiscoveryReportsPersistenceChangedOutsideTheApplication()
    {
        var service = CreateService();
        _backend.Seed(Pointer, Off, On);
        var snapshot = await service.DiscoverAsync();
        Assert.Equal(ExperimentalRestart.Pending, snapshot.Restarts[Pointer]);
        Assert.Equal(Off, snapshot.Observations[Pointer].Runtime?.UserOverride);
        Assert.Equal(On, snapshot.Observations[Pointer].Persistent);
    }

    [Theory]
    [InlineData(WinOptimizerMode.Builder)]
    [InlineData(WinOptimizerMode.ConfigReview)]
    public async Task ProtectedModesBlockWrites(WinOptimizerMode mode)
    {
        var service = CreateService();
        _mode.SetupGet(item => item.CurrentMode).Returns(mode);
        _mode.Setup(item => item.BeginNormalModeSystemOperation()).Throws(new InvalidOperationException("Mode blocked."));
        var result = await service.ChangeAsync("Pointer", ExperimentalAction.Enable);
        Assert.Equal("Mode blocked.", result.Error);
        Assert.Empty(_backend.Writes);
        Assert.Null(_recovery.State.Last);
    }

    private sealed class MemoryRecovery : IExperimentalRecoveryStore
    {
        public ExperimentalRecovery State { get; private set; } = new(null, null, []);
        public bool RejectSave { get; set; }
        public IDisposable ReserveWrite() => Mock.Of<IDisposable>();
        public ExperimentalRecovery Read() => State;
        public void Save(ExperimentalRecovery state) => State = RejectSave ? throw new IOException("Recovery disk full.") : state;
    }

    private sealed class Backend : IExperimentalFeatureBackend
    {
        public ExperimentalWindows Windows { get; set; } =
            new(10, 0, 26200, 10075, Architecture.X64, "25H2", true, 1, true, true);
        public Dictionary<uint, ExperimentalObservation> Observations { get; } = [];
        public List<(uint Id, ExperimentalStore Store, FeatureOverride? Value)> Writes { get; } = [];
        public ExperimentalFlagInventory FlagInventory { get; set; } = new(new([]), new([]));
        public int InventoryReads { get; private set; }
        public Action<uint, ExperimentalStore>? BeforeWrite { get; set; }
        public ExperimentalWindows GetWindows() => Windows;
        public ExperimentalObservation Read(uint id) => Observations.GetValueOrDefault(id) ?? new(id, null, null, null, 1);
        public ExperimentalFlagInventory ReadFlagInventory(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InventoryReads++;
            return FlagInventory;
        }
        public void Seed(uint id, FeatureOverride runtime, FeatureOverride persisted) =>
            Observations[id] = new(id, new(8, runtime), null, persisted, 1);
        public void Write(ExperimentalWindows windows, ExperimentalObservation before, ExperimentalStore store, FeatureOverride? value)
        {
            BeforeWrite?.Invoke(before.Id, store);
            Writes.Add((before.Id, store, value));
            Observations[before.Id] = store == ExperimentalStore.Runtime
                ? before with { Runtime = value is null ? null : new(8, value) }
                : before with { Persistent = value };
        }
    }
}
