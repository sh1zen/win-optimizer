# Shared acquisition and resource lifetime for the two explicitly requested Microsoft tools.
function Assert-MicrosoftSignature([string]$path) {
    $signature=Get-AuthenticodeSignature -LiteralPath $path -ErrorAction Stop
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -notmatch '(?:^|,\s*)O=Microsoft Corporation(?:,|$)') {
        throw 'The Sysinternals executable does not have a valid Microsoft signature. Remove the cached tool and retry.'
    }
}
function Assert-PlainDirectory([string]$path) {
    $current=New-Object IO.DirectoryInfo($path)
    while ($null -ne $current) {
        if ($current.Exists -and ($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'The tool cache contains a redirected directory.' }
        $current=$current.Parent
    }
}
function Open-VerifiedSysinternalsTool([ValidateSet('Autoruns','Handle')][string]$Tool, [bool]$AcceptLicense, [string]$Directory) {
    if (-not $AcceptLicense) { throw 'Accept the Sysinternals license before requesting this tool.' }
    $executable=if ($Tool -eq 'Autoruns') { 'autorunsc64.exe' } else { 'handle64.exe' }
    $expected=Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) ('win-optimizer\Tools\' + $Tool.ToLowerInvariant())
    $directory=[IO.Path]::GetFullPath($Directory)
    if ($directory -ine $expected) { throw 'Invalid Sysinternals cache directory.' }
    Assert-PlainDirectory $directory
    [void][IO.Directory]::CreateDirectory($directory)
    $path=Join-Path $directory $executable
    if (-not [IO.File]::Exists($path)) {
        $stage=Join-Path $directory ([Guid]::NewGuid().ToString('N'))
        [void][IO.Directory]::CreateDirectory($stage)
        try {
            $archive=Join-Path $stage ($Tool + '.zip')
            [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
            Invoke-WebRequest -UseBasicParsing -Uri ('https://download.sysinternals.com/files/' + $Tool + '.zip') -OutFile $archive -TimeoutSec 90 -ErrorAction Stop
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $zip=[IO.Compression.ZipFile]::OpenRead($archive)
            try {
                $entry=$zip.GetEntry($executable)
                if ($null -eq $entry -or $entry.Length -le 0 -or $entry.Length -gt 20MB) { throw 'The Microsoft download does not contain the expected executable.' }
                $candidate=Join-Path $stage $executable
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$candidate,$false)
            } finally { $zip.Dispose() }
            Assert-MicrosoftSignature $candidate
            [IO.File]::Move($candidate,$path)
        } finally {
            foreach ($name in @(($Tool + '.zip'),$executable)) {
                $temporary=Join-Path $stage $name
                if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
            }
            [IO.Directory]::Delete($stage,$false)
        }
    }
    if (([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'The cached executable is a filesystem link.' }
    $lock=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try { Assert-MicrosoftSignature $path; return [pscustomobject]@{Path=$path; Lock=$lock} }
    catch { $lock.Dispose(); throw }
}
