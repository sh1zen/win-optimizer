using WinOptimizer.Core.Features.SystemInformation.Models;

namespace WinOptimizer.Core.Features.SystemInformation.Interfaces;

/// <summary>Retains the session's latest inventory and collects fresh local data on explicit request.</summary>
public interface ISystemInformationService
{
    /// <summary>The last completed collection in this app session, including category availability.</summary>
    SystemInformationSnapshot? CachedSnapshot { get; }

    /// <summary>Collects fresh data and replaces the cached snapshot only after collection completes.</summary>
    Task<SystemInformationSnapshot> ReadAsync(CancellationToken ct);
}
