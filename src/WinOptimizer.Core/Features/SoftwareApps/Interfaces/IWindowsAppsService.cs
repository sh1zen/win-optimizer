using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.SoftwareApps.Models;

namespace WinOptimizer.Core.Features.SoftwareApps.Interfaces;

public interface IWindowsAppsService
{
    string DomainName { get; }
    Task<IEnumerable<ItemDefinition>> GetAppsAsync();
    void InvalidateStatusCache();
    event EventHandler? WinGetReady;

    Task<ItemDefinition?> GetAppByIdAsync(string appId);
    Task<Dictionary<string, bool>> CheckBatchInstalledAsync(IEnumerable<ItemDefinition> definitions);
    /// <summary>Reads installed AppX identities across the device for the optional inactive-app view.</summary>
    Task<HashSet<string>> GetDevicePackageNamesAsync();
    Task<OperationResult<bool>> InstallAppAsync(ItemDefinition item, IProgress<TaskProgressDetail>? progress = null);
}
