# Runs generated production scripts with explicit fake native and registry boundaries.
param([Parameter(Mandatory)][string]$PayloadPath)
$ErrorActionPreference = 'Stop'
$payload = Get-Content -LiteralPath $PayloadPath -Raw | ConvertFrom-Json
function Assert-Network($condition, [string]$message) { if (-not $condition) { throw $message } }
$fixture = @{
    Tcp = [pscustomobject]@{ AutoTuningLevelLocal = 'Normal'; EcnCapability = 'Disabled'; Timestamps = 'Disabled'; ScalingHeuristics = 'Disabled'; NonSackRttResiliency = 'Disabled'; ForceWS = 'Disabled'; InitialRto = 1000; MaxSynRetransmissions = 2 }
    Offload = [pscustomobject]@{ ReceiveSideScaling = 'Enabled'; ReceiveSegmentCoalescing = 'Enabled'; TaskOffload = 'Enabled' }
    IPv4 = [pscustomobject]@{ DefaultHopLimit = 128; NeighborCacheLimit = 256; RouteCacheLimit = 128; DhcpMediaSense = 'Enabled'; IcmpRedirects = 'Enabled' }
    IPv6 = [pscustomobject]@{ DefaultHopLimit = 128; NeighborCacheLimit = 256; RouteCacheLimit = 128; DhcpMediaSense = 'Enabled'; IcmpRedirects = 'Enabled' }
    Priorities = @{ LocalPriority = 499; HostsPriority = 500; DnsPriority = 2000; NetbtPriority = 2001 }
    Reads = @{}; Writes = @(); Failure = $null; Disposed = 0
}
function Get-NetworkFixture([string]$owner) {
    $fixture.Reads[$owner]++
    if ($fixture.Failure -eq $owner) { throw 'Simulated native discovery failure' }
    $fixture[$owner]
}
function Get-NetTCPSetting($SettingName, $ErrorAction) { Assert-Network ($SettingName -eq 'Internet') 'Unexpected TCP template'; Get-NetworkFixture 'Tcp' }
function Get-NetOffloadGlobalSetting($ErrorAction) { Get-NetworkFixture 'Offload' }
function Get-NetIPv4Protocol($ErrorAction) { Get-NetworkFixture 'IPv4' }
function Get-NetIPv6Protocol($ErrorAction) { Get-NetworkFixture 'IPv6' }
function Set-NetworkFixture([string]$owner, $parameters) {
    $fields = @($parameters.Keys | Where-Object { $_ -notin @('ErrorAction', 'SettingName') })
    Assert-Network ($fields.Count -eq 1) 'Write changed more than one native parameter'
    $parameter = $fields[0]
    $property = switch ($parameter) { 'InitialRtoMs' { 'InitialRto' } 'NeighborCacheLimitEntries' { 'NeighborCacheLimit' } 'RouteCacheLimitEntries' { 'RouteCacheLimit' } default { $parameter } }
    $fixture[$owner].$property = $parameters[$parameter]
    $fixture.Writes += [pscustomobject]@{ Owner = $owner; Parameter = $parameter; Value = [string]$parameters[$parameter] }
}
function Set-NetTCPSetting($SettingName, $AutoTuningLevelLocal, $EcnCapability, $Timestamps, $ScalingHeuristics, $NonSackRttResiliency, $ForceWS, $InitialRtoMs, $MaxSynRetransmissions, $ErrorAction) { Assert-Network ($SettingName -eq 'Internet') 'Wrong TCP write template'; Set-NetworkFixture 'Tcp' $PSBoundParameters }
function Set-NetOffloadGlobalSetting($ReceiveSideScaling, $ReceiveSegmentCoalescing, $TaskOffload, $ErrorAction) { Set-NetworkFixture 'Offload' $PSBoundParameters }
function Set-NetIPv4Protocol($DefaultHopLimit, $NeighborCacheLimitEntries, $RouteCacheLimitEntries, $DhcpMediaSense, $IcmpRedirects, $ErrorAction) { Set-NetworkFixture 'IPv4' $PSBoundParameters }
function Set-NetIPv6Protocol($DefaultHopLimit, $NeighborCacheLimitEntries, $RouteCacheLimitEntries, $DhcpMediaSense, $IcmpRedirects, $ErrorAction) { Set-NetworkFixture 'IPv6' $PSBoundParameters }
function Test-Path($LiteralPath, $ErrorAction) { Assert-Network ($LiteralPath -eq 'Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\ServiceProvider') 'Unexpected registry path'; $true }
function Get-Item($LiteralPath, $ErrorAction) {
    Assert-Network ($LiteralPath -eq 'Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\ServiceProvider') 'Unexpected registry read'
    $key = [pscustomobject]@{}
    $key | Add-Member ScriptMethod GetValue { param($name, $default) $fixture.Priorities[$name] }
    $key | Add-Member ScriptMethod GetValueKind { param($name) [Microsoft.Win32.RegistryValueKind]::DWord }
    $key | Add-Member ScriptMethod Dispose { $fixture.Disposed++ }
    $key
}
$readScript = [scriptblock]::Create($payload.Read)
$states = (& $readScript) | ConvertFrom-Json
Assert-Network ($states.Count -eq 25) 'Missing network controls'
Assert-Network (@($states | Where-Object { -not $_.IsAvailable }).Count -eq 0) 'Available fake controls were hidden'
Assert-Network (($states | Where-Object Id -eq 'tcp-initial-rto').Value -eq '1000') 'Read used a parameter alias instead of the native RTO property'
foreach ($owner in @('Tcp', 'Offload', 'IPv4', 'IPv6')) { Assert-Network ($fixture.Reads[$owner] -eq 1) 'Native source was read more than once' }
Assert-Network ($fixture.Disposed -eq 1 -and $fixture.Writes.Count -eq 0) 'Read mutated Windows or leaked the registry handle'
$fixture.Priorities.Remove('DnsPriority')
$states = (& $readScript) | ConvertFrom-Json
$dns = $states | Where-Object Id -eq 'priority-dns'
Assert-Network ($dns.IsAvailable -and $null -eq $dns.Value) 'Missing override was fabricated as a default value'
$fixture.Failure = 'IPv6'
$states = (& $readScript) | ConvertFrom-Json
Assert-Network (@($states | Where-Object { -not $_.IsAvailable }).Count -eq 5) 'Discovery failure did not stay within its owner'
Assert-Network (($states | Where-Object Id -eq 'ipv6-hop-limit').ErrorMessage -like '*Simulated*') 'Discovery error was swallowed'
$fixture.Failure = $null
foreach ($write in $payload.Writes) {
    $tokens = $null; $parseErrors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseInput($write.Script, [ref]$tokens, [ref]$parseErrors)
    Assert-Network ($parseErrors.Count -eq 0) 'Invalid production setter syntax'
    & ([scriptblock]::Create($write.Script))
    $actual = $fixture.Writes[-1]
    Assert-Network ($actual.Owner -eq $write.Owner -and $actual.Parameter -eq $write.Parameter -and $actual.Value -eq $write.Value) 'Write targeted the wrong owner, parameter or value'
}
Assert-Network ($fixture.Writes.Count -eq 21) 'Not every native control was exercised'
Write-Output 'PASS: 25 production reads, all 21 native setters, property aliases, absent overrides and isolated discovery failures.'
