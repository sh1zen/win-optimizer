param([Parameter(Mandatory = $true)][string]$ScriptPath)
$ErrorActionPreference = 'Stop'

# Replace only the Windows Update COM boundary; execute the bundled production script.
function Assert-True($condition, [string]$message) {
    if (-not $condition) { throw $message }
}
function New-TestCollection([object[]]$values) {
    $collection = [pscustomobject]@{ Values = $values; Count = $values.Count }
    $collection | Add-Member ScriptMethod Item { param($index) $this.Values[$index] }
    return $collection
}
function New-TestUpdate([string]$id, [object[]]$children = @(), [bool]$license = $true) {
    $offer = [pscustomobject]@{
        Identity = [pscustomobject]@{ UpdateID = $id }
        Title = 'Official test driver'; DriverProvider = 'Test'; DriverClass = 'Net'
        DriverHardwareID = 'PCI\TEST'; DriverVerDate = [datetime]'2026-01-01'; MaxDownloadSize = 4096
        EulaAccepted = $license; IsDownloaded = $true; BundledUpdates = (New-TestCollection $children)
    }
    $offer | Add-Member ScriptMethod CopyFromCache {
        param($destination, $extract)
        if ($this.BundledUpdates.Count -gt 0) { throw 'Parent bundles cannot be copied.' }
        [IO.File]::WriteAllText((Join-Path $destination 'driver.inf'), $this.Identity.UpdateID)
    }
    return $offer
}
function New-Object {
    param([string]$ComObject)
    if ($ComObject -eq 'Microsoft.Update.Session') { return $script:testSession }
    if ($ComObject -eq 'Microsoft.Update.UpdateColl') {
        $collection = [pscustomobject]@{}
        $collection | Add-Member ScriptMethod Add { param($update) $script:testDownloadOffer = $update; return 0 }
        return $collection
    }
    throw "Unexpected COM request: $ComObject"
}
$testSearcher = [pscustomobject]@{ ServerSelection = 0; Online = $false }
$testSearcher | Add-Member ScriptMethod Search {
    param($criteria)
    Assert-True ($this.ServerSelection -eq 2 -and $this.Online) 'Expected the public online Windows Update service.'
    Assert-True ($criteria -eq "IsInstalled=0 and IsHidden=0 and Type='Driver' and DeploymentAction=*") 'Expected an unrestricted deployment search for nonhidden, uninstalled drivers.'
    if ($script:testSearchFailure) { return [pscustomobject]@{ ResultCode = 4 } }
    return [pscustomobject]@{ ResultCode = 2; Updates = (New-TestCollection $script:testOffers) }
}
$script:testSession = [pscustomobject]@{ ClientApplicationID = '' }
$script:testSession | Add-Member ScriptMethod CreateUpdateSearcher { return $script:testSearcher }
$script:testSession | Add-Member ScriptMethod CreateUpdateDownloader {
    $downloader = [pscustomobject]@{ Updates = $null }
    $downloader | Add-Member ScriptMethod Download {
        $script:testDownloadOffer.IsDownloaded = $true
        return [pscustomobject]@{ ResultCode = 2 }
    }
    return $downloader
}
$testId = 'd4335ba6-fd4e-4081-9bb5-379f7fa346bc'
$testChildId = '5de1ae26-a3c1-4100-b126-35fa00000001'
$testOtherId = '5de1ae26-a3c1-4100-b126-35fa00000002'
$workspace = Join-Path (Split-Path -Parent $ScriptPath) 'payloads'

# Flat, nested and sibling bundles retain distinct payloads even with identical file names.
$script:testOffers = @(New-TestUpdate $testId @(
    (New-TestUpdate $testChildId @((New-TestUpdate $testOtherId))),
    (New-TestUpdate $testOtherId)))
$script:testOffers[0].IsDownloaded = $false
$result = (. $ScriptPath) | ConvertFrom-Json
Assert-True ($result.Updates.Count -eq 1 -and $result.Updates[0].PayloadError -eq '') 'Bundled offer must remain verifiable.'
Assert-True ([IO.File]::ReadAllText((Join-Path $workspace "$testId\0\0\driver.inf")) -eq $testOtherId) 'Nested payload missing.'
Assert-True ([IO.File]::ReadAllText((Join-Path $workspace "$testId\1\driver.inf")) -eq $testOtherId) 'Sibling payload missing.'

$script:testOffers = @((New-TestUpdate $testChildId), (New-TestUpdate $testOtherId -license $false))
$result = (. $ScriptPath) | ConvertFrom-Json
Assert-True ($result.Updates.Count -eq 2) 'A license restriction must preserve discovered offers.'
Assert-True (Test-Path -LiteralPath (Join-Path $workspace "$testChildId\driver.inf")) 'Flat payload missing.'
Assert-True ($result.Updates[1].PayloadError -like '*license*') 'License restriction must be reported.'
Assert-True (-not (Test-Path -LiteralPath (Join-Path $workspace "$testOtherId\driver.inf"))) 'Unaccepted license must not export payloads.'

$script:testOffers = @()
$result = (. $ScriptPath) | ConvertFrom-Json
Assert-True ($result.Updates.Count -eq 0) 'A successful empty search must return an empty collection.'
$script:testSearchFailure = $true
$testFailed = $false
try { . $ScriptPath | Out-Null } catch { $testFailed = $_.Exception.Message -like '*did not complete*' }
Assert-True $testFailed 'Failed searches must not return a successful empty result.'
Write-Output 'Driver update script scenarios passed.'
