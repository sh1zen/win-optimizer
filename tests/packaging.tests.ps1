# Checks production signing decisions without building, downloading or reading certificates.
$ErrorActionPreference = 'Stop'
$packageScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../extras/build-and-package.ps1'))
$tokens = $null; $parseErrors = $null
$packageAst = [Management.Automation.Language.Parser]::ParseFile($packageScript, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
$signingDecision = $packageAst.EndBlock.Statements | Where-Object {
    $_ -is [Management.Automation.Language.IfStatementAst] -and
    $_.Extent.Text -match 'Do you want to sign the application'
}
if (@($signingDecision).Count -ne 1) { throw 'Expected one production signing decision.' }
$signingBlock = [scriptblock]::Create($signingDecision.Extent.Text)
$passed = 0

foreach ($scenario in @(
    @{ Skip = $true; Sign = $false; Answer = ''; Prompts = 0; Certificates = 0; Signed = $false },
    @{ Skip = $false; Sign = $false; Answer = 'n'; Prompts = 1; Certificates = 0; Signed = $false },
    @{ Skip = $false; Sign = $false; Answer = 'y'; Prompts = 1; Certificates = 1; Signed = $true },
    @{ Skip = $false; Sign = $true; Answer = ''; Prompts = 0; Certificates = 1; Signed = $true }
)) {
    $state = @{ Prompts = 0; Certificates = 0 }
    $signed = & {
        param($flow, $scenario, $state)
        # These are the interactive and certificate-store boundaries of the real branch.
        function Read-Host {
            param($Prompt)
            $state.Prompts++
            if ($scenario.Prompts -eq 0) { throw 'Unexpected interactive signing prompt.' }
            return $scenario.Answer
        }
        function Get-SigningCertificate {
            param($Subject, $Thumbprint)
            $state.Certificates++
            return [pscustomobject]@{ Subject = 'Test certificate'; Thumbprint = 'TEST' }
        }
        function Write-Host { param($Object, $ForegroundColor) }
        $SkipSigning = $scenario.Skip; $SignApplication = $scenario.Sign
        $CertificateSubject = ''; $CertificateThumbprint = ''
        $certificate = $null; $shouldSign = $false
        . $flow
        return $shouldSign
    } $signingBlock $scenario $state
    if ($signed -ne $scenario.Signed -or $state.Prompts -ne $scenario.Prompts -or
        $state.Certificates -ne $scenario.Certificates) { throw 'Unexpected signing behavior.' }
    $passed++
}

# Run invalid combinations through the real entry point; it must reject them before side effects.
foreach ($conflict in @(
    @{ SignApplication = $true },
    @{ CertificateSubject = 'Test certificate' },
    @{ CertificateThumbprint = 'TEST' }
)) {
    $failure = ''
    try { & $packageScript -SkipSigning @conflict }
    catch { $failure = $_.Exception.Message }
    if ($failure -notmatch '\-SkipSigning cannot be combined') {
        throw 'Contradictory signing options were not rejected at the package boundary.'
    }
    $passed++
}

$compilerFailure = ''
try { & $packageScript -SkipSigning -InnoCompiler (Join-Path $PSScriptRoot 'missing-ISCC.exe') }
catch { $compilerFailure = $_.Exception.Message }
if ($compilerFailure -notmatch 'Inno Setup 6 compiler not found') {
    throw 'A missing compiler must fail before build or metadata changes.'
}
$passed++

# An explicitly requested signed package cannot silently become unsigned.
$missingCertificateFailure = & {
    param($flow)
    function Get-SigningCertificate { param($Subject, $Thumbprint) return $null }
    function Write-Host { param($Object, $ForegroundColor) }
    function Read-Host { param($Prompt) throw 'Explicit signing must not prompt.' }
    $SkipSigning = $false; $SignApplication = $true
    $CertificateSubject = ''; $CertificateThumbprint = 'TEST'
    $certificate = $null; $shouldSign = $false
    try { . $flow; return '' } catch { return $_.Exception.Message }
} $signingBlock
if ($missingCertificateFailure -notmatch 'No code-signing certificate') {
    throw 'Explicit signing did not fail when its certificate was unavailable.'
}
$passed++

Write-Host "Passed $passed packaging signing scenarios."
