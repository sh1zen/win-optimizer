# The caller supplies a private workspace. Only Microsoft's public update service is queried.
$session = New-Object -ComObject Microsoft.Update.Session
$session.ClientApplicationID = 'win-optimizer Driver Scan'
$searcher = $session.CreateUpdateSearcher()
$searcher.ServerSelection = 2 # ssWindowsUpdate; never substitute a configured third-party/WSUS source.
$searcher.Online = $true
# Include optional offers beyond WUA's implicit DeploymentAction='Installation' restriction.
$search = $searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Driver' and DeploymentAction=*")
if ([int]$search.ResultCode -ne 2) { throw 'Windows Update did not complete the driver search successfully.' }

# WUA requires CopyFromCache on individual bundled updates, not their parent offer.
function Export-DriverPayload {
    param($update, [string]$destination)
    if (-not $update.EulaAccepted) { throw 'Review this driver license in Windows Update before package verification.' }
    [void][IO.Directory]::CreateDirectory($destination)
    if ($update.BundledUpdates.Count -gt 0) {
        for ($childIndex = 0; $childIndex -lt $update.BundledUpdates.Count; $childIndex++) {
            Export-DriverPayload $update.BundledUpdates.Item($childIndex) (Join-Path $destination ([string]$childIndex))
        }
        return
    }
    $update.CopyFromCache($destination, $false)
}

function Download-DriverPayloads {
    param([object[]]$entries)
    if ($entries.Count -eq 0) { return }

    try {
        $collection = New-Object -ComObject Microsoft.Update.UpdateColl
        $requests = [Collections.Generic.List[object]]::new()
        foreach ($entry in $entries) {
            try {
                $position = $collection.Add($entry.Update)
                $requests.Add(@{ Position = $position; Offer = $entry.Offer })
            }
            catch { $entry.Offer.PayloadError = $_.Exception.Message }
        }
        if ($requests.Count -eq 0) { return }
        $downloader = $session.CreateUpdateDownloader()
        $downloader.Updates = $collection
        $download = $downloader.Download()
    }
    catch {
        # A job-level COM failure has no per-update results. Isolate its updates before reporting failures.
        if ($entries.Count -eq 1) { $entries[0].Offer.PayloadError = $_.Exception.Message }
        else {
            foreach ($entry in $entries) {
                if ($entry.Offer.PayloadError -eq '') { Download-DriverPayloads @($entry) }
            }
        }
        return
    }

    foreach ($request in $requests) {
        try {
            $result = $download.GetUpdateResult($request.Position)
            if ([int]$result.ResultCode -ne 2) {
                throw ('Driver package download failed: 0x{0:X8}' -f $result.HResult)
            }
        }
        catch { $request.Offer.PayloadError = $_.Exception.Message }
    }
}

$entries = [Collections.Generic.List[object]]::new()
$pending = [Collections.Generic.List[object]]::new()
for ($index = 0; $index -lt $search.Updates.Count; $index++) {
    $update = $search.Updates.Item($index)
    $offer = @{
        Id = ([Guid]$update.Identity.UpdateID).ToString('D')
        Title = [string]$update.Title
        Provider = [string]$update.DriverProvider
        ClassName = [string]$update.DriverClass
        HardwareId = [string]$update.DriverHardwareID
        Date = ([DateTime]$update.DriverVerDate).ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
        SizeBytes = [long]$update.MaxDownloadSize
        PayloadError = ''
    }
    $entry = @{ Update = $update; Offer = $offer }
    $entries.Add($entry)
    try {
        if (-not $update.EulaAccepted) { throw 'Review this driver license in Windows Update before package verification.' }
        if (-not $update.IsDownloaded) { $pending.Add($entry) }
    }
    catch { $offer.PayloadError = $_.Exception.Message }
}
Download-DriverPayloads $pending.ToArray()

$offers = [Collections.Generic.List[object]]::new()
foreach ($entry in $entries) {
    $offer = $entry.Offer
    if ($offer.PayloadError -eq '') {
        try { Export-DriverPayload $entry.Update (Join-Path $workspace $offer.Id) }
        catch { $offer.PayloadError = $_.Exception.Message }
    }
    $offers.Add($offer)
}
@{ Updates = @($offers.ToArray()) } | ConvertTo-Json -Depth 5 -Compress
