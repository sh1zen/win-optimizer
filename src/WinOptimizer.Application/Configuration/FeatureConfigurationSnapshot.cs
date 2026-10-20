using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Application.Configuration;

/// <summary>Collects discovered feature state for configuration and answer-file exports.</summary>
public static class FeatureConfigurationSnapshot
{
    /// <summary>Discovers each recognized feature once and groups items using the caller's export format.</summary>
    public static async Task PopulateAsync(
        UnifiedConfigurationFile config,
        ICompatibleSettingsRegistry registry,
        ISystemSettingsDiscoveryService discovery,
        ILogService log,
        Func<SettingDefinition, SettingStateResult?, ConfigurationItem> project)
    {
        var allSettingsByFeature = registry.GetAllFilteredSettings();
        int totalOptimizeSettings = 0;
        int totalCustomizeSettings = 0;
        var optimizeFeatures = new Dictionary<string, ConfigSection>();
        var customizeFeatures = new Dictionary<string, ConfigSection>();

        foreach (var (featureId, definitions) in allSettingsByFeature)
        {
            var settings = definitions.ToList();
            if (settings.Count == 0) continue;

            var isOptimize = FeatureDefinitions.OptimizeFeatures.Contains(featureId);
            var isCustomize = FeatureDefinitions.CustomizeFeatures.Contains(featureId);
            if (!isOptimize && !isCustomize)
            {
                log.Log(LogLevel.Warning, $"Feature {featureId} is neither Optimize nor Customize, skipping");
                continue;
            }

            var states = await discovery.GetSettingStatesAsync(settings);
            var items = settings.Select(setting => project(setting, states.GetValueOrDefault(setting.Id))).ToList();
            var section = new ConfigSection { IsIncluded = true, Items = items };
            if (isOptimize)
            {
                optimizeFeatures[featureId] = section;
                config.Optimize.IsIncluded = true;
                totalOptimizeSettings += items.Count;
                log.Log(LogLevel.Info, $"Exported {items.Count} settings from {featureId} (Optimize)");
            }
            else
            {
                customizeFeatures[featureId] = section;
                config.Customize.IsIncluded = true;
                totalCustomizeSettings += items.Count;
                log.Log(LogLevel.Info, $"Exported {items.Count} settings from {featureId} (Customize)");
            }
        }

        config.Optimize.Features = optimizeFeatures;
        config.Customize.Features = customizeFeatures;
        log.Log(LogLevel.Info, $"Total exported: {totalOptimizeSettings} Optimize settings, {totalCustomizeSettings} Customize settings");
    }
}
