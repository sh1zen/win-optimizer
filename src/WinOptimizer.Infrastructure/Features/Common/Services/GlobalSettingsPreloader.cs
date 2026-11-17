using System;
using System.Linq;
using System.Threading.Tasks;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Infrastructure.Features.Common.Services;

public class GlobalSettingsPreloader : IGlobalSettingsPreloader
{
    private readonly ICompatibleSettingsRegistry _compatibleSettingsRegistry;
    private readonly IGlobalSettingsRegistry _globalSettingsRegistry;
    private readonly ILogService _logService;
    private bool _isPreloaded;

    public bool IsPreloaded => _isPreloaded;

    public GlobalSettingsPreloader(
        ICompatibleSettingsRegistry compatibleSettingsRegistry,
        IGlobalSettingsRegistry globalSettingsRegistry,
        ILogService logService)
    {
        _compatibleSettingsRegistry = compatibleSettingsRegistry;
        _globalSettingsRegistry = globalSettingsRegistry;
        _logService = logService;
    }

    public async Task PreloadAllSettingsAsync()
    {
        if (_isPreloaded)
        {
            _logService.Log(LogLevel.Debug, "[Preloader] Settings already preloaded, skipping");
            return;
        }

        _logService.Log(LogLevel.Info, "[Preloader] Starting global settings preload");

        var allBypassedSettings = _compatibleSettingsRegistry.GetAllBypassedSettings();

        foreach (var (featureId, settings) in allBypassedSettings)
        {
            try
            {
                // The compatibility registry already publishes materialized snapshots.
                var snapshot = settings as IReadOnlyCollection<SettingDefinition> ?? settings.ToArray();

                _globalSettingsRegistry.RegisterSettings(featureId, snapshot);

                _logService.Log(LogLevel.Debug, $"[Preloader] Registered {snapshot.Count} bypassed settings from {featureId}");
            }
            catch (Exception ex)
            {
                _logService.Log(LogLevel.Warning, $"[Preloader] Failed to preload settings from {featureId}: {ex.Message}");
            }
        }

        _isPreloaded = true;
        _logService.Log(LogLevel.Info, "[Preloader] Global settings preload completed");
    }
}
