using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
namespace WinOptimizer.Infrastructure.Features.Optimize.Services;

public class UpdateService(
    ILogService logService,
    IWindowsRegistryService registryService,
    IProcessExecutor processExecutor,
    IPowerShellRunner powerShellRunner,
    IFileSystemService fileSystemService) : ISpecialSettingHandler
{
    public async Task<bool> TryApplySpecialSettingAsync(SettingDefinition setting, object value, bool additionalContext = false, ISettingApplicationService? settingApplicationService = null)
    {
        if (setting.Id == SettingIds.UpdatesPolicyMode && value is int index)
        {
            await ApplyUpdatesPolicyModeAsync(setting, index, settingApplicationService).ConfigureAwait(false);
            return true;
        }
        return false;
    }

    public async Task<Dictionary<string, Dictionary<string, object?>>> DiscoverSpecialSettingsAsync(IEnumerable<SettingDefinition> settings)
    {
        var results = new Dictionary<string, Dictionary<string, object?>>();

        var updatesSetting = settings.FirstOrDefault(s => s.Id == SettingIds.UpdatesPolicyMode);
        if (updatesSetting != null)
        {
            var currentIndex = await GetCurrentUpdatePolicyIndexAsync().ConfigureAwait(false);
            results[SettingIds.UpdatesPolicyMode] = new Dictionary<string, object?> { ["CurrentPolicyIndex"] = currentIndex };
        }

        return results;
    }

    public async Task ApplyUpdatesPolicyModeAsync(SettingDefinition setting, object value, ISettingApplicationService? settingApplicationService = null)
    {
        if (value is not int selectionIndex)
            throw new ArgumentException("Expected integer selection index");

        logService.Log(LogLevel.Info, $"[UpdateService] Applying updates-policy-mode with index: {selectionIndex}");

        switch (selectionIndex)
        {
            case 0:
                await ApplyNormalModeAsync(setting).ConfigureAwait(false);
                break;
            case 1:
                await ApplySecurityOnlyModeAsync(setting).ConfigureAwait(false);
                break;
            case 2:
                await ApplyPausedModeAsync(setting, settingApplicationService).ConfigureAwait(false);
                break;
            case 3:
                await ApplyDisabledModeAsync(setting, settingApplicationService).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentException($"Invalid selection index: {selectionIndex}");
        }

        logService.Log(LogLevel.Info, $"[UpdateService] Successfully applied updates-policy-mode index {selectionIndex}");
    }

    private async Task ApplyNormalModeAsync(SettingDefinition setting)
    {
        await RestoreCriticalDllsAsync().ConfigureAwait(false);
        await EnableUpdateServicesAsync().ConfigureAwait(false);
        await EnableUpdateTasksAsync().ConfigureAwait(false);
        ApplyRegistrySettingsForIndex(setting, 0);
    }

    private async Task ApplySecurityOnlyModeAsync(SettingDefinition setting)
    {
        await RestoreCriticalDllsAsync().ConfigureAwait(false);
        await EnableUpdateServicesAsync().ConfigureAwait(false);
        ApplyRegistrySettingsForIndex(setting, 1);
    }

    // Based on work by Aetherinox: https://github.com/Aetherinox/pause-windows-updates/blob/main/windows-updates-pause.reg
    private async Task ApplyPausedModeAsync(SettingDefinition setting, ISettingApplicationService? settingApplicationService)
    {
        logService.Log(LogLevel.Info, "[UpdateService] Applying recommended settings before pausing updates");
        try
        {
            if (settingApplicationService == null)
                throw new InvalidOperationException("settingApplicationService is required for applying recommended settings");
            await settingApplicationService.ApplyRecommendedSettingsForFeatureAsync(setting.Id).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logService.Log(LogLevel.Warning, $"[UpdateService] Failed to apply some recommended settings: {ex.Message}");
        }

        await RestoreCriticalDllsAsync().ConfigureAwait(false);
        await SetUpdateServicesManualAsync().ConfigureAwait(false);
        ApplyRegistrySettingsForIndex(setting, 2);
    }

    // Based on work by Chris Titus: https://github.com/ChrisTitusTech/winutil/blob/main/functions/public/Invoke-WPFUpdatesdisable.ps1
    private async Task ApplyDisabledModeAsync(SettingDefinition setting, ISettingApplicationService? settingApplicationService)
    {
        logService.Log(LogLevel.Info, "[UpdateService] Applying recommended settings before disabling updates");
        try
        {
            if (settingApplicationService == null)
                throw new InvalidOperationException("settingApplicationService is required for applying recommended settings");
            await settingApplicationService.ApplyRecommendedSettingsForFeatureAsync(setting.Id).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logService.Log(LogLevel.Warning, $"[UpdateService] Failed to apply some recommended settings: {ex.Message}");
        }

        await DisableUpdateServicesAsync().ConfigureAwait(false);
        await DisableUpdateTasksAsync().ConfigureAwait(false);
        await RenameCriticalDllsAsync().ConfigureAwait(false);
        await CleanupUpdateFilesAsync().ConfigureAwait(false);
        ApplyRegistrySettingsForIndex(setting, 3);
    }

    private async Task RunCommandAsync(string command, int? acceptedStateCode = null)
    {
        var result = await processExecutor.ExecuteAsync("cmd.exe", $"/c chcp 65001 && {command}").ConfigureAwait(false);
        if (!result.Succeeded && result.ExitCode != acceptedStateCode)
            throw new IOException($"Windows Update command failed ({result.ExitCode}): {command}. {result.StandardError} {result.StandardOutput}".Trim());
    }

    private async Task DisableUpdateServicesAsync()
    {
        foreach (var service in new[] { "wuauserv", "UsoSvc", "WaaSMedicSvc" })
        {
            const int serviceNotActive = 1062;
            await RunCommandAsync($"sc stop {service}", serviceNotActive).ConfigureAwait(false);
            await RunCommandAsync($"sc config {service} start= disabled").ConfigureAwait(false);
            await RunCommandAsync($"sc failure {service} reset= 0 actions= \"\"").ConfigureAwait(false);
        }
    }

    private async Task EnableUpdateServicesAsync()
    {
        foreach (var (service, startType) in new[] { ("wuauserv", "auto"), ("UsoSvc", "auto"), ("WaaSMedicSvc", "demand") })
        {
            const int serviceAlreadyRunning = 1056;
            await RunCommandAsync($"sc config {service} start= {startType}").ConfigureAwait(false);
            await RunCommandAsync($"sc start {service}", serviceAlreadyRunning).ConfigureAwait(false);
        }
    }

    private async Task SetUpdateServicesManualAsync()
    {
        foreach (var service in new[] { "wuauserv", "UsoSvc", "WaaSMedicSvc" })
            await RunCommandAsync($"sc config {service} start= demand").ConfigureAwait(false);
    }

    private Task DisableUpdateTasksAsync() => SetUpdateTasksAsync(enable: false,
        [@"\Microsoft\Windows\InstallService\", @"\Microsoft\Windows\UpdateOrchestrator\",
         @"\Microsoft\Windows\UpdateAssistant\", @"\Microsoft\Windows\WaaSMedic\", @"\Microsoft\Windows\WindowsUpdate\"]);

    private Task EnableUpdateTasksAsync() => SetUpdateTasksAsync(enable: true,
        [@"\Microsoft\Windows\UpdateOrchestrator\", @"\Microsoft\Windows\WindowsUpdate\"]);

    private Task SetUpdateTasksAsync(bool enable, string[] folders)
    {
        var taskPaths = string.Join(", ", folders.Select(folder => $"'{folder}'"));
        var command = enable ? "Enable-ScheduledTask" : "Disable-ScheduledTask";
        return powerShellRunner.RunScriptAsync(
            $"$ErrorActionPreference = 'Stop'; $folders = @({taskPaths}); Get-ScheduledTask | Where-Object {{ $_.TaskPath -in $folders }} | {command}");
    }

    private async Task RenameCriticalDllsAsync()
    {
        foreach (var dll in new[] { "WaaSMedicSvc.dll", "wuaueng.dll" })
        {
            var dllPath = $@"C:\Windows\System32\{dll}";
            var backupPath = $@"C:\Windows\System32\{fileSystemService.GetFileNameWithoutExtension(dll)}_BAK.dll";
            if (!fileSystemService.FileExists(dllPath)) continue;
            if (fileSystemService.FileExists(backupPath))
            {
                await TakeOwnershipAsync(backupPath).ConfigureAwait(false);
                fileSystemService.DeleteFile(backupPath);
            }
            await TakeOwnershipAsync(dllPath).ConfigureAwait(false);
            fileSystemService.MoveFile(dllPath, backupPath);
        }
    }

    private async Task RestoreCriticalDllsAsync()
    {
        foreach (var dll in new[] { "WaaSMedicSvc.dll", "wuaueng.dll" })
        {
            var dllPath = $@"C:\Windows\System32\{dll}";
            var backupPath = $@"C:\Windows\System32\{fileSystemService.GetFileNameWithoutExtension(dll)}_BAK.dll";
            if (!fileSystemService.FileExists(backupPath)) continue;
            await TakeOwnershipAsync(backupPath).ConfigureAwait(false);
            if (fileSystemService.FileExists(dllPath)) fileSystemService.DeleteFile(backupPath);
            else fileSystemService.MoveFile(backupPath, dllPath);
        }
    }

    private async Task TakeOwnershipAsync(string path)
    {
        await RunCommandAsync($"takeown /f \"{path}\"").ConfigureAwait(false);
        await RunCommandAsync($"icacls \"{path}\" /grant *S-1-1-0:F").ConfigureAwait(false);
    }

    private Task CleanupUpdateFilesAsync() => powerShellRunner.RunScriptAsync("""
        $ErrorActionPreference = 'Stop'
        $folder = 'C:\Windows\SoftwareDistribution'
        if (Test-Path -LiteralPath $folder) {
            Get-ChildItem -LiteralPath $folder -Force | Remove-Item -Recurse -Force
        }
        """);

    private void ApplyRegistrySettingsForIndex(SettingDefinition setting, int index)
    {
        var options = setting.ComboBox?.Options;
        if (options == null || index < 0 || index >= options.Count || options[index].ValueMappings is not { } valueMapping)
            return;

        foreach (var registrySetting in setting.RegistrySettings)
        {
            if (registrySetting.ValueName is { } name && valueMapping.TryGetValue(name, out var value) &&
                !registryService.ApplySetting(registrySetting, value != null, value))
                throw new IOException($"Windows could not apply Windows Update policy '{name}'.");
        }
    }

    public async Task<int> GetCurrentUpdatePolicyIndexAsync()
    {
        if (AreCriticalDllsRenamed())
            return 3;

        if (IsUpdatesPaused())
            return 2;

        var deferFeature = registryService.GetValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings",
            "DeferFeatureUpdates");

        if (deferFeature is int defer && defer == 1)
            return 1;

        return 0;
    }

    private bool AreCriticalDllsRenamed()
    {
        var dlls = new[] { "WaaSMedicSvc.dll", "wuaueng.dll" };

        foreach (var dll in dlls)
        {
            var dllPath = $@"C:\Windows\System32\{dll}";
            var backupPath = $@"C:\Windows\System32\{fileSystemService.GetFileNameWithoutExtension(dll)}_BAK.dll";

            if (fileSystemService.FileExists(backupPath) && !fileSystemService.FileExists(dllPath))
                return true;
        }

        return false;
    }

    private bool IsUpdatesPaused()
    {
        var pauseStart = registryService.GetValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings",
            "PauseUpdatesStartTime");

        var pauseExpiry = registryService.GetValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings",
            "PauseUpdatesExpiryTime");

        if (pauseStart != null || pauseExpiry != null)
            return true;

        var pausedQuality = registryService.GetValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings",
            "PausedQualityDate");

        var pausedFeature = registryService.GetValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings",
            "PausedFeatureDate");

        if (pausedQuality != null || pausedFeature != null)
            return true;

        return false;
    }
}
