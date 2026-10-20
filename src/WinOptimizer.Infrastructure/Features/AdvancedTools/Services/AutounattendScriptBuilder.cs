using System.Linq;
using WinOptimizer.Core.Features.Common.Constants;
using System.Text;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Optimize.Interfaces;
using WinOptimizer.Infrastructure.Features.AdvancedTools.Helpers;
using WinOptimizer.Infrastructure.Features.AdvancedTools.ScriptSections;
namespace WinOptimizer.Infrastructure.Features.AdvancedTools.Services;

public class AutounattendScriptBuilder
{
    private readonly ILogService _logService;
    private readonly IPowerShellRunner _powerShellRunner;
    private readonly FeatureRegistryScriptSection _featureRegistrySection;
    private readonly PowerSettingsScriptSection _powerSettingsSection;
    private readonly AppRemovalScriptSection _appRemovalSection;

    public AutounattendScriptBuilder(
        IPowerSettingsQueryService powerSettingsQueryService,
        IHardwareDetectionService hardwareDetectionService,
        ILogService logService,
        IComboBoxResolver comboBoxResolver,
        IPowerShellRunner powerShellRunner)
    {
        _logService = logService;
        _powerShellRunner = powerShellRunner;

        var registryEmitter = new RegistryCommandEmitter(comboBoxResolver, logService);
        _featureRegistrySection = new FeatureRegistryScriptSection(registryEmitter, logService);
        _powerSettingsSection = new PowerSettingsScriptSection(powerSettingsQueryService, hardwareDetectionService, logService, comboBoxResolver);
        _appRemovalSection = new AppRemovalScriptSection();
    }

