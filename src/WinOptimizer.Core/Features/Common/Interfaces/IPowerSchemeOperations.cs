using System;

namespace WinOptimizer.Core.Features.Common.Interfaces;

/// <summary>
/// Wraps PowrProf scheme operations and returns the native status for each change.
/// </summary>
public interface IPowerSchemeOperations
{
    /// <summary>Gets the active power scheme's identifier.</summary>
    uint GetActiveScheme(out Guid schemeGuid);

    /// <summary>Writes one AC or DC setting index for the specified power scheme.</summary>
    uint WriteValue(Guid schemeGuid, Guid subgroup, Guid setting, bool onAc, uint value);

    /// <summary>
    /// Deletes a power scheme by GUID. Safe to call on non-existent schemes
    /// (returns an error code but does not throw).
    /// </summary>
    uint DeleteScheme(Guid schemeGuid);

    /// <summary>
    /// Duplicates a power scheme, returning a new GUID assigned by Windows.
    /// </summary>
    uint DuplicateScheme(Guid sourceGuid, out Guid destinationGuid);

    /// <summary>
    /// Sets the active power scheme.
    /// </summary>
    uint SetActiveScheme(Guid schemeGuid);

    /// <summary>
    /// Writes the friendly name for a power scheme.
    /// </summary>
    uint WriteFriendlyName(Guid schemeGuid, string name);

    /// <summary>
    /// Writes the description for a power scheme.
    /// </summary>
    uint WriteDescription(Guid schemeGuid, string description);
}
