using System.Threading.Tasks;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Core.Features.Common.Interfaces;

public interface IConfigReviewOrchestrationService
{
    Task EnterReviewModeAsync(UnifiedConfigurationFile config, bool isWindowsDefaults = false);
    Task ApplyReviewedConfigAsync();
    Task CancelReviewModeAsync();
}