    public async Task<string> BuildOptimizationsScriptAsync(
        UnifiedConfigurationFile config,
        IReadOnlyDictionary<string, IEnumerable<SettingDefinition>> allSettings)
    {
        WarnOnUnreachableNativePowerApiSettings(config, allSettings);

        var sb = new StringBuilder();

        // 1. Header and setup
        ScriptPreambleSection.AppendHeader(sb);
        sb.AppendLine(AppStorageScript.Helpers);
        ScriptPreambleSection.AppendLoggingSetup(sb);
        ScriptPreambleSection.AppendHelperFunctions(sb);

        // 2. Build if (-not $UserCustomizations) block
        sb.AppendLine();
        sb.AppendLine("if (-not $UserCustomizations) {");
        sb.AppendLine();

        _appRemovalSection.AppendScriptsDirectorySetup(sb, "    ");

        if (config.WindowsApps.Items.Any())
        {
            await _appRemovalSection.AppendBloatRemovalScriptAsync(sb, config.WindowsApps.Items, "    ").ConfigureAwait(false);
        }

        _appRemovalSection.AppendWinOptimizerInstallerScriptContent(sb, "    ");

        // 2b. Power settings
        await _powerSettingsSection.AppendPowerSettingsSectionAsync(sb, config, allSettings, "    ").ConfigureAwait(false);

        // 2c. HKLM registry entries from Optimize
        if (config.Optimize.Features.Any())
        {
            _featureRegistrySection.AppendFeatureGroupRegistryEntries(sb, config.Optimize, allSettings, "Optimize", isHkcu: false, indent: "    ");
        }

        // 2d. HKLM registry entries from Customize
        if (config.Customize.Features.Any())
        {
            _featureRegistrySection.AppendFeatureGroupRegistryEntries(sb, config.Customize, allSettings, "Customize", isHkcu: false, indent: "    ");
        }

        // 2e. Clean Start Menu Layout (always included)
        SpecialFeatureScriptSection.AppendCleanStartMenuSection(sb, "    ");

        // 2f. Register UserCustomizations scheduled task
        SpecialFeatureScriptSection.AppendUserCustomizationsScheduledTask(sb, "    ");

        // 2g. System-wide custom script placeholder
        AppendCustomScriptPlaceholder(sb, "    ", "SYSTEM WIDE");

        sb.AppendLine("}");
        sb.AppendLine();

        // 3. Build if ($UserCustomizations) block
        sb.AppendLine("if ($UserCustomizations) {");
        sb.AppendLine();
        AppendUserDetectionBridge(sb);

        // 3a. HKCU registry entries from Optimize
        if (config.Optimize.Features.Any())
        {
            _featureRegistrySection.AppendFeatureGroupRegistryEntries(sb, config.Optimize, allSettings, "Optimize", isHkcu: true, indent: "            ");
        }

        // 3b. HKCU registry entries from Customize
        if (config.Customize.Features.Any())
        {
            _featureRegistrySection.AppendFeatureGroupRegistryEntries(sb, config.Customize, allSettings, "Customize", isHkcu: true, indent: "            ");
        }

        // 3c. User-specific custom script placeholder
        AppendCustomScriptPlaceholder(sb, "            ", "USER SPECIFIC");

        AppendUserDetectionBridgeClosing(sb);

        // 4. Completion block
        ScriptPreambleSection.AppendCompletionBlock(sb);

        var scriptContent = sb.ToString();

        // Validate the generated script has no PowerShell syntax errors
        try
        {
            await _powerShellRunner.ValidateScriptSyntaxAsync(scriptContent).ConfigureAwait(false);
            _logService.Log(LogLevel.Info, "Optimizations.ps1 script passed PowerShell syntax validation");
        }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Error, $"Optimizations.ps1 script failed PowerShell syntax validation: {ex.Message}");
            throw;
        }

        return scriptContent;
    }

    /// <summary>
    /// NativePowerApiSettings are applied via a managed Win32 API at runtime (see
    /// SettingOperationExecutor) and have no emitter in the autounattend pipeline. A setting
    /// whose only applicable payload is NativePowerApi would silently be skipped in an unattend
    /// install. Warn loudly so the author notices before shipping.
    /// </summary>
    private void WarnOnUnreachableNativePowerApiSettings(
        UnifiedConfigurationFile config,
        IReadOnlyDictionary<string, IEnumerable<SettingDefinition>> allSettings)
    {
        var selectedIds = new HashSet<string>(
            config.Optimize.Features.SelectMany(f => f.Value.Items.Select(i => i.Id))
                .Concat(config.Customize.Features.SelectMany(f => f.Value.Items.Select(i => i.Id))),
            StringComparer.OrdinalIgnoreCase);

        foreach (var group in allSettings)
        {
            foreach (var settingDef in group.Value)
            {
                if (!selectedIds.Contains(settingDef.Id)) continue;
                if (settingDef.NativePowerApiSettings?.Count is not > 0) continue;

                bool hasAutounattendFallback =
                    settingDef.RegistrySettings?.Count > 0
                    || settingDef.PowerCfgSettings?.Any() == true
                    || settingDef.PowerShellScripts?.Count > 0
                    || settingDef.RegContents?.Count > 0
                    || settingDef.ScheduledTaskSettings?.Count > 0
                    || settingDef.Id == "power-hibernation-enable";

                if (!hasAutounattendFallback)
                {
                    _logService.Log(
                        LogLevel.Warning,
                        $"Setting '{settingDef.Id}' is applied only via NativePowerApiSettings, " +
                        $"which has no autounattend emitter. It will be silently skipped during " +
                        $"unattend install. Add a RegistrySettings or PowerCfgSettings fallback.");
                }
            }
        }
    }

    private static void AppendCustomScriptPlaceholder(StringBuilder sb, string indent, string scopeLabel)
    {
        sb.AppendLine();
        sb.AppendLine($"{indent}# ============================================================================");
        sb.AppendLine($"{indent}# ADD YOUR {scopeLabel} POWERSHELL SCRIPT CONTENTS BELOW");
        sb.AppendLine($"{indent}# ============================================================================");
        sb.AppendLine();
        sb.AppendLine($"{indent}# Start here");
        sb.AppendLine();
        sb.AppendLine($"{indent}# End here");
        sb.AppendLine();
    }

    /// <summary>
    /// Emits the SYSTEM/User detection bridge at the start of the $UserCustomizations block.
    /// Detects the logged-in user, prepares private completion state and launches
    /// a child process as the interactive user.
    /// </summary>
    private static void AppendUserDetectionBridge(StringBuilder sb)
    {
        sb.AppendLine("""
    $runningAsSystem = ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -eq 'S-1-5-18')
    if ($runningAsSystem) {
        Write-Log "UserCustomizations running as SYSTEM, detecting logged-in user..." "INFO"
        $targetUser = $null
        for ($attempt = 1; $attempt -le 12; $attempt++) {
            $targetUser = Get-TargetUser
            if ($targetUser) { break }
            Write-Log "Waiting for user login (attempt $attempt/12)..." "INFO"
            Start-Sleep -Seconds 10
        }
        if (-not $targetUser) {
            Write-Log "No logged-in user detected after 2 minutes, will retry at next logon" "WARNING"
            exit 1
        }
        $targetUserSID = Get-UserSID -Username $targetUser
        if (-not $targetUserSID) { throw "Failed to get SID for user: $targetUser" }
        $userStorage = Initialize-WinOptimizerUserDirectory $targetUserSID
        $markerPath = Join-Path $userStorage 'State\Customizations\UserCustomizationsApplied.json'
        $completed = Read-WinOptimizerCompletionMarker $markerPath
        if ($null -ne $completed -and $completed.Value -eq 1) {
            Write-Log "User customizations have already been applied for this user" "INFO"
            Write-Log "To re-apply, delete: $markerPath" "INFO"
        } else {
            Write-Log "Launching child process as interactive user to apply customizations..." "INFO"
            icacls $LogPath /grant "${targetUser}:(M)" 2>&1 | Out-Null
            $scriptPath = $MyInvocation.MyCommand.Path
            $cmdLine = "powershell.exe -ExecutionPolicy Bypass -NoProfile -WindowStyle Hidden -File `"$scriptPath`" -UserCustomizations"
            $success = Start-ProcessAsUser -CommandLine $cmdLine
            if ($success) {
                Write-Log "Child process completed successfully" "SUCCESS"
                shutdown.exe /r /t 20
            } else {
                Write-Log "Child process failed or timed out - will retry at next logon" "ERROR"
                exit 1
            }
        }
    } else {
        Write-Log "UserCustomizations running as user" "INFO"
        $userSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        $markerPath = Join-Path (Get-WinOptimizerUserDirectory $userSid) 'State\Customizations\UserCustomizationsApplied.json'
        $completed = Read-WinOptimizerCompletionMarker $markerPath
        if ($null -ne $completed -and $completed.Value -eq 1) {
            Write-Log "User customizations have already been applied for this user" "INFO"
            Write-Log "To re-apply, delete: $markerPath" "INFO"
        } else {
            Write-Log "Applying user customizations for the first time..." "INFO"
""");
    }

    /// <summary>Marks successfully completed user work in the account's private disk state.</summary>
    private static void AppendUserDetectionBridgeClosing(StringBuilder sb)
    {
        sb.AppendLine("""
            Save-WinOptimizerJson $markerPath @{Version=1; Value=1} -ReplaceExisting
            Write-Log "User customizations completed and marked as applied" "SUCCESS"
        }
    }
}
""");
    }
}
