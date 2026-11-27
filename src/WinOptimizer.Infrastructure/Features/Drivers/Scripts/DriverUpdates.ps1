# The caller supplies a private workspace. Only Microsoft's public update service is queried.
$session = New-Object -ComObject Microsoft.Update.Session
$session.ClientApplicationID = 'win-optimizer Driver Scan'
$searcher = $session.CreateUpdateSearcher()
$searcher.ServerSelection = 2 # ssWindowsUpdate; never substitute a configured third-party/WSUS source.
$searcher.Online = $true
# Include optional offers beyond WUA's implicit DeploymentAction='Installation' restriction.
$search = $searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Driver' and DeploymentAction=*")
if ([int]$search.ResultCode -notin @(2, 3)) { throw 'Windows Update did not complete the driver search successfully.' }

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

function Test-DriverLicense($update) {
    if (-not $update.EulaAccepted) { return $false }
    for ($childIndex = 0; $childIndex -lt $update.BundledUpdates.Count; $childIndex++) {
        if (-not (Test-DriverLicense $update.BundledUpdates.Item($childIndex))) { return $false }
    }
    return $true
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
    $id = ([Guid]$update.Identity.UpdateID).ToString('D')
    $revision = [int]$update.Identity.RevisionNumber
    if ($requestedUpdate -and ($id -ne $requestedUpdate -or $revision -ne $requestedRevision)) { continue }
    $offer = @{
        Id = $id
        Revision = $revision
        Title = [string]$update.Title
        Provider = [string]$update.DriverProvider
        Manufacturer = [string]$update.DriverManufacturer
        ClassName = [string]$update.DriverClass
        HardwareId = [string]$update.DriverHardwareID
        Date = $(if ($update.DriverVerDate) { ([DateTime]$update.DriverVerDate).ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture) } else { '' })
        ReleaseDate = $(if ($update.LastDeploymentChangeTime) { ([DateTime]$update.LastDeploymentChangeTime).ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture) } else { '' })
        SizeBytes = [long]$update.MaxDownloadSize
        PayloadError = ''
        LicenseRequired = $false
        VerificationSkipped = $false
    }
    $entry = @{ Update = $update; Offer = $offer }
    $entries.Add($entry)
    try {
        if (-not (Test-DriverLicense $update)) {
            $offer.LicenseRequired = $true
            throw 'Review this driver license in Windows Update before package verification.'
        }
        if ($requestedUpdate -and $offer.SizeBytes -gt 2147483648) {
            throw 'This Windows Update package exceeds the 2 GiB acquisition limit.'
        }
        if ($null -ne $verificationLimit -and $offer.SizeBytes -gt 268435456) {
            $offer.VerificationSkipped = $true
        }
    }
    catch { $offer.PayloadError = $_.Exception.Message }
}

$eligible = @($entries | Where-Object { $_.Offer.PayloadError -eq '' -and -not $_.Offer.VerificationSkipped })
if ($null -ne $verificationLimit) {
    # Prefer the newest driver date for each target before spending a slot on another release for that target.
    $targets = @{}
    $position = 0
    $ranked = foreach ($entry in ($eligible | Sort-Object -Property { $_.Offer.Date }, { $_.Offer.ReleaseDate } -Descending)) {
        $offer = $entry.Offer
        $target = if ($offer.HardwareId) { $offer.ClassName + '|' + $offer.HardwareId } else { $offer.Id }
        $turn = [int]$targets[$target]
        $targets[$target] = $turn + 1
        @{ Entry = $entry; Turn = $turn; Position = $position }
        $position++
    }
    $scheduled = 0
    foreach ($rank in ($ranked | Sort-Object -Property { $_.Turn }, { $_.Position })) {
        $rank.Entry.Offer.VerificationSkipped = ($scheduled -ge $verificationLimit)
        $scheduled++
    }
}
foreach ($entry in $eligible) {
    if (-not $entry.Offer.VerificationSkipped -and -not $entry.Update.IsDownloaded) { $pending.Add($entry) }
}
Download-DriverPayloads $pending.ToArray()

$offers = [Collections.Generic.List[object]]::new()
foreach ($entry in $entries) {
    $offer = $entry.Offer
    if ($offer.PayloadError -eq '' -and -not $offer.VerificationSkipped) {
        try { Export-DriverPayload $entry.Update (Join-Path $workspace $offer.Id) }
        catch { $offer.PayloadError = $_.Exception.Message }
    }
    $offers.Add($offer)
}
@{ Updates = @($offers.ToArray()); Partial = ([int]$search.ResultCode -eq 3) } | ConvertTo-Json -Depth 5 -Compress
