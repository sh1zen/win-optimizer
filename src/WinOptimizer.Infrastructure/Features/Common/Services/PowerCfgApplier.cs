using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.ComponentModel;
using System.Threading.Tasks;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Helpers;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Common.Native;
using WinOptimizer.Infrastructure.Features.Common.Utilities;

namespace WinOptimizer.Infrastructure.Features.Common.Services;

public class PowerCfgApplier(
    IPowerSettingsQueryService powerSettingsQueryService,
    IHardwareDetectionService hardwareDetectionService,
    IComboBoxResolver comboBoxResolver,
    ILogService logService,
    IPowerSchemeOperations schemeOperations) : IPowerCfgApplier
{
    public async Task<OperationResult> ApplyPowerCfgSettingsAsync(SettingDefinition setting, bool enable, object? value)
    {
        if (setting.PowerCfgSettings == null || setting.PowerCfgSettings.Count == 0)
            return OperationResult.Succeeded();

        if (setting.InputType == InputType.Selection &&
            setting.PowerCfgSettings[0].PowerModeSupport == PowerModeSupport.Separate &&
            value is ValueTuple<int, int> tupleSeparate)
        {
            logService.Log(LogLevel.Info, $"[PowerCfgApplier] Applying PowerCfg settings for '{setting.Id}' with separate AC/DC Selection tuple");

            var acPowerCfgValue = comboBoxResolver.GetValueFromIndex(setting, tupleSeparate.Item1);
            var dcPowerCfgValue = comboBoxResolver.GetValueFromIndex(setting, tupleSeparate.Item2);

            var convertedDict = new Dictionary<string, object?>
            {
                ["ACValue"] = acPowerCfgValue,
                ["DCValue"] = dcPowerCfgValue
            };

            return await ExecutePowerCfgSettingsAsync(setting.PowerCfgSettings, convertedDict, await hardwareDetectionService.HasBatteryAsync().ConfigureAwait(false)).ConfigureAwait(false);
        }
        else if (setting.InputType == InputType.Selection &&
            setting.PowerCfgSettings[0].PowerModeSupport == PowerModeSupport.Separate &&
            value is Dictionary<string, object?> dict)
        {
            logService.Log(LogLevel.Info, $"[PowerCfgApplier] Applying PowerCfg settings for '{setting.Id}' with separate AC/DC Selection values");

            var acIndex = ExtractIndexFromValue(dict.TryGetValue("ACValue", out var acVal) ? acVal : 0);
            var dcIndex = ExtractIndexFromValue(dict.TryGetValue("DCValue", out var dcVal) ? dcVal : 0);

            var acPowerCfgValue = comboBoxResolver.GetValueFromIndex(setting, acIndex);
            var dcPowerCfgValue = comboBoxResolver.GetValueFromIndex(setting, dcIndex);

            var convertedDict = new Dictionary<string, object?>
            {
                ["ACValue"] = acPowerCfgValue,
                ["DCValue"] = dcPowerCfgValue
            };

            return await ExecutePowerCfgSettingsAsync(setting.PowerCfgSettings, convertedDict, await hardwareDetectionService.HasBatteryAsync().ConfigureAwait(false)).ConfigureAwait(false);
        }
        else if (setting.InputType == InputType.NumericRange &&
                 setting.PowerCfgSettings[0].PowerModeSupport == PowerModeSupport.Separate &&
                 value is Dictionary<string, object?> numericDict)
        {
            logService.Log(LogLevel.Info, $"[PowerCfgApplier] Applying PowerCfg settings for '{setting.Id}' with separate AC/DC NumericRange values");

            var acValue = numericDict.TryGetValue("ACValue", out var ac) ? ExtractSingleValue(ac) : 0;
            var dcValue = numericDict.TryGetValue("DCValue", out var dc) ? ExtractSingleValue(dc) : 0;

            var scale = PowerSettingUnits.SystemUnitsPerDisplayUnit(PowerSettingUnits.GetDisplayUnits(setting));

            var convertedDict = new Dictionary<string, object?>
            {
                ["ACValue"] = acValue * scale,
                ["DCValue"] = dcValue * scale
            };

            return await ExecutePowerCfgSettingsAsync(setting.PowerCfgSettings, convertedDict, await hardwareDetectionService.HasBatteryAsync().ConfigureAwait(false)).ConfigureAwait(false);
        }
        else
        {
            if (setting.InputType == InputType.NumericRange && value == null)
            {
                throw new ArgumentException($"A numeric value is required for power setting '{setting.Id}'.", nameof(value));
            }

            int valueToApply = setting.InputType switch
            {
                InputType.Toggle => enable ? 1 : 0,
                InputType.Selection when value is int index => comboBoxResolver.GetValueFromIndex(setting, index),
                InputType.NumericRange when value != null => NumericConversionHelper.ConvertNumericValue(value)
                    * PowerSettingUnits.SystemUnitsPerDisplayUnit(PowerSettingUnits.GetDisplayUnits(setting)),
                _ => throw new NotSupportedException($"Input type '{setting.InputType}' not supported for PowerCfg operations")
            };

            logService.Log(LogLevel.Info, $"[PowerCfgApplier] Applying {setting.PowerCfgSettings.Count} PowerCfg settings for '{setting.Id}' with value: {valueToApply}");
            return await ExecutePowerCfgSettingsAsync(setting.PowerCfgSettings, valueToApply, await hardwareDetectionService.HasBatteryAsync().ConfigureAwait(false)).ConfigureAwait(false);
        }
    }

    /// <summary>Applies and activates power settings, retaining precise errors from each native operation.</summary>
    private async Task<OperationResult> ExecutePowerCfgSettingsAsync(IReadOnlyList<PowerCfgSetting> powerCfgSettings, object valueToApply, bool hasBattery)
    {
        var status = schemeOperations.GetActiveScheme(out var scheme);
        if (status != PowerProf.ERROR_SUCCESS)
            return OperationResult.Failed(FormatFailure("get the active power scheme", status));

        int changeCount = 0;
        var failures = new List<string>();
        foreach (var setting in powerCfgSettings)
        {
            var (currentAc, currentDc) = await powerSettingsQueryService.GetPowerSettingACDCValuesAsync(setting).ConfigureAwait(false);
            var subgroup = Guid.Parse(setting.SubgroupGuid);
            var settingId = Guid.Parse(setting.SettingGuid);
            var (acValue, dcValue) = ExtractACDCValues(valueToApply);

            if (setting.PowerModeSupport != PowerModeSupport.DCOnly && currentAc != acValue)
                WriteValue(onAc: true, acValue);
            if (hasBattery && setting.PowerModeSupport != PowerModeSupport.ACOnly && currentDc != dcValue)
                WriteValue(onAc: false, dcValue);

            void WriteValue(bool onAc, int value)
            {
                var writeStatus = schemeOperations.WriteValue(scheme, subgroup, settingId, onAc, (uint)value);
                if (writeStatus == PowerProf.ERROR_SUCCESS)
                    changeCount++;
                else
                    failures.Add(FormatFailure($"write {(onAc ? "AC" : "DC")} value for {settingId}", writeStatus));
            }
        }

        if (changeCount > 0)
        {
            var activationStatus = schemeOperations.SetActiveScheme(scheme);
            powerSettingsQueryService.InvalidateCache();
            if (activationStatus != PowerProf.ERROR_SUCCESS)
                failures.Add(FormatFailure("activate the updated power scheme", activationStatus));
        }

        if (failures.Count > 0)
            return OperationResult.Failed(string.Join("; ", failures));

        logService.Log(LogLevel.Info, $"[PowerCfgApplier] Applied {changeCount} powercfg changes via P/Invoke");
        return OperationResult.Succeeded();
    }

    private static string FormatFailure(string operation, uint status) =>
        $"Could not {operation} (Windows error {status}: {new Win32Exception((int)status).Message}).";

    private int ExtractSingleValue(object? value)
    {
        return value switch
        {
            int intVal => intVal,
            long longVal => (int)longVal,
            double doubleVal => (int)doubleVal,
            float floatVal => (int)floatVal,
            string stringVal when int.TryParse(stringVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) => parsed,
            ValueTuple<int, int> tuple => tuple.Item1,
            System.Text.Json.JsonElement je when je.TryGetInt32(out int jsonInt) => jsonInt,
            _ => throw new ArgumentException($"Cannot convert '{value}' (type: {value?.GetType().Name ?? "null"}) to single numeric value")
        };
    }

    private (int acValue, int dcValue) ExtractACDCValues(object value)
    {
        if (value is ValueTuple<object, object> tuple)
        {
            return (ExtractSingleValue(tuple.Item1), ExtractSingleValue(tuple.Item2));
        }

        if (value is Dictionary<string, object?> dict)
        {
            var acValue = dict.TryGetValue("ACValue", out var ac) ? ExtractSingleValue(ac) : 0;
            var dcValue = dict.TryGetValue("DCValue", out var dc) ? ExtractSingleValue(dc) : 0;
            return (acValue, dcValue);
        }

        var singleValue = ExtractSingleValue(value);
        return (singleValue, singleValue);
    }

    private int ExtractIndexFromValue(object? value)
    {
        if (value == null) return 0;

        if (value.GetType().Name == "ComboBoxOption")
        {
            var valueProp = value.GetType().GetProperty("Value");
            if (valueProp != null)
            {
                var innerValue = valueProp.GetValue(value);
                if (innerValue is int intVal)
                    return intVal;
            }
        }

        if (value is int directInt)
            return directInt;

        if (value is System.Text.Json.JsonElement je)
            return je.TryGetInt32(out int jsonInt) ? jsonInt : 0;

        if (int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            return parsed;

        return 0;
    }
}
