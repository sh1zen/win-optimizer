using System;
using System.Threading;
using System.Threading.Tasks;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.SoftwareApps.Models;

namespace WinOptimizer.Core.Features.SoftwareApps.Interfaces;

public interface IExternalAppUninstallService
{
    /// <summary>Runs the registered Windows command, or returns null when no unambiguous command exists.</summary>
    Task<OperationResult<bool>?> TryUninstallRegisteredAsync(ItemDefinition item, CancellationToken cancellationToken);

    Task<OperationResult<bool>> UninstallAsync(
        ItemDefinition item,
        IProgress<TaskProgressDetail>? progress = null,
        CancellationToken cancellationToken = default);
}

public enum UninstallMethod
{
    None,
    WinGet,
    Chocolatey,
    Registry,
    FileSystem,
    AppX
}
