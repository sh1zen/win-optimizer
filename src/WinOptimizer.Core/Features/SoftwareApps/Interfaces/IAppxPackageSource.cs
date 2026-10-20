using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WinOptimizer.Core.Features.SoftwareApps.Interfaces;

/// <summary>
/// Enumerates installed AppX packages (PackageManager COM → WMI → PowerShell fallback).
/// </summary>
public interface IAppxPackageSource
{
    Task<HashSet<string>> GetInstalledPackageNamesAsync(CancellationToken cancellationToken = default);
    /// <summary>Reads packages retained on the device, including packages without a current-user registration.</summary>
    Task<HashSet<string>> GetDevicePackageNamesAsync(CancellationToken cancellationToken = default);
    /// <summary>Reads registrations for an explicit Windows account without substituting another account's inventory.</summary>
    Task<HashSet<string>> GetInstalledPackageNamesForUserAsync(string accountSid, CancellationToken cancellationToken = default);
}
