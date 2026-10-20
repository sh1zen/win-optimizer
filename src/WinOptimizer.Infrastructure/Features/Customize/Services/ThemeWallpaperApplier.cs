using System.Collections.Generic;
using System.Threading.Tasks;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Customize.Interfaces;
using WinOptimizer.Core.Features.Customize.Models;

namespace WinOptimizer.Infrastructure.Features.Customize.Services;

/// <summary>
/// Applies wallpaper and registry values for the Windows theme switch.
/// ProcessRestartManager refreshes Explorer through the setting's RestartProcess metadata.
/// </summary>
public sealed class ThemeWallpaperApplier(
    IWallpaperService wallpaperService,
    IWindowsVersionService versionService,
    IWindowsRegistryService registryService,
    ILogService logService,
    IFileSystemService fileSystemService) : ISpecialSettingHandler
{
    public async Task<bool> TryApplySpecialSettingAsync(
        SettingDefinition setting,
        object value,
        bool additionalContext = false,
        ISettingApplicationService? settingApplicationService = null)
    {
        if (setting.Id != SettingIds.ThemeModeWindows) return false;
        if (value is not int selectionIndex) return false;

        logService.Log(LogLevel.Info,
            $"[ThemeWallpaperApplier] Applying theme mode - Index: {selectionIndex}, ApplyWallpaper: {additionalContext}");

        // Light = 0, Dark = 1. The registry values flip: light writes 1, dark writes 0.
        int themeValue = selectionIndex == 1 ? 0 : 1;
        if (setting.RegistrySettings != null)
        {
            foreach (var registrySetting in setting.RegistrySettings)
                registryService.ApplySetting(registrySetting, true, themeValue);
        }

        // Import-flow checkbox: also change the wallpaper to match.
        if (additionalContext)
        {
            try
            {
                var isDarkMode = selectionIndex == 1;
                var isWindows11 = versionService.IsWindows11();
                var wallpaperPath = WindowsThemeCustomizations.Wallpaper.GetDefaultWallpaperPath(isWindows11, isDarkMode);

                if (fileSystemService.FileExists(wallpaperPath))
                {
                    await wallpaperService.SetWallpaperAsync(wallpaperPath).ConfigureAwait(false);
                    logService.Log(LogLevel.Info, $"[ThemeWallpaperApplier] Wallpaper changed to: {wallpaperPath}");
                }
            }
            catch (System.Exception ex)
            {
                logService.Log(LogLevel.Warning, $"[ThemeWallpaperApplier] Failed to change wallpaper: {ex.Message}");
            }
        }

        return true;
    }
}
