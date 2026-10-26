using System.Collections.Generic;
using System.Threading.Tasks;
using WinOptimizer.Core.Features.SoftwareApps.Models;

namespace WinOptimizer.Core.Features.SoftwareApps.Interfaces;

public interface IAppStatusDiscoveryService
{
    Task<Dictionary<string, bool>> GetInstallationStatusBatchAsync(IEnumerable<ItemDefinition> definitions);
    Task<Dictionary<string, bool>> GetExternalAppsInstallationStatusAsync(IEnumerable<ItemDefinition> definitions);
    /// <summary>Discards shared WinGet and account AppX evidence for the next provider check.</summary>
    void InvalidateCache();
}
