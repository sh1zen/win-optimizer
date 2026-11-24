using WinOptimizer.Core.Features.AdvancedTools.Experimental;

namespace WinOptimizer.Application.AdvancedTools.Experimental;

/// <summary>Evaluates explicit catalog evidence and protects dependencies shared by multiple feature bundles.</summary>
internal sealed class ExperimentalFeaturePolicy(IReadOnlyList<ExperimentalFeature> catalog)
{
    // This is the Windows client generation where ViVe's runtime feature APIs are available.
    public const int MinimumBuild = 19041;

    public static bool SupportsFeatureConfiguration(ExperimentalWindows windows) =>
        windows.IsClient && windows.Major >= 10 && windows.Build >= MinimumBuild;

    public ExperimentalAvailability Compatibility(ExperimentalFeature feature, ExperimentalWindows windows,
        bool checkPrerequisites = true)
    {
        if (windows.Build == 0 || windows.Revision < 0)
            return new(ExperimentalCompatibility.Unknown, "Experimental_UnverifiedBuild");
        if (!SupportsFeatureConfiguration(windows))
            return new(ExperimentalCompatibility.Unsupported, "Experimental_WindowsClientRequired");
        if (!feature.Verified || !feature.Builds.Any(build =>
                build.Build == windows.Build && build.Revision == windows.Revision &&
                build.Architectures.Contains(windows.Architecture)))
            return new(ExperimentalCompatibility.Unknown, "Experimental_UnverifiedBuild");
        if (!checkPrerequisites) return new(ExperimentalCompatibility.Supported, "Experimental_Supported");
        foreach (var prerequisite in feature.Prerequisites)
        {
            if (prerequisite != ExperimentalPrerequisite.EnglishWindows)
                return new(ExperimentalCompatibility.Unknown, "Experimental_UnverifiedBuild");
            if (windows.UsesEnglish != true)
                return new(windows.UsesEnglish is null ? ExperimentalCompatibility.Unknown : ExperimentalCompatibility.Unsupported,
                    "Experimental_EnglishWindows");
        }
        return new(ExperimentalCompatibility.Supported, "Experimental_Supported");
    }

    public uint[] SharedIds(ExperimentalFeature feature) => feature.Requirements
        .Where(requirement => catalog.Any(other => other.Key != feature.Key &&
            other.Requirements.Any(shared => shared.Id == requirement.Id)))
        .Select(requirement => requirement.Id).Distinct().ToArray();

    public string? Plan(ExperimentalFeature feature, ExperimentalAction action,
        IReadOnlyDictionary<uint, ExperimentalObservation> observations, out Dictionary<uint, FeatureOverride?> changes)
    {
        changes = [];
        foreach (var requirement in feature.Requirements)
        {
            if (action == ExperimentalAction.Disable && requirement.Disable is null) continue;
            var desired = action switch
            {
                ExperimentalAction.Enable => requirement.Enable,
                ExperimentalAction.Disable => requirement.Disable,
                ExperimentalAction.RestoreDefault => null,
                _ => throw new ArgumentOutOfRangeException(nameof(action))
            };
            if (requirement.Id == 0 || desired is { IsValid: false } ||
                changes.TryGetValue(requirement.Id, out var previous) && previous != desired)
                return "Experimental_ConflictingRequirements";
            changes[requirement.Id] = desired;
        }

        foreach (var (id, target) in changes)
        {
            var observed = observations[id];
            if (ReadBlock(observed) is { } blocked) return blocked;
            if (observed.Persistent == target && observed.Runtime?.UserOverride == target?.AsRuntime()) continue;
            if (Conflicts(feature, id, target, observations)) return "Experimental_SharedConflict";
        }
        return null;
    }

    private bool Conflicts(ExperimentalFeature feature, uint id, FeatureOverride? target,
        IReadOnlyDictionary<uint, ExperimentalObservation> observations)
    {
        foreach (var other in catalog.Where(item => item.Key != feature.Key))
        {
            var shared = other.Requirements.FirstOrDefault(item => item.Id == id);
            if (shared is null) continue;
            var compatibleWithEnable = shared.Enable.AsRuntime() == target?.AsRuntime();
            var compatibleWithDisable = shared.Disable is null || shared.Disable.AsRuntime() == target?.AsRuntime();
            if (compatibleWithEnable && compatibleWithDisable) continue;
            // Exclusive components establish whether the peer bundle is using the shared ID.
            var otherComponents = other.Requirements.Where(item => item.Id != id && !item.Dependency).ToArray();
            var evidence = otherComponents.Length == 0 ? [shared] : otherComponents;
            if (evidence.Any(item =>
            {
                var observed = observations[item.Id];
                if (observed.RuntimeError is not null || observed.PersistenceError is not null || observed.StartupError is not null)
                    return true;
                var runtime = observed.Runtime?.Value.State;
                var persisted = observed.Persistent?.State;
                var started = observed.Startup?.Value.State;
                return !compatibleWithEnable && (runtime == item.Enable.State || persisted == item.Enable.State ||
                    started == item.Enable.State) ||
                    !compatibleWithDisable && item.Disable is { } disabled &&
                    (observed.Runtime?.UserOverride?.State == disabled.State || persisted == disabled.State ||
                     observed.Startup?.UserOverride?.State == disabled.State);
            }))
                return true;
        }
        return false;
    }

    public static string? ReadBlock(ExperimentalObservation observed)
    {
        if (observed.RuntimeError is not null || observed.PersistenceError is not null || observed.StartupError is not null)
            return "Experimental_QueryFailed";
        if (observed.Runtime?.MasksUserOverride == true || observed.Startup?.MasksUserOverride == true)
            return "Experimental_HigherPriority";
        return observed.RuntimeStamp == 0 ? "Experimental_QueryFailed" : null;
    }

    public static ExperimentalRestart Restart(ExperimentalPending? pending, ExperimentalWindows windows,
        ExperimentalObservation observed)
    {
        if (pending is null)
        {
            if (observed.PersistenceError is not null || observed.StartupError is not null)
                return ExperimentalRestart.Unverified;
            if (observed.Startup?.MasksUserOverride == true)
                return observed.Persistent is null ? ExperimentalRestart.None : ExperimentalRestart.Unverified;
            return observed.Startup?.UserOverride == observed.Persistent?.AsRuntime()
                ? ExperimentalRestart.None : ExperimentalRestart.Pending;
        }
        if (!pending.Windows.SameRelease(windows) || observed.PersistenceError is not null ||
            observed.Persistent != pending.Persistent)
            return ExperimentalRestart.Unverified;
        if (pending.Windows.BootTime == windows.BootTime) return ExperimentalRestart.Pending;
        return observed.StartupError is null && observed.Startup?.MasksUserOverride != true &&
            observed.Startup?.UserOverride == pending.Persistent?.AsRuntime()
            ? ExperimentalRestart.None : ExperimentalRestart.Unverified;
    }
}
