# The caller supplies a private workspace. Only Microsoft's public update service is queried.
$session = New-Object -ComObject Microsoft.Update.Session
$session.ClientApplicationID = 'win-optimizer Driver Scan'
$searcher = $session.CreateUpdateSearcher()
$searcher.ServerSelection = 2 # ssWindowsUpdate; never substitute a configured third-party/WSUS source.
$searcher.Online = $true
$search = $searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Driver'")
if ([int]$search.ResultCode -ne 2) { throw 'Windows Update did not complete the driver search successfully.' }
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
        [void][IO.Directory]::CreateDirectory($destination)
        $update.CopyFromCache($destination, $false)
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
