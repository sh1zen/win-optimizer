using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace WinOptimizer.Core.Features.AdvancedTools.Experimental;

public enum FeatureOverrideState : uint { Default, Disabled, Enabled }
public enum ExperimentalAction { Enable, Disable, RestoreDefault, Undo }
public enum ExperimentalCompatibility { Supported, Unsupported, Unknown }
public enum ExperimentalPrerequisite { EnglishWindows }
public enum ExperimentalRestart { None, Pending, Unverified }
public enum ExperimentalStore { Runtime, Persistence }
public enum ExperimentalWriteStage { Unchanged, Ready, Started, Verified }

/// <summary>Windows identity is detected on each discovery and immediately before writing.</summary>
public sealed record ExperimentalWindows(int Major, int Minor, int Build, int Revision, Architecture Architecture,
    string DisplayVersion, bool IsClient, long BootTime, bool? UsesEnglish, bool CanWrite)
{
    public bool SameRelease(ExperimentalWindows other) =>
        (Major, Minor, Build, Revision, Architecture, IsClient) ==
        (other.Major, other.Minor, other.Build, other.Revision, other.Architecture, other.IsClient);
}

/// <summary>A User override. Nullable fields preserve which DWORDs actually existed in the persistent key.</summary>
public sealed record FeatureOverride(FeatureOverrideState? State, uint? Options = null, uint? Variant = null,
    uint? PayloadKind = null, uint? Payload = null)
{
    public const uint UserPriority = 8;

    public FeatureOverride AsRuntime() => new(State ?? FeatureOverrideState.Default, Options ?? 0,
        Variant ?? 0, PayloadKind ?? 0, Payload ?? 0);

    public bool IsValid => (State is null or >= FeatureOverrideState.Default and <= FeatureOverrideState.Enabled) &&
        Options is null or <= 1 && Variant is null or <= 63 && PayloadKind is null or <= 3;
}

public sealed record ObservedFeature(uint Priority, FeatureOverride Value)
{
    public FeatureOverride? UserOverride => Priority == FeatureOverride.UserPriority ? Value : null;
    public bool MasksUserOverride => Priority > FeatureOverride.UserPriority;
}

/// <summary>Runtime and startup observations are effective configurations, distinct from writable persistence.</summary>
public sealed record ExperimentalObservation(uint Id, ObservedFeature? Runtime, ObservedFeature? Startup,
    FeatureOverride? Persistent, ulong RuntimeStamp, string? RuntimeError = null, string? StartupError = null,
    string? PersistenceError = null);

/// <summary>A reported feature state at one configuration priority. Unknown states retain their ID and priority.</summary>
public sealed record ExperimentalFlagConfiguration(uint Id, uint Priority, FeatureOverrideState? State);
public sealed record ExperimentalFlagStore(IReadOnlyList<ExperimentalFlagConfiguration> Configurations, string? Error = null);
public sealed record ExperimentalFlagInventory(ExperimentalFlagStore Runtime, ExperimentalFlagStore Startup);

/// <summary>A Windows-reported ID without a verified feature mapping on this release.</summary>
public sealed record ExperimentalDiscoveredFlag(uint Id, IReadOnlyList<ExperimentalFlagConfiguration> Runtime,
    IReadOnlyList<ExperimentalFlagConfiguration> Startup);
public sealed record ExperimentalFlagDiscovery(IReadOnlyList<ExperimentalDiscoveredFlag> Flags,
    string? RuntimeError, string? StartupError);

public sealed record ExperimentalBuild(int Build, int Revision, ImmutableArray<Architecture> Architectures);
public sealed record ExperimentalRequirement(uint Id, FeatureOverride Enable, FeatureOverride? Disable,
    bool Dependency = false);

/// <summary>A sourced feature bundle for a specific Windows build and revision.</summary>
public sealed record ExperimentalBuildProfile(string FeatureKey, ExperimentalBuild Target,
    ImmutableArray<ExperimentalRequirement> Requirements, ImmutableArray<Uri> Sources,
    ImmutableArray<ExperimentalPrerequisite>? Prerequisites = null);

/// <summary>Feature metadata includes the exact component bundle and the public evidence supporting it.</summary>
public sealed record ExperimentalFeature(string Key, string CategoryKey, ImmutableArray<ExperimentalRequirement> Requirements,
    ImmutableArray<ExperimentalBuild> Builds, ImmutableArray<ExperimentalPrerequisite> Prerequisites,
    ImmutableArray<Uri> Sources, bool Verified, bool Imported = false)
{
    public string NameKey => $"Experimental_{Key}_Name";
    public string DescriptionKey => $"Experimental_{Key}_Description";
}

public sealed record ExperimentalAvailability(ExperimentalCompatibility Compatibility, string ReasonKey);
public sealed record ExperimentalFeatureStatus(ExperimentalFeature Feature, ExperimentalAvailability Availability,
    uint[] SharedIds, string? EnableBlocked, string? DisableBlocked, string? RestoreBlocked);
public sealed record ExperimentalDiscovery(ExperimentalWindows Windows, IReadOnlyList<ExperimentalFeatureStatus> Features,
    IReadOnlyDictionary<uint, ExperimentalObservation> Observations, IReadOnlyDictionary<uint, ExperimentalRestart> Restarts,
    string? UndoFeature, string? UndoBlocked, bool RecoveryRequired)
{
    public ExperimentalFlagDiscovery? FlagDiscovery { get; init; }
}

/// <summary>Each transaction records both stores before the first write and checkpoints each attempted step.</summary>
public sealed record ExperimentalRecoveryItem(ExperimentalObservation Before, FeatureOverride? Runtime,
    FeatureOverride? Persistent, ExperimentalWriteStage RuntimeStage, ExperimentalWriteStage PersistenceStage,
    ExperimentalPending? PreviousPending);
public sealed record ExperimentalReceipt(Guid Id, string FeatureKey, ExperimentalWindows Windows,
    ExperimentalRecoveryItem[] Items, bool Complete);
public sealed record ExperimentalPending(ExperimentalWindows Windows, FeatureOverride? Persistent);
public sealed record ExperimentalDeferredRecovery(ExperimentalReceipt Last, ExperimentalReceipt? Undo);
public sealed record ExperimentalRecovery(ExperimentalReceipt? Last, ExperimentalReceipt? Undo,
    Dictionary<uint, ExperimentalPending> Pending, ExperimentalBuildProfile[]? Profiles = null,
    ExperimentalDeferredRecovery[]? Deferred = null);

public sealed record ExperimentalIdResult(uint Id, ExperimentalWriteStage Runtime, ExperimentalWriteStage Persistence,
    string? RuntimeError = null, string? PersistenceError = null);
public sealed record ExperimentalChangeResult(IReadOnlyList<ExperimentalIdResult> Items, bool Cancelled,
    bool RecoveryRequired, string? Error = null);

/// <summary>Shares action localization keys between the page and change history.</summary>
public static class ExperimentalActionText
{
    public static string Key(ExperimentalAction action) => action switch
    {
        ExperimentalAction.Enable => "Autoruns_Enable",
        ExperimentalAction.Disable => "Autoruns_Disable",
        ExperimentalAction.RestoreDefault => "Experimental_Action_RestoreDefault",
        ExperimentalAction.Undo => "Experimental_Action_Undo",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };
}
