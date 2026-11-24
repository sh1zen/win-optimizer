using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.SoftwareApps.Models;

namespace WinOptimizer.Core.Features.SoftwareApps.Interfaces;

public interface IBloatRemovalService
{
    Task<IReadOnlyList<UninstallNativeOutcome>> ExecuteBloatRemovalAsync(IReadOnlyList<ItemDefinition> apps,
        bool saveRemovalScript, IProgress<TaskProgressDetail>? progress = null, CancellationToken ct = default);
}
