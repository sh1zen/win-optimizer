using System.Threading.Tasks;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Core.Features.Common.Interfaces;

public interface IWindowsUIManagementService
{
    bool IsProcessRunning(string processName);
    void KillProcess(string processName);
    Task<OperationResult> RefreshWindowsGUI(bool killExplorer = true);
    void BroadcastRegionalSettingChange();
}
