using System.Threading;
using System.Threading.Tasks;

namespace WinOptimizer.Core.Features.SoftwareApps.Interfaces;

/// <summary>
/// Checks WinGet availability and removes packages through the CLI.
/// </summary>
public interface IWinGetPackageInstaller
{
    Task<bool> UninstallPackageAsync(string packageId, string? source = null, string? displayName = null, CancellationToken cancellationToken = default);
    Task<bool> IsWinGetInstalledAsync(CancellationToken cancellationToken = default);
}
