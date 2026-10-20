# Native boundary for security checks. Each failed read remains an explicit Unknown result.
function Read-Value([string]$path, [string]$name) {
    $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($path)
    try { if ($null -ne $key) { return $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) } }
    finally { if ($null -ne $key) { $key.Dispose() } }
}
function Read-UserValue([string]$path, [string]$name) {
    $key = [Microsoft.Win32.Registry]::Users.OpenSubKey($request.Sid + '\' + $path)
    try { if ($null -ne $key) { return $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) } }
    finally { if ($null -ne $key) { $key.Dispose() } }
}
function New-Result($id, $state, $current, $evidence, $canFix = $false) {
    [pscustomobject]@{ Id = $id; State = $state; Current = [string]$current; Evidence = [string]$evidence; CanFix = [bool]$canFix }
}
function Read-Check([string]$id) {
    switch ($id) {
        'Antivirus' {
            $mp = $null; $mpError = ''
            try { $mp = Get-MpComputerStatus -ErrorAction Stop }
            catch { $mpError = $_.Exception.Message }
            # Registration is not proof of a third-party provider's current protection state.
            if ($mp.AntivirusEnabled -and $mp.RealTimeProtectionEnabled) {
                return New-Result $id 'Good' 'Enabled' ('Microsoft Defender; signatures: ' + $mp.AntivirusSignatureLastUpdated)
            }
            $products = @(Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction Stop)
            $other = @($products | Where-Object { $_.displayName -notmatch 'Windows Defender|Microsoft Defender' })
            if ($other.Count -gt 0) { return New-Result $id 'Unknown' ($other.displayName -join ', ') ('Registered antivirus products; verify protection in Windows Security. ' + $mpError) }
            if ($null -eq $mp) { return New-Result $id 'Unknown' '' $mpError }
            return New-Result $id 'Attention' 'Disabled' ('Defender antivirus: ' + $mp.AntivirusEnabled + '; real-time: ' + $mp.RealTimeProtectionEnabled) ([bool]$mp.AntivirusEnabled)
        }
        'Firewall' {
            $profiles = @(Get-NetFirewallProfile -PolicyStore ActiveStore -ErrorAction Stop | Sort-Object Name)
            if ($profiles.Count -ne 3 -or ($profiles.Name -join ',') -ne 'Domain,Private,Public' -or
                @($profiles | Where-Object { [string]$_.Enabled -notin @('True', 'False') }).Count -gt 0) {
                throw 'The complete Windows firewall profile inventory is unavailable.'
            }
            $evidence = ($profiles | ForEach-Object { $_.Name + '=' + $_.Enabled }) -join '; '
            if (@($profiles | Where-Object { $_.Enabled -ne 'True' }).Count -eq 0) { return New-Result $id 'Good' 'Enabled' $evidence }
            return New-Result $id 'Attention' 'Disabled' $evidence $true
        }
        'Spyware' {
            $mp = Get-MpComputerStatus -ErrorAction Stop
            if (-not $mp.AntivirusEnabled) { return New-Result $id 'Unknown' '' 'Microsoft Defender is not active; use the installed antivirus to scan for threats.' }
            $threats = @(Get-MpThreat -ErrorAction Stop | Where-Object { $_.IsActive })
            if ($threats.Count -gt 0) { return New-Result $id 'Attention' ([string]$threats.Count) (($threats | ForEach-Object { $_.ThreatName }) -join '; ') }
            return New-Result $id 'Good' '0' ('No known active Defender threats; last quick scan: ' + $mp.QuickScanEndTime)
        }
        'Uac' {
            $path = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
            $enabled = Read-Value $path 'EnableLUA'
            $prompt = Read-Value $path 'ConsentPromptBehaviorAdmin'
            if ($enabled -isnot [int] -or $prompt -isnot [int] -or $enabled -notin @(0, 1) -or $prompt -notin @(0, 1, 2, 3, 4, 5)) {
                throw 'UAC policy values are unavailable or unrecognized.'
            }
            $evidence = 'EnableLUA=' + $enabled + '; ConsentPromptBehaviorAdmin=' + $prompt
            if ($enabled -eq 1 -and $prompt -gt 0) { return New-Result $id 'Good' 'Enabled' $evidence }
            return New-Result $id 'Attention' 'Disabled' $evidence
        }
        'Updates' {
            $session = $searcher = $result = $null
            try {
                $session = New-Object -ComObject Microsoft.Update.Session
                $searcher = $session.CreateUpdateSearcher()
                $searcher.Online = $false
                $result = $searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Software'")
                if ([int]$result.ResultCode -ne 2) { throw 'The cached Windows Update search did not complete successfully.' }
                $updates = @($result.Updates | Where-Object { @($_.Categories | Where-Object { $_.CategoryID -in @('0fa1201d-4330-4fa8-8ae9-b877473b6441', 'e6cf1350-c01b-414d-a61f-263d14d133b4') }).Count -gt 0 })
                if ($updates.Count -gt 0) { return New-Result $id 'Attention' ([string]$updates.Count) (($updates | ForEach-Object { $_.Title }) -join '; ') }
                return New-Result $id 'Unknown' '0' 'No pending security/critical updates in the local Windows Update cache. Check online in Windows Update for current coverage.'
            }
            finally { foreach ($com in @($result, $searcher, $session)) { if ($null -ne $com) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($com) } } }
        }
        'Isatap' {
            $state = [string](Get-NetIsatapConfiguration -ErrorAction Stop).State
            if ($state -notin @('Default', 'Automatic', 'Enabled', 'Disabled')) { throw 'Unrecognized ISATAP configuration.' }
            if ($state -eq 'Disabled') { return New-Result $id 'Good' $state $state }
            return New-Result $id 'Attention' $state $state $true
        }
        'Teredo' {
            $state = [string](Get-NetTeredoConfiguration -ErrorAction Stop).Type
            if ($state -notin @('Default', 'Disabled', 'Client', 'Enterpriseclient', 'Natawareclient', 'Server')) { throw 'Unrecognized Teredo configuration.' }
            if ($state -eq 'Disabled') { return New-Result $id 'Good' $state $state }
            return New-Result $id 'Attention' $state $state $true
        }
        'Firefox' {
            $paths = @(
                (Read-Value 'SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\firefox.exe' ''),
                (Read-Value 'SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\firefox.exe' ''),
                (Read-UserValue 'SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\firefox.exe' ''),
                (Join-Path $env:ProgramFiles 'Mozilla Firefox\firefox.exe'),
                (Join-Path ${env:ProgramFiles(x86)} 'Mozilla Firefox\firefox.exe')
            ) | Where-Object { $_ -and [IO.Path]::IsPathRooted($_) -and [IO.File]::Exists($_) }
            if (@($paths).Count -eq 0) { return New-Result $id 'NotApplicable' '' 'No registered or standard Firefox installation found.' }
            foreach ($path in $paths) {
                if (Test-Path -LiteralPath (Join-Path ([IO.Path]::GetDirectoryName($path)) 'distribution\policies.json')) {
                    return New-Result $id 'Unknown' '' 'Firefox has a policies.json file. Inspect effective policies at about:policies.'
                }
            }
            $policy = 'SOFTWARE\Policies\Mozilla\Firefox\Homepage'
            $url = Read-Value $policy 'URL'
            $locked = Read-Value $policy 'Locked'
            if ($null -eq $url -and $null -eq $locked) { $url = Read-UserValue $policy 'URL'; $locked = Read-UserValue $policy 'Locked' }
            $evidence = 'URL=' + $url + '; Locked=' + $locked
            if ($locked -eq 1 -and -not [string]::IsNullOrWhiteSpace($url)) { return New-Result $id 'Good' 'Enabled' $evidence }
            return New-Result $id 'Attention' 'Disabled' $evidence $true
        }
        default { throw 'Unsupported native security check.' }
    }
}

