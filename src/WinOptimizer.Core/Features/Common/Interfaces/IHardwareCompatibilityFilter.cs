using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Core.Features.Common.Interfaces;

public interface IHardwareCompatibilityFilter
{
    Task<IEnumerable<SettingDefinition>> FilterSettingsByHardwareAsync(IEnumerable<SettingDefinition> settings);
}