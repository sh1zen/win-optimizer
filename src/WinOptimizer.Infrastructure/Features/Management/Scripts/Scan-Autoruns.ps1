param([Parameter(Mandatory=$true)][string]$RequestBase64)
# An explicit advanced scan acquires the official, signed Microsoft tool on demand.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
$request = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($RequestBase64)) | ConvertFrom-Json

try {
    $toolInfo=Open-VerifiedSysinternalsTool -Tool Autoruns -AcceptLicense $request.AcceptLicense -Directory $request.ToolDirectory
    $tool=$toolInfo.Path
    $directory=[IO.Path]::GetDirectoryName($tool)
    try {
    $admin=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    $arguments=@('-accepteula','-nobanner','-a','*','-c','-s','-t')
    if ($admin) { $arguments += '*' }
    # No VirusTotal flags, file upload, Microsoft filter or unsigned-only filter.
    $errors = Join-Path $directory ([Guid]::NewGuid().ToString('N') + '.stderr')
    try {
        $previousPreference=$ErrorActionPreference
        try {
            $ErrorActionPreference='Continue'
            $lines = @(& $tool @arguments 2> $errors)
            $code=$LASTEXITCODE
        } finally { $ErrorActionPreference=$previousPreference }
        $nativeErrors=if ([IO.File]::Exists($errors)) { [IO.File]::ReadAllText($errors) } else { '' }
        if ($code -ne 0) { throw "Autorunsc exited with code ${code}. $nativeErrors" }
        $text=($lines -join "`n").Trim().TrimStart([char]0xFEFF)
        $warnings=New-Object 'Collections.Generic.List[string]'
        if (-not $admin) { $warnings.Add('Run as administrator to inspect all accessible profiles and protected loading points.') }
        if ($nativeErrors.Trim()) { $warnings.Add($nativeErrors.Trim()) }
        $coverage=if ($admin) { 'Microsoft Autorunsc: all categories, all accessible user profiles, including Microsoft entries. Configured loading points, not a live process list.' }
            else { 'Microsoft Autorunsc: all categories accessible to the current account, including Microsoft entries. Configured loading points, not a live process list.' }
        @{Csv=$text; Warnings=@($warnings.ToArray()); Coverage=$coverage} | ConvertTo-Json -Depth 8 -Compress
    } finally { if ([IO.File]::Exists($errors)) { [IO.File]::Delete($errors) } }
    } finally { $toolInfo.Lock.Dispose() }
} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
