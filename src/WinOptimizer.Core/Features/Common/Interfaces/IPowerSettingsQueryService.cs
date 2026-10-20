using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Optimize.Models;

namespace WinOptimizer.Core.Features.Common.Interfaces;

public interface IPowerSettingsQueryService
{
    Task<List<PowerPlan>> GetAvailablePowerPlansAsync();
    Task<PowerPlan> GetActivePowerPlanAsync();
    Task<(int? acValue, int? dcValue)> GetPowerSettingACDCValuesAsync(PowerCfgSetting powerCfgSetting);
    Task<Dictionary<string, (int? acValue, int? dcValue)>> GetAllPowerSettingsACDCAsync(string powerPlanGuid = "SCHEME_CURRENT");
    Task<bool> IsSettingHardwareControlledAsync(PowerCfgSetting powerCfgSetting);
    void InvalidateCache();
}