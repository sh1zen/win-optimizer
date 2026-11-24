using System.ComponentModel;
using System.Security;
using System.Text.Json;
using WinOptimizer.Core.Features.AdvancedTools.Experimental;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Application.AdvancedTools.Experimental;

/// <summary>Serializes discovery and changes, and owns the recoverable two-store write workflow.</summary>
internal sealed class ExperimentalFeaturesService(IExperimentalFeatureBackend backend, IExperimentalRecoveryStore recovery,
    IApplicationModeService mode, IChangeHistoryService history, ILogService log, ILocalizationService localization,
    IExperimentalProfileFile profileFile)
    : IExperimentalFeatures
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ExperimentalDiscovery> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await Task.Run(() => Discover(cancellationToken), cancellationToken); }
        finally { _gate.Release(); }
    }

    public async Task<ExperimentalChangeResult> ChangeAsync(string featureKey, ExperimentalAction action,
        CancellationToken cancellationToken = default)
    {
        var feature = ExperimentalCatalog.Features.FirstOrDefault(item => item.Key == featureKey)
            ?? throw new ArgumentException("Unknown experimental feature.", nameof(featureKey));
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));

        ExperimentalChangeResult result;
        try
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                using var reservation = mode.BeginNormalModeSystemOperation();
                result = await Task.Run(() => Change(feature, action, cancellationToken), cancellationToken);
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = new([], true, false);
        }
        catch (Exception ex) when (IsOperationFailure(ex))
        {
            result = new([], false, false, ex.Message);
        }

        Record(feature, action, result);
        return result;
    }

    /// <summary>Replaces the local profile set without writing either Windows configuration store.</summary>
    public async Task<int> ImportProfilesAsync(string path, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var reservation = mode.BeginNormalModeSystemOperation();
            return await Task.Run(() =>
            {
                var profiles = profileFile.Read(path);
                ExperimentalCatalog.ValidateProfiles(profiles);
                cancellationToken.ThrowIfCancellationRequested();
                using var writer = recovery.ReserveWrite();
                var state = recovery.Read();
                recovery.Save(state with { Profiles = profiles });
                using var topic = log.BeginTopic(LogTopic.WindowsTools);
                log.Log(LogLevel.Info, $"[Experimental Features] Imported {profiles.Length} compatibility profiles.");
                return profiles.Length;
            }, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private ExperimentalDiscovery Discover(CancellationToken token)
    {
        var windows = backend.GetWindows();
        var state = SelectRecovery(recovery.Read(), windows);
        var catalog = ExperimentalCatalog.Resolve(windows, state.Profiles);
        var policy = new ExperimentalFeaturePolicy(catalog);
        var observations = ReadCatalog(catalog, state, token);
        var recoveryRequired = RecoveryRequired(state);
        var generalBlock = !windows.CanWrite ? "Experimental_Elevation" :
            recoveryRequired ? "Experimental_RecoveryRequired" : null;
        var mappedIds = new HashSet<uint>();
        var statuses = catalog.Select(feature =>
        {
            var availability = policy.Compatibility(feature, windows);
            var mapping = policy.Compatibility(feature, windows, checkPrerequisites: false);
            if (mapping.Compatibility == ExperimentalCompatibility.Supported)
                mappedIds.UnionWith(feature.Requirements.Select(item => item.Id));
            string? Block(ExperimentalAction action, ExperimentalAvailability compatibility) => generalBlock ??
                (compatibility.Compatibility == ExperimentalCompatibility.Supported
                    ? policy.Plan(feature, action, observations, out _) : compatibility.ReasonKey);
            return new ExperimentalFeatureStatus(feature, availability, policy.SharedIds(feature),
                Block(ExperimentalAction.Enable, availability), Block(ExperimentalAction.Disable, mapping),
                Block(ExperimentalAction.RestoreDefault, mapping));
        }).ToArray();

        var restarts = observations.ToDictionary(pair => pair.Key, pair =>
            ExperimentalFeaturePolicy.Restart(state.Pending.GetValueOrDefault(pair.Key), windows, pair.Value));
        var deferred = state.Deferred?.LastOrDefault();
        var undo = state.Undo ?? state.Last ?? deferred?.Undo ?? deferred?.Last;
        return new(windows, statuses, observations, restarts, undo?.FeatureKey,
            !windows.CanWrite ? "Experimental_Elevation" : undo is null ? "Experimental_NoUndo" :
                UndoBlock(undo, windows, observations),
            recoveryRequired)
        {
            FlagDiscovery = ExperimentalFeaturePolicy.SupportsFeatureConfiguration(windows)
                ? DiscoverFlags(mappedIds, token) : null
        };
    }

    /// <summary>Combines both Windows views by ID and retains every priority for unmapped flags.</summary>
    private ExperimentalFlagDiscovery DiscoverFlags(HashSet<uint> mappedIds, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var inventory = backend.ReadFlagInventory(token);
        var runtime = inventory.Runtime.Configurations.ToLookup(item => item.Id);
        var startup = inventory.Startup.Configurations.ToLookup(item => item.Id);
        var flags = runtime.Select(group => group.Key).Union(startup.Select(group => group.Key))
            .Where(id => !mappedIds.Contains(id)).Order();
        var results = new List<ExperimentalDiscoveredFlag>();
        foreach (var id in flags)
        {
            token.ThrowIfCancellationRequested();
            results.Add(new(id, runtime[id].OrderByDescending(item => item.Priority).ToArray(),
                startup[id].OrderByDescending(item => item.Priority).ToArray()));
        }
        token.ThrowIfCancellationRequested();
        return new(results, inventory.Runtime.Error, inventory.Startup.Error);
    }

    private ExperimentalChangeResult Change(ExperimentalFeature feature, ExperimentalAction action, CancellationToken token)
    {
        using var recoveryReservation = recovery.ReserveWrite();
        var windows = backend.GetWindows();
        var state = SelectRecovery(recovery.Read(), windows);
        ExperimentalChangeResult Rejected(string error) => new([], false, RecoveryRequired(state), error);
        if (WriteBlock(windows) is { } blocked) return Rejected(blocked);
        if (action != ExperimentalAction.Undo && RecoveryRequired(state)) return Rejected("Experimental_RecoveryRequired");

        var catalog = ExperimentalCatalog.Resolve(windows, state.Profiles);
        var observations = ReadCatalog(catalog, state, token);
        ExperimentalReceipt receipt;
        if (action == ExperimentalAction.Undo)
        {
            if (state.Last is not { } last || last.FeatureKey != feature.Key)
                return Rejected(state.Deferred?.Any(item => item.Last.FeatureKey == feature.Key) == true
                    ? "Experimental_RecoveryBuild" : "Experimental_NoUndo");
            if (UndoBlock(state.Undo ?? last, windows, observations) is { } conflict) return Rejected(conflict);
            receipt = state.Undo ?? NewReceipt(feature, windows, last.Items.Select(item =>
            {
                var now = observations[item.Before.Id];
                var runtime = Attempted(item.RuntimeStage) ? item.Before.Runtime?.UserOverride : now.Runtime?.UserOverride;
                var persistent = Attempted(item.PersistenceStage) ? item.Before.Persistent : now.Persistent;
                return BuildItem(now, runtime, persistent, state.Pending.GetValueOrDefault(now.Id));
            }));
            state = state with { Undo = receipt };
        }
        else
        {
            feature = catalog.Single(item => item.Key == feature.Key);
            var policy = new ExperimentalFeaturePolicy(catalog);
            var availability = policy.Compatibility(feature, windows, checkPrerequisites: action == ExperimentalAction.Enable);
            if (availability.Compatibility != ExperimentalCompatibility.Supported) return Rejected(availability.ReasonKey);
            if (policy.Plan(feature, action, observations, out var plan) is { } conflict) return Rejected(conflict);
            receipt = NewReceipt(feature, windows, plan.Select(change =>
                BuildItem(observations[change.Key], change.Value?.AsRuntime(), change.Value,
                    state.Pending.GetValueOrDefault(change.Key))));
            if (receipt.Items.All(item => !NeedsWrite(item)))
                return new(receipt.Items.Select(item => Result(item)).ToArray(), false, false);
            state = state with { Last = receipt };
        }

        return Execute(state, receipt, action == ExperimentalAction.Undo, windows, token);
    }

    private ExperimentalChangeResult Execute(ExperimentalRecovery state, ExperimentalReceipt receipt, bool undo,
        ExperimentalWindows windows, CancellationToken token)
    {
        // A successful initial checkpoint is a precondition for any operating-system write.
        var items = receipt.Items.ToArray();
        var results = items.Select(item => Result(item)).ToArray();
        var pending = new Dictionary<uint, ExperimentalPending>(state.Pending);
        var checkpointed = false;
        void Checkpoint(bool complete = false)
        {
            var current = receipt with { Items = items.ToArray(), Complete = complete };
            recovery.Save(undo
                ? state with { Last = complete ? null : state.Last, Undo = complete ? null : current, Pending = new(pending) }
                : state with { Last = current, Pending = new(pending) });
            checkpointed = true;
        }

        try
        {
            token.ThrowIfCancellationRequested();
            Checkpoint();
            for (int index = 0; index < items.Length; index++)
            {
                var item = items[index];
                foreach (var store in new[] { ExperimentalStore.Runtime, ExperimentalStore.Persistence })
                {
                    var stage = store == ExperimentalStore.Runtime ? item.RuntimeStage : item.PersistenceStage;
                    if (stage is ExperimentalWriteStage.Unchanged or ExperimentalWriteStage.Verified) continue;
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var current = backend.Read(item.Before.Id);
                        if (ExperimentalFeaturePolicy.ReadBlock(current) is { } block)
                            throw new IOException(localization.GetString(block) + " " +
                                (current.RuntimeError ?? current.PersistenceError ?? current.StartupError));
                        if (!Matches(receipt.Windows, windows, item, current))
                            throw new IOException(localization.GetString("Experimental_ExternalChange"));

                        var runtime = store == ExperimentalStore.Runtime;
                        item = runtime ? item with { RuntimeStage = ExperimentalWriteStage.Started }
                            : item with { PersistenceStage = ExperimentalWriteStage.Started };
                        items[index] = item;
                        pending[item.Before.Id] = new(windows, item.Persistent);
                        Checkpoint();

                        backend.Write(windows, current, store, runtime ? item.Runtime : item.Persistent);

                        item = runtime ? item with { RuntimeStage = ExperimentalWriteStage.Verified }
                            : item with { PersistenceStage = ExperimentalWriteStage.Verified };
                        items[index] = item;
                        Checkpoint();
                        results[index] = Result(item);
                    }
                    catch (Exception ex) when (IsOperationFailure(ex))
                    {
                        results[index] = store == ExperimentalStore.Runtime ? Result(item, runtimeError: ex.Message)
                            : Result(item, persistenceError: ex.Message);
                        return new(results, false, true);
                    }
                }
            }
            if (undo && state.Last is { } original)
            {
                foreach (var item in original.Items.Where((_, index) => !NeedsWrite(items[index])))
                {
                    var id = item.Before.Id;
                    if (item.PreviousPending is { } previous) pending[id] = previous;
                    else pending.Remove(id);
                }
            }
            Checkpoint(complete: true);
            return new(results, false, false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new(results, true, checkpointed || RecoveryRequired(state));
        }
        catch (Exception ex) when (IsOperationFailure(ex))
        {
            return new(results, false, checkpointed || RecoveryRequired(state), ex.Message);
        }
    }

    private string? WriteBlock(ExperimentalWindows windows) => mode.CurrentMode != WinOptimizerMode.Normal
        ? "Experimental_NormalMode" : !windows.CanWrite ? "Experimental_Elevation" : null;

    private static string? UndoBlock(ExperimentalReceipt receipt, ExperimentalWindows windows,
        IReadOnlyDictionary<uint, ExperimentalObservation> observations)
    {
        if (!receipt.Windows.SameRelease(windows)) return "Experimental_RecoveryBuild";
        foreach (var item in receipt.Items)
        {
            var current = observations[item.Before.Id];
            if (ExperimentalFeaturePolicy.ReadBlock(current) is { } blocked) return blocked;
            if (!Matches(receipt.Windows, windows, item, current)) return "Experimental_ExternalChange";
        }
        return null;
    }

    private static bool Matches(ExperimentalWindows before, ExperimentalWindows windows, ExperimentalRecoveryItem item,
        ExperimentalObservation current)
    {
        var persistenceMatches = item.PersistenceStage switch
        {
            ExperimentalWriteStage.Verified => current.Persistent == item.Persistent,
            ExperimentalWriteStage.Started => MatchesInterruptedPersistence(item, current.Persistent),
            _ => current.Persistent == item.Before.Persistent
        };
        if (!persistenceMatches) return false;

        if (before.BootTime != windows.BootTime && item.RuntimeStage != ExperimentalWriteStage.Verified)
            return current.Runtime?.UserOverride == current.Startup?.UserOverride &&
                (current.Startup?.UserOverride == item.Before.Persistent?.AsRuntime() ||
                 current.Startup?.UserOverride == item.Persistent?.AsRuntime());
        if (before.BootTime != windows.BootTime && current.Runtime?.UserOverride == current.Startup?.UserOverride &&
            current.Startup?.UserOverride == current.Persistent?.AsRuntime())
            return true;

        return item.RuntimeStage switch
        {
            ExperimentalWriteStage.Verified => current.Runtime?.UserOverride == item.Runtime,
            ExperimentalWriteStage.Started => current.Runtime == item.Before.Runtime || current.Runtime?.UserOverride == item.Runtime,
            _ => current.Runtime == item.Before.Runtime
        };
    }

    private static bool MatchesInterruptedPersistence(ExperimentalRecoveryItem item, FeatureOverride? current)
    {
        if (current == item.Before.Persistent || current == item.Persistent) return true;
        if (current is null || item.Persistent is not { } target) return false;
        // The registry backend applies these five fields in order. Accept a checkpointed prefix
        // after an interrupted write. A value outside that transition is an external change.
        var previous = item.Before.Persistent ?? new FeatureOverride(null);
        return current == previous ||
            current == previous with { State = target.State } ||
            current == previous with { State = target.State, Options = target.Options } ||
            current == target with { PayloadKind = previous.PayloadKind, Payload = previous.Payload } ||
            current == target with { Payload = previous.Payload };
    }

    private Dictionary<uint, ExperimentalObservation> ReadCatalog(IReadOnlyList<ExperimentalFeature> catalog,
        ExperimentalRecovery state, CancellationToken token)
    {
        var ids = catalog.SelectMany(feature => feature.Requirements.Select(item => item.Id))
            .Concat(state.Pending.Keys).Concat(state.Last?.Items.Select(item => item.Before.Id) ?? []).Distinct();
        var observations = new Dictionary<uint, ExperimentalObservation>();
        foreach (var id in ids)
        {
            token.ThrowIfCancellationRequested();
            observations.Add(id, backend.Read(id));
        }
        return observations;
    }

    /// <summary>Retains unfinished recovery with its original release while allowing changes on other releases.</summary>
    private static ExperimentalRecovery SelectRecovery(ExperimentalRecovery state, ExperimentalWindows windows)
    {
        if (state.Last?.Windows.SameRelease(windows) == true) return state;
        var deferred = (state.Deferred ?? []).ToList();
        if (state.Last is { } last && RecoveryRequired(state)) deferred.Add(new(last, state.Undo));
        var match = deferred.FindIndex(item => item.Last.Windows.SameRelease(windows));
        if (match < 0) return state with { Last = null, Undo = null, Deferred = deferred.ToArray() };
        var active = deferred[match];
        deferred.RemoveAt(match);
        return state with { Last = active.Last, Undo = active.Undo, Deferred = deferred.ToArray() };
    }

    private void Record(ExperimentalFeature feature, ExperimentalAction action, ExperimentalChangeResult result)
    {
        using var topic = log.BeginTopic(LogTopic.WindowsTools);
        var failed = result.Error is not null || result.Cancelled || result.RecoveryRequired;
        var summary = localization.GetString(result.Cancelled ? "Experimental_Cancelled" :
            failed ? "Experimental_ChangeFailed" : result.Items.Any(item =>
                Attempted(item.Runtime) || Attempted(item.Persistence)) ? "Experimental_ChangeVerified" : "Experimental_NoChange");
        var message = $"{localization.GetString(ExperimentalActionText.Key(action))} · {summary}";
        var name = localization.GetString(feature.NameKey);
        history.LogMaintenance(LogTopic.WindowsTools, name, message);
        log.Log(failed ? LogLevel.Warning : LogLevel.Info, $"[Experimental Features] {name} · {message} {result.Error}");
        foreach (var item in result.Items)
            log.Log(item.RuntimeError is null && item.PersistenceError is null ? LogLevel.Info : LogLevel.Warning,
                $"[Experimental Features] {item.Id} · runtime {item.Runtime} {item.RuntimeError} · persistence {item.Persistence} {item.PersistenceError}");
    }

    private static ExperimentalReceipt NewReceipt(ExperimentalFeature feature, ExperimentalWindows windows,
        IEnumerable<ExperimentalRecoveryItem> items) => new(Guid.NewGuid(), feature.Key, windows, items.ToArray(), false);

    private static ExperimentalRecoveryItem BuildItem(ExperimentalObservation current, FeatureOverride? runtime,
        FeatureOverride? persisted, ExperimentalPending? previousPending) => new(current, runtime, persisted,
        current.Runtime?.UserOverride == runtime ? ExperimentalWriteStage.Unchanged : ExperimentalWriteStage.Ready,
        current.Persistent == persisted ? ExperimentalWriteStage.Unchanged : ExperimentalWriteStage.Ready, previousPending);

    private static ExperimentalIdResult Result(ExperimentalRecoveryItem item, string? runtimeError = null,
        string? persistenceError = null) => new(item.Before.Id, item.RuntimeStage, item.PersistenceStage, runtimeError, persistenceError);

    private static bool Attempted(ExperimentalWriteStage stage) =>
        stage is ExperimentalWriteStage.Started or ExperimentalWriteStage.Verified;

    private static bool NeedsWrite(ExperimentalRecoveryItem item) =>
        item.RuntimeStage != ExperimentalWriteStage.Unchanged || item.PersistenceStage != ExperimentalWriteStage.Unchanged;

    private static bool RecoveryRequired(ExperimentalRecovery state) => state.Undo is not null || state.Last is { Complete: false };

    private static bool IsOperationFailure(Exception ex) => ex is IOException or InvalidDataException or Win32Exception or SecurityException or
        UnauthorizedAccessException or InvalidOperationException or JsonException or EntryPointNotFoundException;
}
