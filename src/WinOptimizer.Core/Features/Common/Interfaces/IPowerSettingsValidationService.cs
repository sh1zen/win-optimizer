using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Core.Features.Common.Interfaces;

public interface IPowerSettingsValidationService
{
    Task<IEnumerable<SettingDefinition>> FilterSettingsByExistenceAsync(IEnumerable<SettingDefinition> settings);
}