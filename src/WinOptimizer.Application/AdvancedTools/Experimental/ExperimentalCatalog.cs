using System.Collections.Immutable;
using System.Runtime.InteropServices;
using WinOptimizer.Core.Features.AdvancedTools.Experimental;

namespace WinOptimizer.Application.AdvancedTools.Experimental;

/// <summary>Built-in evidence and selection of the feature bundle for the installed Windows release.</summary>
internal static class ExperimentalCatalog
{
    private static readonly ImmutableArray<ExperimentalBuild> Kb5132057X64 =
        [new(26200, 10075, [Architecture.X64]), new(26300, 10075, [Architecture.X64])];

    private static readonly ImmutableArray<Uri> Kb5132057Sources =
    [
        new("https://support.microsoft.com/en-us/servicing/os/windows-11/2026/11/kb5132057-windows-11-25h2-26h2-update"),
        new("https://www.ilsoftware.it/kb5132057-windows-11-attivare-nuove-funzioni-vivetool/"),
        new("https://pureinfotech.com/kb5132057-enable-features-windows-11-december-2026-update/")
    ];

    private static readonly FeatureOverride Enabled = new FeatureOverride(FeatureOverrideState.Enabled).AsRuntime();
    private static readonly FeatureOverride Disabled = new FeatureOverride(FeatureOverrideState.Disabled).AsRuntime();

    public static ImmutableArray<ExperimentalFeature> Features { get; } =
    [
        new("Search", "Experimental_Shell",
            [new(58381341, Enabled, Disabled), new(59210771, Enabled, Disabled), new(59210767, Enabled, Disabled)],
            Kb5132057X64, [ExperimentalPrerequisite.EnglishWindows], Kb5132057Sources, Verified: true),
        new("Run", "Experimental_Shell",
            [new(58381341, Enabled, null, Dependency: true), new(58527096, Enabled, Disabled),
             new(57156807, Enabled, Disabled), new(57259990, Enabled, Disabled)],
            Kb5132057X64, [], Kb5132057Sources, Verified: true),
        new("Pointer", "SettingGroup_Accessibility", [new(27829265, Enabled, Disabled)],
            Kb5132057X64, [], Kb5132057Sources, Verified: true)
    ];

    private static readonly Uri ReleasePreview =
        new("https://learn.microsoft.com/en-us/windows-insider/release-notes/release-preview-24h2-25h2/build-26200-10066-26300-10066");

    private static readonly ImmutableArray<ExperimentalBuildProfile> HistoricalProfiles =
    [
        PreviousKbRevision("Search", 26200), PreviousKbRevision("Search", 26300),
        PreviousKbRevision("Run", 26200), PreviousKbRevision("Run", 26300),
        PreviousKbRevision("Pointer", 26200), PreviousKbRevision("Pointer", 26300),
        new("Pointer", new(26220, 9606, [Architecture.X64]), Features[2].Requirements,
            [new("https://github.com/SunshineStream/Sunshine/issues/5293")])
    ];

    private static ExperimentalBuildProfile PreviousKbRevision(string key, int build) =>
        new(key, new(build, 10066, [Architecture.X64]), Features.Single(feature => feature.Key == key).Requirements,
            [ReleasePreview, Kb5132057Sources[2]]);

    private static bool Matches(ExperimentalBuild target, ExperimentalWindows windows) =>
        target.Build == windows.Build && target.Revision == windows.Revision && target.Architectures.Contains(windows.Architecture);

    public static ImmutableArray<ExperimentalFeature> Resolve(ExperimentalWindows windows, ExperimentalBuildProfile[]? imported)
    {
        var profiles = imported ?? [];
        ValidateProfiles(profiles);
        return Features.Select(feature =>
        {
            if (feature.Builds.Any(target => Matches(target, windows))) return feature;
            var profile = HistoricalProfiles.Concat(profiles)
                .FirstOrDefault(profile => profile.FeatureKey == feature.Key && Matches(profile.Target, windows));
            return profile is null ? feature : feature with
            {
                Requirements = profile.Requirements.Select(requirement => requirement with
                {
                    Enable = requirement.Enable.AsRuntime(),
                    Disable = requirement.Disable?.AsRuntime()
                }).ToImmutableArray(),
                Builds = [profile.Target],
                Sources = profile.Sources,
                Prerequisites = profile.Prerequisites ?? feature.Prerequisites,
                Imported = profiles.Contains(profile)
            };
        }).ToImmutableArray();
    }

    /// <summary>Rejects contradictory or invalid declarations before any profile can affect a write plan.</summary>
    public static void ValidateProfiles(ExperimentalBuildProfile[] profiles)
    {
        if (profiles.Length > 256) throw new InvalidDataException("At most 256 experimental build profiles are supported.");
        var declarations = new HashSet<(string Feature, int Build, int Revision, Architecture Architecture)>();
        foreach (var profile in profiles)
        {
            if (profile is null || !Features.Any(feature => feature.Key == profile.FeatureKey) ||
                profile.Target is not { Build: >= ExperimentalFeaturePolicy.MinimumBuild, Revision: >= 0 } target ||
                target.Architectures.IsDefaultOrEmpty || target.Architectures.Any(architecture => architecture is not (Architecture.X64 or Architecture.Arm64)) ||
                profile.Requirements.IsDefaultOrEmpty || profile.Requirements.Length > 16 ||
                !profile.Requirements.Any(requirement => requirement is { Dependency: false, Disable: not null }) ||
                profile.Prerequisites is { } prerequisites &&
                    (prerequisites.IsDefault || prerequisites.Any(prerequisite => !Enum.IsDefined(prerequisite))) ||
                profile.Sources.IsDefaultOrEmpty || profile.Sources.Length > 8 || profile.Sources.Any(source =>
                    source is null || !source.IsAbsoluteUri || source.Scheme != Uri.UriSchemeHttps || source.UserInfo.Length != 0))
                throw new InvalidDataException("A profile needs a catalog feature, an exact Windows target, valid requirements and HTTPS evidence.");

            var ids = new HashSet<uint>();
            foreach (var requirement in profile.Requirements)
                if (requirement is null || !ids.Add(requirement.Id) || requirement.Id == 0 ||
                    requirement.Enable is not { State: FeatureOverrideState.Enabled, IsValid: true } ||
                    requirement.Disable is not (null or { State: FeatureOverrideState.Disabled, IsValid: true }))
                    throw new InvalidDataException("Profile requirements need unique feature IDs and valid enable and disable overrides.");

            foreach (var architecture in target.Architectures)
            {
                bool MatchesTarget(ExperimentalBuild builtIn) => builtIn.Build == target.Build &&
                    builtIn.Revision == target.Revision && builtIn.Architectures.Contains(architecture);
                if (Features.Single(feature => feature.Key == profile.FeatureKey).Builds.Any(MatchesTarget) ||
                    HistoricalProfiles.Any(entry => entry.FeatureKey == profile.FeatureKey && MatchesTarget(entry.Target)))
                    throw new InvalidDataException("Imported profiles cannot replace a built-in compatibility mapping.");
                if (!declarations.Add((profile.FeatureKey, target.Build, target.Revision, architecture)))
                    throw new InvalidDataException("Multiple profiles match the same feature, build, revision and architecture.");
            }
        }
    }
}
