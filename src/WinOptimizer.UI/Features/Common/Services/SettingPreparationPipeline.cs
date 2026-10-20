using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.Common.Interfaces;

namespace WinOptimizer.UI.Features.Common.Services;

/// <summary>
/// Prepares setting definitions by filtering for compatibility and applying localization.
/// </summary>
public class SettingPreparationPipeline : ISettingPreparationPipeline
{
    private readonly ICompatibleSettingsRegistry _compatibleSettingsRegistry;
    private readonly ISettingLocalizationService _settingLocalizationService;

    public SettingPreparationPipeline(
        ICompatibleSettingsRegistry compatibleSettingsRegistry,
        ISettingLocalizationService settingLocalizationService)
    {
        _compatibleSettingsRegistry = compatibleSettingsRegistry;
        _settingLocalizationService = settingLocalizationService;
    }

    /// <inheritdoc />
    public IReadOnlyList<SettingDefinition> PrepareSettings(string featureModuleId)
    {
        var settingDefinitions = _compatibleSettingsRegistry.GetFilteredSettings(featureModuleId);
        return settingDefinitions
            .Select(s => _settingLocalizationService.LocalizeSetting(s))
            .ToList();
    }
}
