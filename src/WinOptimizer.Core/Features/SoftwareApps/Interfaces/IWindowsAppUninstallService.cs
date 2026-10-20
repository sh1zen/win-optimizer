using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.SoftwareApps.Models;

namespace WinOptimizer.Core.Features.SoftwareApps.Interfaces;

public interface IWindowsAppUninstallService
{
    Task<OperationResult<bool>> UninstallAppAsync(string appId, IProgress<TaskProgressDetail>? progress = null);
    Task<OperationResult<int>> UninstallAppsAsync(List<ItemDefinition> apps, IProgress<TaskProgressDetail>? progress = null, bool saveRemovalScripts = true);
    Task<OperationResult<int>> UninstallAppsInParallelAsync(List<ItemDefinition> apps, bool saveRemovalScripts = true);
    /// <summary>Reports each selected application's native success, failure or scheduled removal.</summary>
    Task<OperationResult<IReadOnlyList<UninstallNativeOutcome>>> UninstallAppsWithOutcomesAsync(List<ItemDefinition> apps, bool saveRemovalScripts = true);
}
