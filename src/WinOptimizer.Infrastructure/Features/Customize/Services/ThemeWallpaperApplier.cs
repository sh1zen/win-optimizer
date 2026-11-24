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
        if (value is not int selectionIndex || selectionIndex is < 0 or > 1) return false;

        logService.Log(LogLevel.Info,
            $"[ThemeWallpaperApplier] Applying theme mode - Index: {selectionIndex}, ApplyWallpaper: {additionalContext}");

        // Light = 0, Dark = 1. The registry values flip: light writes 1, dark writes 0.
        int themeValue = selectionIndex == 1 ? 0 : 1;
        foreach (var registrySetting in setting.RegistrySettings)
        {
            if (!registryService.ApplySetting(registrySetting, true, themeValue))
                throw new IOException($"Windows could not write theme value '{registrySetting.ValueName}'.");
        }

        if (additionalContext) await ApplyDefaultWallpaperAsync(selectionIndex == 1).ConfigureAwait(false);

        return true;
    }

    /// <summary>Changes the wallpaper using the current Windows theme without writing any theme values.</summary>
    public Task ApplyDefaultForCurrentThemeAsync()
    {
        var theme = registryService.GetValue(WindowsThemeCustomizations.ThemeRegistryPath, WindowsThemeCustomizations.SystemThemeValueName);
        return ApplyDefaultWallpaperAsync(theme is 0);
    }

    private async Task ApplyDefaultWallpaperAsync(bool isDarkMode)
    {
        var path = WindowsThemeCustomizations.Wallpaper.GetDefaultWallpaperPath(versionService.IsWindows11(), isDarkMode);
        if (!fileSystemService.FileExists(path))
            throw new FileNotFoundException($"The default wallpaper for the Windows theme is missing: {path}");
        if (!await wallpaperService.SetWallpaperAsync(path).ConfigureAwait(false))
            throw new IOException($"Windows could not apply the default wallpaper: {path}");
        logService.Log(LogLevel.Info, $"[ThemeWallpaperApplier] Wallpaper changed to: {path}");
    }
}
