using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WinOptimizer.Core.Features.SoftwareApps.Models;

namespace WinOptimizer.Core.Features.SoftwareApps.Interfaces;

/// <summary>
/// Enumerates installed AppX packages (PackageManager COM → WMI → PowerShell fallback).
/// </summary>
public interface IAppxPackageSource
{
    Task<HashSet<string>> GetInstalledPackageNamesAsync(CancellationToken cancellationToken = default);
    /// <summary>Captures fresh registrations for consumers that also need metadata for the same account.</summary>
    async Task<AppxInventorySnapshot> ReadInstalledInventoryAsync(CancellationToken cancellationToken = default) =>
        new(await GetInstalledPackageNamesAsync(cancellationToken).ConfigureAwait(false));
    /// <summary>Reads packages retained on the device, including packages without a current-user registration.</summary>
    Task<HashSet<string>> GetDevicePackageNamesAsync(CancellationToken cancellationToken = default);
    /// <summary>Reads registrations for an explicit Windows account without substituting another account's inventory.</summary>
    Task<HashSet<string>> GetInstalledPackageNamesForUserAsync(string accountSid, CancellationToken cancellationToken = default);
}
