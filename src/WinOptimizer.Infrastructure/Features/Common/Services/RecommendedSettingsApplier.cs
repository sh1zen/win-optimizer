using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Infrastructure.Features.Common.Helpers; // RecommendedSettingsResolver

namespace WinOptimizer.Infrastructure.Features.Common.Services;

public class RecommendedSettingsApplier(
    ICompatibleSettingsRegistry compatibleSettingsRegistry,
    IWindowsVersionService versionService,
    IProcessRestartManager processRestartManager,
    ILogService logService) : IRecommendedSettingsApplier
{
    public async Task<IReadOnlyList<SettingDefinition>> ApplyRecommendedToSettingsAsync(
        IReadOnlyList<SettingDefinition> settings,
        ISettingApplicationService apply,
        IProgress<TaskProgressDetail>? progress = null)
    {
        var appliedForRestart = new List<SettingDefinition>(settings.Count);
        int total = settings.Count;

        // Suppress per-setting restarts; the CALLER flushes the coalesced restart.
        using (processRestartManager.SuppressRestarts())
        {
            for (int i = 0; i < total; i++)
            {
                var setting = settings[i];
                try
                {
                    progress?.Report(new TaskProgressDetail
                    {
                        Progress = (double)i / total * 100,
                        StatusText = $"Applying recommended: {setting.Name}",
                        QueueCurrent = i + 1,
                        QueueTotal = total,
                        IsActive = true
                    });

                    var enable = true;
                    object? value = null;
                    if (setting.InputType == InputType.Toggle)
                    {
                        var toggleState = SettingDefinitionToggleState.GetRecommendedToggleState(setting);
                        if (toggleState is not bool enableValue) continue;
                        enable = enableValue;
                    }
                    else if (setting.InputType == InputType.Selection)
                    {
                        value = RecommendedSettingsResolver.BuildPowerCfgApplyValue(setting)
                            ?? RecommendedSettingsResolver.GetRecommendedIndex(setting);
                        if (value == null) continue;
                    }
                    else
                    {
                        value = RecommendedSettingsResolver.GetRecommendedValueForSetting(setting)
                            ?? RecommendedSettingsResolver.BuildPowerCfgApplyValue(setting);
                        if (value == null) continue;
                    }

                    var result = await apply.ApplySettingAsync(new ApplySettingRequest
                    {
                        SettingId = setting.Id, Enable = enable, Value = value, SkipValuePrerequisites = true
                    }).ConfigureAwait(false);
                    if (!result.Success)
                    {
                        logService.Log(LogLevel.Warning, $"[RecommendedSettingsApplier] Failed to apply recommended for '{setting.Id}': {result.ErrorMessage}");
                        continue;
                    }

                    appliedForRestart.Add(setting);
                    logService.Log(LogLevel.Debug, $"[RecommendedSettingsApplier] Applied recommended for '{setting.Id}'");
                }
                catch (Exception ex)
                {
                    logService.Log(LogLevel.Warning, $"[RecommendedSettingsApplier] Failed to apply recommended for '{setting.Id}': {ex.Message}");
                }
            }
        }

        return appliedForRestart;
    }

    public async Task<IReadOnlyList<SettingDefinition>> ApplyRecommendedForFeatureAsync(
        string triggerSettingId, ISettingApplicationService apply)
    {
        var featureId = compatibleSettingsRegistry.GetFeatureIdForSetting(triggerSettingId)
            ?? throw new InvalidOperationException($"Setting '{triggerSettingId}' has no feature mapping");

        var osInfo = RecommendedSettingsResolver.BuildOSInfo(versionService);
        var settings = compatibleSettingsRegistry.GetFilteredSettings(featureId)
            .Where(s => s.Id != triggerSettingId && RecommendedSettingsResolver.IsCompatibleWithCurrentOS(s, osInfo))
            .ToList();

        logService.Log(LogLevel.Info, $"[RecommendedSettingsApplier] Applying recommended for feature '{featureId}' ({settings.Count} candidate settings)");
        return await ApplyRecommendedToSettingsAsync(settings, apply, null).ConfigureAwait(false);
    }

    public async Task ApplyRecommendedSettingsForFeatureAsync(string settingId, ISettingApplicationService apply)
    {
        var applied = await ApplyRecommendedForFeatureAsync(settingId, apply).ConfigureAwait(false);
        await processRestartManager.FlushCoalescedRestartsAsync(applied).ConfigureAwait(false);
    }
}
