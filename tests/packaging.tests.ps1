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

# Run the production compiler resource lifetime with only the external compiler faked.
$compilation = $packageAst.EndBlock.Statements | Where-Object {
    $_ -is [Management.Automation.Language.TryStatementAst] -and
    $_.Extent.Text -match 'Installer compilation failed with exit code'
}
$temporaryStart = $packageAst.EndBlock.Statements | Where-Object {
    $_ -is [Management.Automation.Language.AssignmentStatementAst] -and
    $_.Left.Extent.Text -eq '$temporaryDirectory'
}
if (@($compilation).Count -ne 1 -or @($temporaryStart).Count -ne 1) { throw 'Expected one installer compilation lifetime.' }
$compilationBlock = [scriptblock]::Create($packageAst.Extent.Text.Substring(
    $temporaryStart.Extent.StartOffset, $compilation.Extent.EndOffset - $temporaryStart.Extent.StartOffset))
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixtureParent = Join-Path $repoRoot 'build'
$fixture = Join-Path $fixtureParent ('packaging-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $fixture -Force
$savedExitCode = $LASTEXITCODE
$seenInputs = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
try {
    # Keep signing paths as literal tool arguments, including spaces and apostrophes.
    $signer = Join-Path $fixture "signtool's fixture.ps1"
    Set-Content -LiteralPath $signer -Value @'
$args | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'signing-arguments.json')
$global:LASTEXITCODE = [int](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'signing-exit.txt') -Raw)
'@
    $fileToSign = Join-Path $fixture "app's output.exe"
    Set-Content -LiteralPath $fileToSign -Value 'fixture'
    $signingFunction = $packageAst.FindAll({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Set-FileSignature'
    }, $false)
    if (@($signingFunction).Count -ne 1) { throw 'Expected one production signing function.' }
    $signingDefinition = [scriptblock]::Create($signingFunction.Extent.Text)
    $key = [Security.Cryptography.RSA]::Create()
    $testCertificate = $null
    try {
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
            'CN=Build fixture', $key, [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $testCertificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-1), [DateTimeOffset]::UtcNow.AddMinutes(5))
        foreach ($exitCode in @(0, 7)) {
            Set-Content -LiteralPath (Join-Path $fixture 'signing-exit.txt') -Value $exitCode
            $signedResult = & {
                # Fake only Windows SDK discovery and its executable boundary.
                function Test-Path {
                    param([string]$Path)
                    if ($Path -eq 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe') { return $false }
                    Microsoft.PowerShell.Management\Test-Path -LiteralPath $Path
                }
                function Get-ChildItem {
                    param($Path, $ErrorAction)
                    return ,@(Microsoft.PowerShell.Management\Get-Item -LiteralPath $signer)
                }
                . $signingDefinition
                Set-FileSignature -FilePath $fileToSign -Certificate $testCertificate
            }
            $arguments = Get-Content -LiteralPath (Join-Path $fixture 'signing-arguments.json') -Raw | ConvertFrom-Json
            if ($signedResult -ne ($exitCode -eq 0) -or $arguments.Count -ne 10 -or
                $arguments[-1] -cne $fileToSign -or $arguments[-2] -cne $testCertificate.Thumbprint) {
                throw 'Signing did not preserve literal arguments or report the native exit correctly.'
            }
            $passed++
        }
    } finally {
        if ($testCertificate) { $testCertificate.Dispose() }
        $key.Dispose()
    }
    foreach ($signed in @($false, $true)) {
        foreach ($outcome in @('Success', 'ExitFailure', 'Exception')) {
            $state = @{ Script = ''; Batch = ''; Invoked = $false }
            $failure = & {
                param($flow, $state, $signed, $outcome)
                function Invoke-TestCompiler {
                    $state.Invoked = $true
                    $state.Script = [string]$args[-1]
                    if ((Get-Content -LiteralPath $state.Script -Raw).Trim() -ne 'fixture content') { throw 'Missing compiler input.' }
                    if ($signed) {
                        if ($args.Count -ne 2 -or $args[0] -notlike '/Smysigntool=* $f') { throw 'Missing Inno signing command.' }
                        $state.Batch = $args[0].Substring('/Smysigntool='.Length) -replace ' \$f$', ''
                        if ((Get-Content -LiteralPath $state.Batch -Raw) -notlike '* /sha1 TEST %1*') { throw 'Missing signing batch input.' }
                    } elseif ($args.Count -ne 1) { throw 'Unsigned compilation received signing arguments.' }
                    if ($outcome -eq 'Exception') { throw 'Fixture compiler exception' }
                    $global:LASTEXITCODE = $(if ($outcome -eq 'ExitFailure') { 42 } else { 0 })
                }
                $solutionDir = $fixture
                $innoCompiler = 'Invoke-TestCompiler'
                $innoContent = 'fixture content'
                $shouldSign = $signed
                $certificate = $(if ($signed) { [pscustomobject]@{ Thumbprint = 'TEST' } } else { $null })
                try { . $flow; return '' } catch { return $_.Exception.Message }
            } $compilationBlock $state $signed $outcome
            $expected = switch ($outcome) {
                'Success' { '' }
                'ExitFailure' { 'Installer compilation failed with exit code 42.' }
                'Exception' { 'Fixture compiler exception' }
            }
            if ($failure -ne $expected -or -not $state.Invoked) { throw "Unexpected compilation result: $failure" }
            if (-not $seenInputs.Add($state.Script)) { throw 'Compilations reused temporary inputs.' }
            foreach ($inputFile in @($state.Script, $state.Batch) | Where-Object { $_ }) {
                if (Test-Path -LiteralPath $inputFile) { throw "Compiler input was left behind: $inputFile" }
            }
            $passed++
        }
    }
    # Simulate filesystem link metadata at the OS boundary before any fixture writes.
    $link = Join-Path $fixture 'build/temp'
    $sentinel = Join-Path $link 'unrelated.txt'
    Set-Content -LiteralPath $sentinel -Value 'preserve'
    $failure = & {
        function Get-Item {
            param([string]$LiteralPath)
            if ($LiteralPath -eq $link) { return [pscustomobject]@{ Attributes = [IO.FileAttributes]::ReparsePoint } }
            Microsoft.PowerShell.Management\Get-Item -LiteralPath $LiteralPath
        }
        $solutionDir = $fixture
        try { . $compilationBlock; return '' } catch { return $_.Exception.Message }
    }
    if ($failure -notmatch 'must not be a filesystem link' -or
        @(Get-ChildItem -LiteralPath $link -Force).Count -ne 1 -or
        (Get-Content -LiteralPath $sentinel -Raw).Trim() -ne 'preserve') {
        throw 'Linked packaging temporary directory was not rejected before writes.'
    }
    $passed++
} finally {
    $global:LASTEXITCODE = $savedExitCode
    $cleanupPath = [IO.Path]::GetFullPath($fixture)
    if (-not $cleanupPath.StartsWith($fixtureParent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Unsafe packaging fixture cleanup path.'
    }
    Remove-Item -LiteralPath $cleanupPath -Recurse -Force
}
Write-Host "Passed $passed packaging signing and temporary-file scenarios."
