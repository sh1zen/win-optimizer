namespace WinOptimizer.Core.Features.AdvancedTools.Experimental;

/// <summary>Native feature configuration boundary. Writes are restricted to User priority.</summary>
public interface IExperimentalFeatureBackend
{
    ExperimentalWindows GetWindows();
    ExperimentalObservation Read(uint id);

    /// <summary>Enumerates Windows-reported flag configurations in each read-only view.</summary>
    ExperimentalFlagInventory ReadFlagInventory(CancellationToken cancellationToken);

    /// <summary>Compares the current state with the supplied snapshot and verifies the requested write.</summary>
    void Write(ExperimentalWindows windows, ExperimentalObservation before, ExperimentalStore store,
        FeatureOverride? value);
}

/// <summary>Protected machine-wide recovery data for machine-wide feature overrides.</summary>
public interface IExperimentalRecoveryStore
{
    /// <summary>Excludes writers from other instances for the lifetime of a change.</summary>
    IDisposable ReserveWrite();
    ExperimentalRecovery Read();
    void Save(ExperimentalRecovery state);
}

/// <summary>Reads the typed contents of a user-selected compatibility profile file.</summary>
public interface IExperimentalProfileFile
{
    ExperimentalBuildProfile[] Read(string path);
}

/// <summary>Discovery and explicit experimental changes, independent of presets and config import.</summary>
public interface IExperimentalFeatures
{
    Task<ExperimentalDiscovery> DiscoverAsync(CancellationToken cancellationToken = default);
    Task<ExperimentalChangeResult> ChangeAsync(string featureKey, ExperimentalAction action,
        CancellationToken cancellationToken = default);
    Task<int> ImportProfilesAsync(string path, CancellationToken cancellationToken = default);
}
