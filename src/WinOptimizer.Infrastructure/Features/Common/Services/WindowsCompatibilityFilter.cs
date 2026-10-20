using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Common.Helpers;

namespace WinOptimizer.Infrastructure.Features.Common.Services;

public class WindowsCompatibilityFilter : IWindowsCompatibilityFilter
{
    private readonly IWindowsVersionService _versionService;
    private readonly ILogService _logService;
    private readonly ConcurrentDictionary<string, byte> _loggedCompatibilityMessages = new();

    public WindowsCompatibilityFilter(
        IWindowsVersionService versionService,
        ILogService logService)
    {
        _versionService = versionService ?? throw new ArgumentNullException(nameof(versionService));
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
    }

    public virtual IEnumerable<SettingDefinition> FilterSettingsByWindowsVersion(
        IEnumerable<SettingDefinition> settings)
    {
        return FilterSettingsByWindowsVersion(settings, applyFilter: true);
    }

    public virtual IEnumerable<SettingDefinition> FilterSettingsByWindowsVersion(
        IEnumerable<SettingDefinition> settings,
        bool applyFilter)
    {
        if (!applyFilter)
        {
            return DecorateSettingsWithCompatibilityMessages(settings);
        }

        try
        {
            var isWindows11 = _versionService.IsWindows11();
            var buildNumber = _versionService.GetWindowsBuildNumber();
            var buildRevision = _versionService.GetWindowsBuildRevision();
            var isServer = _versionService.IsWindowsServer();

            if (isServer)
            {
                _logService.Log(LogLevel.Info,
                    $"Windows Server detected (build {buildNumber}). Treating as Windows {(isWindows11 ? "11" : "10")} for compatibility filtering.");
            }

            _logService.Log(LogLevel.Debug,
                $"Filtering settings for Windows {(isWindows11 ? "11" : "10")}{(isServer ? " Server" : "")} build {buildNumber}");

            var compatibleSettings = new List<SettingDefinition>();
            var filteredCount = 0;

            foreach (var setting in settings)
            {
                var incompatibility = BuildVersionGate.GetCompatibilityMessage(
                    setting, isWindows11, buildNumber, buildRevision);

                if (incompatibility == null)
                {
                    compatibleSettings.Add(setting);
                }
                else
                {
                    filteredCount++;
                    _logService.Log(LogLevel.Debug,
                        $"Filtered out setting '{setting.Id}': {incompatibility}");
                }
            }

            if (filteredCount > 0)
            {
                _logService.Log(LogLevel.Debug,
                    $"Filtered out {filteredCount} incompatible settings. {compatibleSettings.Count} settings remain.");
            }

            return compatibleSettings;
        }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Error,
                $"Error filtering settings by Windows version: {ex.Message}. Returning all settings.");
            return settings;
        }
    }

    private IEnumerable<SettingDefinition> DecorateSettingsWithCompatibilityMessages(
        IEnumerable<SettingDefinition> settings)
    {
        var isWindows11 = _versionService.IsWindows11();
        var buildNumber = _versionService.GetWindowsBuildNumber();
        var buildRevision = _versionService.GetWindowsBuildRevision();

        foreach (var setting in settings)
        {
            var compatibilityMessage = BuildVersionGate.GetCompatibilityMessage(
                setting, isWindows11, buildNumber, buildRevision);

            if (compatibilityMessage != null)
            {
                var logKey = $"{setting.Name}:{compatibilityMessage}";
                if (_loggedCompatibilityMessages.TryAdd(logKey, 0))
                {
                    _logService.Log(LogLevel.Info, $"Adding compatibility message to {setting.Name}: {compatibilityMessage}");
                }

                yield return setting with { VersionCompatibilityMessage = compatibilityMessage };
            }
            else
            {
                yield return setting;
            }
        }
    }
}
