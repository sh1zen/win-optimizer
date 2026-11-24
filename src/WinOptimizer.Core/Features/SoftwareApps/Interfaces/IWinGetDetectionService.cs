using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WinOptimizer.Core.Features.SoftwareApps.Interfaces;

/// <summary>
/// Enumerates installed WinGet packages through COM with a CLI fallback.
/// </summary>
public interface IWinGetDetectionService
{
    Task<HashSet<string>> GetInstalledPackageIdsAsync(CancellationToken cancellationToken = default);
}
