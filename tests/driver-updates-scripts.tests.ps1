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
        Identity = [pscustomobject]@{ UpdateID = $id; RevisionNumber = 7 }
        Title = 'Official test driver'; DriverProvider = 'Test'; DriverClass = 'Net'
        DriverHardwareID = 'PCI\TEST'; DriverVerDate = [datetime]'2026-01-01'; MaxDownloadSize = 4096
        EulaAccepted = $license; IsDownloaded = $true; BundledUpdates = (New-TestCollection $children)
        DownloadCode = 2; DownloadHResult = 0
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
        $collection = [pscustomobject]@{ Values = [Collections.Generic.List[object]]::new() }
        $collection | Add-Member ScriptMethod Add { param($update) $this.Values.Add($update); return $this.Values.Count - 1 }
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
        $script:testDownloadJobs.Add($this.Updates.Values.Count)
        if ($script:testRejectBatch -and $this.Updates.Values.Count -gt 1) { throw 'Test download job rejected.' }
        $results = foreach ($offer in $this.Updates.Values) {
            Assert-True ($offer.EulaAccepted -and -not $offer.IsDownloaded) 'Only uncached, licensed offers belong in a download job.'
            if ($offer.DownloadCode -eq 2) { $offer.IsDownloaded = $true }
            [pscustomobject]@{ ResultCode = $offer.DownloadCode; HResult = $offer.DownloadHResult }
        }
        $download = [pscustomobject]@{ Results = @($results) }
        $download | Add-Member ScriptMethod GetUpdateResult { param($position) $this.Results[$position] }
        return $download
    }
    return $downloader
}
$testId = 'd4335ba6-fd4e-4081-9bb5-379f7fa346bc'
$testChildId = '5de1ae26-a3c1-4100-b126-35fa00000001'
$testOtherId = '5de1ae26-a3c1-4100-b126-35fa00000002'
$workspace = Join-Path (Split-Path -Parent $ScriptPath) 'payloads'
$script:testDownloadJobs = [Collections.Generic.List[int]]::new()

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

$workspace = Join-Path $workspace 'multiple-offers'
$script:testDownloadJobs.Clear()
$script:testOffers = @((New-TestUpdate $testChildId), (New-TestUpdate $testOtherId), (New-TestUpdate $testId -license $false))
foreach ($offer in $script:testOffers) { $offer.IsDownloaded = $false }
$script:testOffers[1].DownloadCode = 4
$script:testOffers[1].DownloadHResult = 0x80240022
$result = (. $ScriptPath) | ConvertFrom-Json
Assert-True (($script:testDownloadJobs -join ',') -eq '2') 'Uncached licensed offers should share one download job.'
Assert-True ($result.Updates[0].Id -eq $testChildId -and $result.Updates[0].PayloadError -eq '') 'A successful offer must retain its position and be exported.'
Assert-True ($result.Updates[1].Id -eq $testOtherId -and $result.Updates[1].PayloadError -like '*80240022*') 'A failed offer must retain its own HRESULT.'
Assert-True (Test-Path -LiteralPath (Join-Path $workspace "$testChildId\driver.inf")) 'Successful payload missing from partial batch.'
Assert-True (-not (Test-Path -LiteralPath (Join-Path $workspace "$testOtherId\driver.inf"))) 'Failed payload must not be exported.'
Assert-True ($result.Updates[2].PayloadError -like '*license*') 'An unlicensed offer must keep its original diagnostic.'

$workspace = Join-Path $workspace 'isolated-jobs'
$script:testDownloadJobs.Clear()
$script:testRejectBatch = $true
foreach ($offer in $script:testOffers) { $offer.IsDownloaded = $false }
$result = (. $ScriptPath) | ConvertFrom-Json
Assert-True (($script:testDownloadJobs -join ',') -eq '2,1,1') 'A rejected job must isolate each eligible offer.'
Assert-True ($result.Updates[0].PayloadError -eq '' -and $result.Updates[1].PayloadError -like '*80240022*') 'Isolated jobs must report individual results.'

$workspace = Join-Path $workspace 'selected-revision'
$script:testOffers = @((New-TestUpdate $testChildId), (New-TestUpdate $testId))
$requestedUpdate = $testId
$requestedRevision = 6
$result = (. $ScriptPath) | ConvertFrom-Json
Assert-True ($result.Updates.Count -eq 0) 'A different revision must not be downloaded.'
$requestedRevision = 7
$result = (. $ScriptPath) | ConvertFrom-Json
Assert-True ($result.Updates.Count -eq 1 -and $result.Updates[0].Revision -eq 7 -and $result.Updates[0].Id -eq $testId) 'Manual acquisition must use the exact selected revision.'
Assert-True (Test-Path -LiteralPath (Join-Path $workspace "$testId\driver.inf")) 'The selected revision was not exported.'
$requestedUpdate = $null
$verificationLimit = 0
$workspace = Join-Path $workspace 'metadata-only'
$result = (. $ScriptPath) | ConvertFrom-Json
Assert-True ($result.Updates.Count -eq 2 -and $result.Updates[0].VerificationSkipped -and $result.Updates[1].VerificationSkipped) 'Metadata-only discovery must retain offers.'
Assert-True (-not (Test-Path -LiteralPath $workspace)) 'Metadata-only discovery must skip all exports.'

$workspace = Join-Path $workspace 'prioritized'
$verificationLimit = 2
$script:testOffers = @((New-TestUpdate $testChildId), (New-TestUpdate $testOtherId), (New-TestUpdate $testId))
$script:testOffers[0].DriverVerDate = [datetime]'2026-03-02'
$script:testOffers[1].DriverVerDate = [datetime]'2026-03-03'
$script:testOffers[2].DriverHardwareID = 'PCI\OTHER'
$result = (. $ScriptPath) | ConvertFrom-Json
Assert-True $result.Updates[0].VerificationSkipped 'An earlier offer for the same device must yield its slot.'
Assert-True (-not $result.Updates[1].VerificationSkipped -and -not $result.Updates[2].VerificationSkipped) 'The newest offer per device should be verified first.'
Assert-True (Test-Path -LiteralPath (Join-Path $workspace "$testOtherId\driver.inf")) 'The newest release was not exported.'
Assert-True (Test-Path -LiteralPath (Join-Path $workspace "$testId\driver.inf")) 'The second device did not get a verification slot.'
$verificationLimit = $null

$script:testOffers = @()
$result = (. $ScriptPath) | ConvertFrom-Json
Assert-True ($result.Updates.Count -eq 0) 'A successful empty search must return an empty collection.'
$script:testSearchFailure = $true
$testFailed = $false
try { . $ScriptPath | Out-Null } catch { $testFailed = $_.Exception.Message -like '*did not complete*' }
Assert-True $testFailed 'Failed searches must not return a successful empty result.'
Write-Output 'Driver update script scenarios passed.'
