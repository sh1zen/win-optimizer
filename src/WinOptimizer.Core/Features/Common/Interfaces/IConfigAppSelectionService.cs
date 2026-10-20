using System.Collections.Generic;
using System.Threading.Tasks;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Core.Features.Common.Interfaces;

public interface IConfigAppSelectionService
{
    Task SelectWindowsAppsFromConfigAsync(ConfigSection windowsAppsSection);
    Task<(bool shouldContinue, bool saveScripts)> ConfirmWindowsAppsRemovalAsync();
    Task ClearWindowsAppsSelectionAsync();
    Task SelectExternalAppsFromConfigAsync(ConfigSection externalAppsSection);
    Task ProcessExternalAppsRemovalAsync(ConfigSection externalAppsSection);
    Task RemoveSelectedExternalAppsAsync(List<string> selectedAppIds);
}
