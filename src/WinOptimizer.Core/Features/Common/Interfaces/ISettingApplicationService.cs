using System.Threading.Tasks;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Core.Features.Common.Interfaces;

public interface ISettingApplicationService
{
    Task<OperationResult> ApplySettingAsync(ApplySettingRequest request);
    Task ApplyRecommendedSettingsForFeatureAsync(string settingId);
}
