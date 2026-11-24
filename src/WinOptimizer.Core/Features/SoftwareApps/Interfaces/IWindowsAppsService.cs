using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WinOptimizer.Core.Features.SoftwareApps.Models;

namespace WinOptimizer.Core.Features.SoftwareApps.Interfaces;

public interface IWindowsAppsService
{
    string DomainName { get; }
    Task<IEnumerable<ItemDefinition>> GetAppsAsync();
    void InvalidateStatusCache();
    event EventHandler? WinGetReady;

    /// <summary>Returns observed installation states. An omitted component has no confirmed status.</summary>
    Task<Dictionary<string, bool>> CheckBatchInstalledAsync(IEnumerable<ItemDefinition> definitions);
    /// <summary>Reads installed AppX identities across the device for the optional inactive-app view.</summary>
    Task<HashSet<string>> GetDevicePackageNamesAsync();
}
