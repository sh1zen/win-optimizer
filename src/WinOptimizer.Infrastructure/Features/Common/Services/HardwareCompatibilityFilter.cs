using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Infrastructure.Features.Common.Services;

public class HardwareCompatibilityFilter : IHardwareCompatibilityFilter
{
    private readonly IHardwareDetectionService _hardwareDetectionService;
    private readonly ILogService _logService;
    
    private bool? _hasBattery;
    private bool? _hasLid;
    private bool? _supportsBrightness;
    private bool? _supportsHybridSleep;

    public HardwareCompatibilityFilter(IHardwareDetectionService hardwareDetectionService, ILogService logService)
    {
        _hardwareDetectionService = hardwareDetectionService ?? throw new ArgumentNullException(nameof(hardwareDetectionService));
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
    }

    public async Task<IEnumerable<SettingDefinition>> FilterSettingsByHardwareAsync(
        IEnumerable<SettingDefinition> settings)
    {
        var settingsList = settings.ToList();
        var originalCount = settingsList.Count;

        // These probes have independent results and retain their own cached
        // values and error handling. Start them together to overlap Windows I/O.
        var batteryTask = GetHasBatteryAsync();
        var lidTask = GetHasLidAsync();
        var brightnessTask = GetSupportsBrightnessAsync();
        var hybridSleepTask = GetSupportsHybridSleepAsync();
        await Task.WhenAll(batteryTask, lidTask, brightnessTask, hybridSleepTask).ConfigureAwait(false);

        var hasBattery = await batteryTask.ConfigureAwait(false);
        var hasLid = await lidTask.ConfigureAwait(false);
        var supportsBrightness = await brightnessTask.ConfigureAwait(false);
        var supportsHybridSleep = await hybridSleepTask.ConfigureAwait(false);

        var filteredSettings = settingsList.Where(setting =>
        {
            if (setting.RequiresBattery && !hasBattery)
            {
                return false;
            }

            if (setting.RequiresLid && !hasLid)
            {
                return false;
            }

            if (setting.RequiresDesktop && (hasBattery || hasLid))
            {
                return false;
            }

            if (setting.RequiresBrightnessSupport && !supportsBrightness)
            {
                return false;
            }

            if (setting.RequiresHybridSleepCapable && !supportsHybridSleep)
            {
                return false;
            }

            return true;
        }).ToList();

        var filteredCount = originalCount - filteredSettings.Count;
        if (filteredCount > 0)
        {
            _logService.Log(LogLevel.Info, $"Filtered out {filteredCount} hardware-incompatible settings");
        }

        return filteredSettings;
    }

    private async Task<bool> GetHasBatteryAsync()
    {
        if (!_hasBattery.HasValue)
        {
            try
            {
                _hasBattery = await _hardwareDetectionService.HasBatteryAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logService.Log(LogLevel.Warning, $"Failed to detect battery presence: {ex.Message}");
                _hasBattery = false;
            }
        }
        return _hasBattery.Value;
    }

    private async Task<bool> GetHasLidAsync()
    {
        if (!_hasLid.HasValue)
        {
            try
            {
                _hasLid = await _hardwareDetectionService.HasLidAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logService.Log(LogLevel.Warning, $"Failed to detect lid presence: {ex.Message}");
                _hasLid = false;
            }
        }
        return _hasLid.Value;
    }

    private async Task<bool> GetSupportsBrightnessAsync()
    {
        if (!_supportsBrightness.HasValue)
        {
            try
            {
                _supportsBrightness = await _hardwareDetectionService.SupportsBrightnessControlAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logService.Log(LogLevel.Warning, $"Failed to detect brightness support: {ex.Message}");
                _supportsBrightness = false;
            }
        }
        return _supportsBrightness.Value;
    }

    private async Task<bool> GetSupportsHybridSleepAsync()
    {
        if (!_supportsHybridSleep.HasValue)
        {
            try
            {
                _supportsHybridSleep = await _hardwareDetectionService.SupportsHybridSleepAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logService.Log(LogLevel.Warning, $"Failed to detect hybrid sleep support: {ex.Message}");
                _supportsHybridSleep = false;
            }
        }
        return _supportsHybridSleep.Value;
    }
}
