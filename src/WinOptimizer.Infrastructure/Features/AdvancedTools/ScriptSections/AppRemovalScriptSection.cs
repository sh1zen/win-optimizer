using System;
using System.Collections.Generic;
using System.Text;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.SoftwareApps.Utilities;

namespace WinOptimizer.Infrastructure.Features.AdvancedTools.ScriptSections;

/// <summary>
/// Handles scripts directory setup and app removal script embedding.
/// </summary>
internal class AppRemovalScriptSection
{
    public void AppendScriptsDirectorySetup(StringBuilder sb, string indent = "")
    {
        sb.AppendLine($"{indent}$scriptsDir = \"{ScriptPaths.ScriptsDirectoryLiteral}\"");
        sb.AppendLine($"{indent}if (!(Test-Path $scriptsDir)) {{");
        sb.AppendLine($"{indent}    New-Item -ItemType Directory -Path $scriptsDir -Force | Out-Null");
        sb.AppendLine($"{indent}    Write-Log \"Created scripts directory: $scriptsDir\" \"SUCCESS\"");
        sb.AppendLine($"{indent}}} else {{");
        sb.AppendLine($"{indent}    Write-Log \"Scripts directory already exists: $scriptsDir\" \"INFO\"");
        sb.AppendLine($"{indent}}}");
        sb.AppendLine();
    }

    /// <summary>Emits one package and Windows feature removal job for all selected identities.</summary>
    public void AppendBloatRemovalScript(StringBuilder sb, IReadOnlyList<ConfigurationItem> selectedApps, string indent = "")
    {
        List<string> packages = [];
        List<string> capabilities = [];
        List<string> optionalFeatures = [];
        foreach (var app in selectedApps)
        {
            if (!string.IsNullOrEmpty(app.CapabilityName))
                capabilities.Add(app.CapabilityName);
            else if (!string.IsNullOrEmpty(app.OptionalFeatureName))
                optionalFeatures.Add(app.OptionalFeatureName);
            else if (app.AppxPackageName is { Length: > 0 })
                packages.AddRange(app.AppxPackageName);
        }

        if (packages.Count == 0 && capabilities.Count == 0 && optionalFeatures.Count == 0) return;
        sb.AppendLine();
        sb.AppendLine($"{indent}# WINDOWS APPS REMOVAL");
        sb.AppendLine($"{indent}$removalContent = @'");
        sb.Append(BloatRemovalScriptGenerator.GenerateScript(packages, capabilities, optionalFeatures));
        sb.AppendLine("'@");
        sb.AppendLine($"{indent}$removalPath = Join-Path $scriptsDir '{ScriptPaths.PackageRemovalFileName}'");
        sb.AppendLine($"{indent}try {{");
        sb.AppendLine($"{indent}    $powershell = Join-Path $env:SystemRoot 'System32\\WindowsPowerShell\\v1.0\\powershell.exe'");
        sb.AppendLine($"{indent}    $removalContent | Out-File -LiteralPath $removalPath -Encoding UTF8 -Force -ErrorAction Stop");
        sb.AppendLine($"{indent}    & $powershell -ExecutionPolicy Bypass -NoProfile -File $removalPath");
        sb.AppendLine($"{indent}    if ($LASTEXITCODE -ne 0) {{ Write-Log \"Windows package removal failed (exit $LASTEXITCODE)\" \"ERROR\" }}");
        sb.AppendLine($"{indent}    else {{ Write-Log 'Windows package removal completed' 'SUCCESS' }}");
        sb.AppendLine($"{indent}    $action = New-ScheduledTaskAction -Execute $powershell -Argument ('-ExecutionPolicy Bypass -NoProfile -File ' + [char]34 + $removalPath + [char]34)");
        sb.AppendLine($"{indent}    $trigger = New-ScheduledTaskTrigger -AtLogOn");
        sb.AppendLine($"{indent}    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit 0");
        sb.AppendLine($"{indent}    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest");
        sb.AppendLine($"{indent}    Register-ScheduledTask -TaskName '{ScriptPaths.PackageRemovalTaskName}' -TaskPath '\\win-optimizer' -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force -ErrorAction Stop | Out-Null");
        sb.AppendLine($"{indent}}} catch {{");
        sb.AppendLine($"{indent}    Write-Log \"Windows package removal setup failed: $($_.Exception.Message)\" \"ERROR\"");
        sb.AppendLine($"{indent}}}");
        sb.AppendLine();
    }
}
