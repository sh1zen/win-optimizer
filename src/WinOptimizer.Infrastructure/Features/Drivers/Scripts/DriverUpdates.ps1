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

$offers = [Collections.Generic.List[object]]::new()
for ($index = 0; $index -lt $search.Updates.Count; $index++) {
    $update = $search.Updates.Item($index)
    $id = ([Guid]$update.Identity.UpdateID).ToString('D')
    $payloadError = ''
    try {
        if (-not $update.EulaAccepted) { throw 'Review this driver license in Windows Update before package verification.' }
        if (-not $update.IsDownloaded) {
            $collection = New-Object -ComObject Microsoft.Update.UpdateColl
            [void]$collection.Add($update)
            $downloader = $session.CreateUpdateDownloader()
            $downloader.Updates = $collection
            $download = $downloader.Download()
            if ([int]$download.ResultCode -ne 2) { throw ('Driver package download failed: 0x{0:X8}' -f $download.GetUpdateResult(0).HResult) }
        }
        $destination = Join-Path $workspace $id
        Export-DriverPayload $update $destination
    }
    catch { $payloadError = $_.Exception.Message }
    $offers.Add(@{
        Id = $id
        Title = [string]$update.Title
        Provider = [string]$update.DriverProvider
        ClassName = [string]$update.DriverClass
        HardwareId = [string]$update.DriverHardwareID
        Date = ([DateTime]$update.DriverVerDate).ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
        SizeBytes = [long]$update.MaxDownloadSize
        PayloadError = $payloadError
    })
}
@{ Updates = @($offers.ToArray()) } | ConvertTo-Json -Depth 5 -Compress