try {
    if ($null -ne $request.Fix) {
        $fix = $request.Fix
        $current = Read-Check $fix.Id
        if ($current.State -ne 'Attention' -or -not $current.CanFix -or $current.Evidence -cne $fix.Evidence -or $current.Current -cne $fix.Current) {
            throw 'Security evidence changed after scanning. Scan again.'
        }
        switch ($fix.Id) {
            'Antivirus' { Set-MpPreference -DisableRealtimeMonitoring $false -ErrorAction Stop }
            'Firewall' { Set-NetFirewallProfile -Profile Domain,Private,Public -Enabled True -ErrorAction Stop }
            'Isatap' { Set-NetIsatapConfiguration -State Disabled -ErrorAction Stop | Out-Null }
            'Teredo' { Set-NetTeredoConfiguration -Type Disabled -ErrorAction Stop | Out-Null }
            'Firefox' {
                $policy = 'SOFTWARE\Policies\Mozilla\Firefox\Homepage'
                $url = Read-Value $policy 'URL'
                if ($null -eq $url) { $url = Read-UserValue $policy 'URL' }
                if ([string]::IsNullOrWhiteSpace($url)) { $url = 'about:home' }
                $key = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey($policy)
                try {
                    $key.SetValue('URL', $url, [Microsoft.Win32.RegistryValueKind]::String)
                    $key.SetValue('Locked', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
                }
                finally { $key.Dispose() }
            }
            default { throw 'This security finding requires manual review.' }
        }
        $updated = Read-Check $fix.Id
        if ($updated.State -ne 'Good') { throw 'Windows did not confirm the change. Scan again to inspect partial changes.' }
        ConvertTo-Json -InputObject @($updated) -Depth 5 -Compress
    }
    else {
        $rows = @(foreach ($id in $request.Ids) {
            try { Read-Check $id }
            catch { New-Result $id 'Unknown' '' $_.Exception.Message }
        })
        ConvertTo-Json -InputObject $rows -Depth 5 -Compress
    }
}
catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
