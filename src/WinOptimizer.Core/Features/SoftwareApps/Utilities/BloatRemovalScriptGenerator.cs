using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using WinOptimizer.Core.Features.Common.Constants;

namespace WinOptimizer.Core.Features.SoftwareApps.Utilities;

/// <summary>Builds the shared Windows servicing script for interactive, scheduled and setup use.</summary>
public static class BloatRemovalScriptGenerator
{
    public const string ScriptVersion = "3.1";
    public const string ResultPrefix = "WINOPTIMIZER_REMOVAL_RESULTS:";
    private static readonly ConcurrentDictionary<string, Regex> ArrayPatternCache = new();

    public static string GenerateScript(List<string> packages, List<string> capabilities, List<string> optionalFeatures,
        bool reportResults = false, string? cancellationEventName = null)
    {
        var script = new StringBuilder();
        script.AppendLine($"# Windows package servicing. Script Version: {ScriptVersion}");
        script.AppendLine("$reportResults = " + (reportResults ? "$true" : "$false"));
        script.AppendLine($"$cancellationEventName = '{cancellationEventName?.Replace("'", "''")}'");
        script.AppendLine($"$logFolder = \"{ScriptPaths.LogsDirectoryLiteral}\"");
        script.AppendLine($"$resultPrefix = '{ResultPrefix}'");
        AppendArray(script, "packages", packages);
        AppendArray(script, "capabilities", capabilities);
        AppendArray(script, "optionalFeatures", optionalFeatures);
        script.AppendLine(RemovalLogic);
        return script.ToString();
    }

    public static List<string> ExtractArrayFromScript(string content, string arrayName)
    {
        var regex = ArrayPatternCache.GetOrAdd(arrayName, name => new Regex(
            @"\$" + Regex.Escape(name) + @"\s*=\s*@\(\s*(.*?)\s*\)", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase));
        var match = regex.Match(content);
        return match.Success
            ? match.Groups[1].Value.Split('\n').Select(line => line.Trim().Trim(',').Trim('\'', '"').Replace("''", "'"))
                .Where(item => !string.IsNullOrWhiteSpace(item)).ToList()
            : [];
    }

    public static string UpdateScriptTemplate(string existingContent) => GenerateScript(
        ExtractArrayFromScript(existingContent, "packages"), ExtractArrayFromScript(existingContent, "capabilities"),
        ExtractArrayFromScript(existingContent, "optionalFeatures"));

    private static void AppendArray(StringBuilder script, string name, IEnumerable<string> values)
    {
        script.Append('$').Append(name).AppendLine(" = @(");
        foreach (var value in values.Distinct(StringComparer.OrdinalIgnoreCase))
            script.AppendLine($"    '{value.Replace("'", "''")}'");
        script.AppendLine(")");
    }

    private const string RemovalLogic = """
        function Invoke-WindowsPackageRemoval([string[]]$Packages, [string[]]$Capabilities,
            [string[]]$OptionalFeatures, [string]$LogFolder, [bool]$ReportResults, [Threading.EventWaitHandle]$CancelSignal) {
            $ErrorActionPreference = 'Stop'

            $logFile = Join-Path $logFolder 'BloatRemovalLog.txt'
            if (!(Test-Path -LiteralPath $logFolder)) { New-Item -ItemType Directory -Path $logFolder -Force | Out-Null }
            function Write-Log([string]$Message) {
                if ((Test-Path -LiteralPath $logFile) -and (Get-Item -LiteralPath $logFile).Length -gt 512000) {
                    Remove-Item -LiteralPath $logFile -Force
                }
                "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') - $Message" | Out-File -LiteralPath $logFile -Append
                Write-Host $Message
            }

            $results = @{}
            function Test-RemovalCancelled { return $null -ne $CancelSignal -and $CancelSignal.WaitOne(0) }

            function Start-Removal([string]$Key) {
                $results[$Key] = @{ Key = $Key; Error = $null; RestartRequired = $false }
            }

            function Set-RemovalFailure([string]$Key, [string]$Message) {
                if (!$results.ContainsKey($Key)) { Start-Removal $Key }
                $previous = $results[$Key].Error
                $results[$Key].Error = if ($previous) { "$previous $Message" } else { $Message }
                Write-Log "Failed $($Key): $Message"
            }

            # Work items retain their catalogue identity when an app has several native package names.
            function Invoke-PackagePhase([object[]]$Items, [scriptblock]$Remove) {
                if ($Items.Count -eq 0) { return }
                $jobs = [Collections.Generic.List[object]]::new()
                $pool = [RunspaceFactory]::CreateRunspacePool(1, [Math]::Min($Items.Count, 10))
                try {
                    $pool.Open()
                    foreach ($item in $Items) {
                        $pipe = [PowerShell]::Create()
                        try {
                            $null = $pipe.AddScript($Remove.ToString()).AddArgument($item.NativeName)
                            $pipe.RunspacePool = $pool
                            $jobs.Add(@{ Item = $item; Pipe = $pipe; Handle = $pipe.BeginInvoke() })
                        } catch {
                            $pipe.Dispose()
                            Set-RemovalFailure $item.Key $_.Exception.Message
                        }
                    }
                    foreach ($job in $jobs) {
                        try {
                            $null = $job.Pipe.EndInvoke($job.Handle)
                            if ($job.Pipe.HadErrors) { throw ($job.Pipe.Streams.Error -join ' ') }
                            Write-Log "Removed $($job.Item.NativeName)"
                        } catch {
                            Set-RemovalFailure $job.Item.Key $_.Exception.Message
                        }
                    }
                } catch {
                    foreach ($item in $Items) { Set-RemovalFailure $item.Key $_.Exception.Message }
                } finally {
                    foreach ($job in $jobs) { $job.Pipe.Dispose() }
                    $pool.Dispose()
                }
            }

            # Finish both AppX phases once started so provisioning and registration stay in sync.
            if ($packages.Count -gt 0 -and !(Test-RemovalCancelled)) {
                foreach ($name in $packages) { Start-Removal "package:$name" }
                try {
                    $installed = @(Get-AppxPackage -AllUsers -ErrorAction Stop)
                    $provisioned = @(Get-AppxProvisionedPackage -Online -ErrorAction Stop)
                    $registeredWork = @(foreach ($package in $installed) {
                        if ($package.Name -in $packages) { @{ Key = "package:$($package.Name)"; NativeName = $package.PackageFullName } }
                    })
                    $provisionedWork = @(foreach ($package in $provisioned) {
                        if ($package.DisplayName -in $packages) { @{ Key = "package:$($package.DisplayName)"; NativeName = $package.PackageName } }
                    })
                    # Deprovision first so Remove-AppxPackage -AllUsers can succeed on Windows 10.
                    Invoke-PackagePhase $provisionedWork {
                        param($name)
                        Remove-AppxProvisionedPackage -Online -PackageName $name -ErrorAction Stop | Out-Null
                    }
                    Invoke-PackagePhase $registeredWork {
                        param($name)
                        Remove-AppxPackage -Package $name -AllUsers -ErrorAction Stop
                    }
                } catch {
                    foreach ($name in $packages) { Set-RemovalFailure "package:$name" $_.Exception.Message }
                }
            }

            if ($capabilities.Count -gt 0 -and !(Test-RemovalCancelled)) {
                try {
                    $installed = @(Get-WindowsCapability -Online -ErrorAction Stop)
                    foreach ($name in $capabilities) {
                        if (Test-RemovalCancelled) { break }
                        $key = "capability:$name"
                        Start-Removal $key
                        foreach ($entry in $installed | Where-Object {
                            ($_.Name -eq $name -or $_.Name.StartsWith("$name~", [StringComparison]::OrdinalIgnoreCase)) -and
                            $_.State -in 'Installed', 'UninstallPending'
                        }) {
                            try {
                                if ($entry.State -eq 'UninstallPending') {
                                    $results[$key].RestartRequired = $true
                                } else {
                                    $change = Remove-WindowsCapability -Online -Name $entry.Name -ErrorAction Stop
                                    if ($change.RestartNeeded) { $results[$key].RestartRequired = $true }
                                    Write-Log "Removed $($entry.Name)"
                                }
                            } catch { Set-RemovalFailure $key $_.Exception.Message }
                        }
                    }
                } catch {
                    foreach ($name in $capabilities) { Set-RemovalFailure "capability:$name" $_.Exception.Message }
                }
            }

            if ($optionalFeatures.Count -gt 0 -and !(Test-RemovalCancelled)) {
                try {
                    $features = @(Get-WindowsOptionalFeature -Online -ErrorAction Stop)
                    foreach ($name in $optionalFeatures) {
                        if (Test-RemovalCancelled) { break }
                        $key = "feature:$name"
                        Start-Removal $key
                        try {
                            $feature = $features | Where-Object FeatureName -EQ $name
                            if ($feature.State -eq 'DisablePending') {
                                $results[$key].RestartRequired = $true
                            } elseif ($feature.State -eq 'Enabled') {
                                $change = Disable-WindowsOptionalFeature -Online -FeatureName $name -NoRestart -ErrorAction Stop
                                $results[$key].RestartRequired = [bool]$change.RestartNeeded
                                Write-Log "Disabled $name"
                            }
                        } catch { Set-RemovalFailure $key $_.Exception.Message }
                    }
                } catch {
                    foreach ($name in $optionalFeatures) { Set-RemovalFailure "feature:$name" $_.Exception.Message }
                }
            }

            if ($reportResults) {
                $json = ConvertTo-Json -InputObject @($results.Values) -Compress -Depth 3
                Write-Output ($resultPrefix + $json)
            } elseif (@($results.Values | Where-Object Error).Count -gt 0) { exit 1 }
        }

        if ($MyInvocation.InvocationName -ne '.') {
            if (!([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
                [Security.Principal.WindowsBuiltInRole]::Administrator)) {
                if ($reportResults) { throw 'Windows package servicing requires administrator privileges.' }
                $child = Start-Process PowerShell.exe -ArgumentList ("-NoProfile -ExecutionPolicy Bypass -File " + [char]34 + $PSCommandPath + [char]34) -Verb RunAs -Wait -PassThru
                exit $child.ExitCode
            }
            $cancelSignal = $null
            try {
                if ($cancellationEventName) { $cancelSignal = [Threading.EventWaitHandle]::OpenExisting($cancellationEventName) }
                Invoke-WindowsPackageRemoval -Packages $packages -Capabilities $capabilities -OptionalFeatures $optionalFeatures `
                    -LogFolder $logFolder -ReportResults $reportResults -CancelSignal $cancelSignal
            } finally {
                if ($null -ne $cancelSignal) { $cancelSignal.Dispose() }
            }
        }
        """;
}
