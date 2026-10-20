# Runs the production security script against fake OS command and registry boundaries.
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot '../src/WinOptimizer.Infrastructure/Features/Security/Scripts/Security.ps1'
$parseErrors = $null
$null = [Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$null, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
$request = [pscustomobject]@{ Ids = @(); Fix = $null; Sid = 'S-1-5-21-111-222-333-1001' }
. $scriptPath | Out-Null
$script:checks = 0
function Assert-Equal($expected, $actual) {
    if ($expected -cne $actual) { throw "Expected '$expected', got '$actual'." }
    $script:checks++
}
function Get-MpComputerStatus { [pscustomobject]@{ AntivirusEnabled = $true; RealTimeProtectionEnabled = $script:realtime; AntivirusSignatureLastUpdated = '2026-01-01'; QuickScanEndTime = '2026-01-01' } }
function Get-CimInstance { @() }
function Get-MpThreat { $script:threats }
function Get-NetFirewallProfile { foreach ($name in @('Domain', 'Private', 'Public')) { [pscustomobject]@{ Name = $name; Enabled = $script:firewall } } }
function Get-NetIsatapConfiguration { [pscustomobject]@{ State = $script:isatap } }
function Get-NetTeredoConfiguration { [pscustomobject]@{ Type = $script:teredo } }
function Set-NetTeredoConfiguration { param($Type, $ErrorAction) $script:teredo = $Type }
function Set-NetIsatapConfiguration { param($State, $ErrorAction) $script:isatap = $State }
function Set-NetFirewallProfile { param($Profile, $Enabled, $ErrorAction) $script:firewall = 'True' }
function Set-MpPreference { param($DisableRealtimeMonitoring, $ErrorAction) $script:realtime = -not $DisableRealtimeMonitoring }
$script:realtime = $true; $script:firewall = 'True'; $script:isatap = 'Disabled'; $script:teredo = 'Disabled'; $script:threats = @()
foreach ($id in @('Antivirus', 'Firewall', 'Spyware', 'Isatap', 'Teredo')) { Assert-Equal 'Good' (Read-Check $id).State }
$script:threats = @([pscustomobject]@{ IsActive = $false; ThreatName = 'Resolved' }, [pscustomobject]@{ IsActive = $true; ThreatName = 'Active' })
Assert-Equal 'Active' (Read-Check 'Spyware').Evidence
$script:teredo = 'Client'; $script:isatap = 'Automatic'; $script:firewall = 'False'; $script:realtime = $false
foreach ($id in @('Antivirus', 'Firewall', 'Isatap', 'Teredo')) {
    $row = Read-Check $id
    Assert-Equal 'Attention' $row.State; Assert-Equal $true $row.CanFix
    $request = [pscustomobject]@{ Ids = @($id); Fix = $row; Sid = 'S-1-5-21-111-222-333-1001' }
    $updated = (. $scriptPath) | ConvertFrom-Json
    Assert-Equal 'Good' $updated[0].State
}
function Get-MpComputerStatus { throw 'Defender unavailable' }
function Get-CimInstance { [pscustomobject]@{ displayName = 'Other antivirus' } }
$provider = Read-Check 'Antivirus'
Assert-Equal 'Unknown' $provider.State; Assert-Equal 'Other antivirus' $provider.Current
Assert-Equal $true $provider.Evidence.Contains('Defender unavailable')
# Query exceptions become explicit unknown rows rather than empty or healthy results.
function Get-NetTeredoConfiguration { throw 'Provider unavailable' }
$request = [pscustomobject]@{ Ids = @('Teredo'); Fix = $null; Sid = 'S-1-5-21-111-222-333-1001' }
$unknown = (. $scriptPath) | ConvertFrom-Json
Assert-Equal 'Unknown' $unknown[0].State; Assert-Equal 'Provider unavailable' $unknown[0].Evidence; Assert-Equal $false $unknown[0].CanFix
# Registry probes are replaced only at their test boundary; the production UAC evaluator stays intact.
function Read-Value { param($path, $name) if ($name -eq 'EnableLUA') { 1 } else { 0 } }
Assert-Equal 'Attention' (Read-Check 'Uac').State
function Read-Value { param($path, $name) if ($name -eq 'EnableLUA') { 1 } else { 5 } }
Assert-Equal 'Good' (Read-Check 'Uac').State
function Read-Value { param($path, $name) 42 }
$badUac = $false
try { Read-Check 'Uac' | Out-Null } catch { $badUac = $_.Exception.Message.Contains('unrecognized') }
Assert-Equal $true $badUac
foreach ($scenario in @('changed', 'unverified')) {
    $before = if ($scenario -eq 'changed') { 'Enabled' } else { 'Client' }
    $negativeScript = @"
`$request = [pscustomobject]@{ Ids = @('Teredo'); Sid = 'S-1-5-21-111-222-333-1001'; Fix = [pscustomobject]@{ Id = 'Teredo'; Current = '$before'; Evidence = '$before' } }
function Get-NetTeredoConfiguration { [pscustomobject]@{ Type = 'Client' } }
function Set-NetTeredoConfiguration { }
. '$($scriptPath.Replace("'", "''"))'
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($negativeScript))
    $negativeProcess = [Diagnostics.Process]::new()
    $negativeProcess.StartInfo = [Diagnostics.ProcessStartInfo]::new('powershell.exe', '-NoProfile -ExecutionPolicy Bypass -EncodedCommand ' + $encoded)
    $negativeProcess.StartInfo.UseShellExecute = $false; $negativeProcess.StartInfo.CreateNoWindow = $true
    $negativeProcess.StartInfo.RedirectStandardError = $true
    try {
        [void]$negativeProcess.Start(); $errorText = $negativeProcess.StandardError.ReadToEnd(); $negativeProcess.WaitForExit()
        Assert-Equal 1 $negativeProcess.ExitCode
    }
    finally { $negativeProcess.Dispose() }
    $expected = if ($scenario -eq 'changed') { 'changed after scanning' } else { 'did not confirm' }
    Assert-Equal $true $errorText.Contains($expected)
}
Write-Host "PASS: $script:checks security script assertions; no Windows settings changed."
exit 0
