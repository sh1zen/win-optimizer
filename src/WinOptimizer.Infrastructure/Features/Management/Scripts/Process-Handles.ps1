param([Parameter(Mandatory=$true)][string]$RequestBase64)
# Inspect file references only. Handle-close and driver-install commands are never requested.
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
$request=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($RequestBase64)) | ConvertFrom-Json
try {
    if (-not $request.AcceptLicense) { throw 'Accept the Sysinternals license before loading open files.' }
    $targetPid=[int]$request.Pid
    if ($targetPid -le 0) { throw 'Invalid process ID.' }
    if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Microsoft Handle requires administrator permissions.'
    }
    $tool=Open-VerifiedSysinternalsTool -Tool Handle -AcceptLicense $true -Directory $request.ToolDirectory
    try {
        $errors=Join-Path $request.ToolDirectory ([Guid]::NewGuid().ToString('N') + '.stderr')
        try {
            $previousPreference=$ErrorActionPreference
            try {
                $ErrorActionPreference='Continue'
                $lines=@(& $tool.Path -accepteula -nobanner -v -p ([string]$targetPid) 2> $errors)
                $code=$LASTEXITCODE
            } finally { $ErrorActionPreference=$previousPreference }
            $diagnostic=if ([IO.File]::Exists($errors)) { [IO.File]::ReadAllText($errors).Trim() } else { '' }
            if ($code -ne 0) { throw "Microsoft Handle exited with code ${code}. $diagnostic" }
            @{Csv=(($lines -join "`n").Trim()); Diagnostic=$diagnostic} | ConvertTo-Json -Compress
        } finally { if ([IO.File]::Exists($errors)) { [IO.File]::Delete($errors) } }
    } finally { $tool.Lock.Dispose() }
} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
