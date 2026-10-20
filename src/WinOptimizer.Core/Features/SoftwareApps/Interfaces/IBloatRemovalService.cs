using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.SoftwareApps.Enums;
using WinOptimizer.Core.Features.SoftwareApps.Models;

namespace WinOptimizer.Core.Features.SoftwareApps.Interfaces;

public interface IBloatRemovalService
{
    Task<RemovalOutcome> ExecuteDedicatedScriptAsync(ItemDefinition app,
        IProgress<TaskProgressDetail>? progress = null, CancellationToken ct = default);

    Task<RemovalOutcome> ExecuteBloatRemovalAsync(List<ItemDefinition> apps,
        IProgress<TaskProgressDetail>? progress = null, CancellationToken ct = default);

    Task PersistRemovalScriptsAsync(List<ItemDefinition> allApps);
    Task CleanupAllRemovalArtifactsAsync();

    Task<bool> RemoveItemsFromScriptAsync(List<ItemDefinition> itemsToRemove);
}
