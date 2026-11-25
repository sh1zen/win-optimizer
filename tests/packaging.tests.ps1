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
$applicationSigningStep = $packageAst.EndBlock.Statements | Where-Object {
    $_ -is [Management.Automation.Language.IfStatementAst] -and
    $_.Extent.Text -match 'Set-ApplicationSignatures -PublishDirectory'
}
if (@($applicationSigningStep).Count -ne 1) { throw 'Expected one production payload signing step.' }
$applicationSigningBlock = [scriptblock]::Create($applicationSigningStep.Extent.Text)
$installerVerification = $packageAst.EndBlock.Statements | Where-Object {
    $_ -is [Management.Automation.Language.IfStatementAst] -and
    $_.Extent.Text -match 'The final installer signature could not be verified'
}
if (@($installerVerification).Count -ne 1) { throw 'Expected one final installer signature check.' }
$installerVerificationBlock = [scriptblock]::Create($installerVerification.Extent.Text)
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
    @{ SignPackagePayload = $true; SignApplication = $true; CertificateThumbprint = ('1' * 40) },
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

foreach ($options in @(@{ SignPackagePayload = $true }, @{ SignPackagePayload = $true; SignApplication = $true })) {
    $failure = ''
    try { & $packageScript @options }
    catch { $failure = $_.Exception.Message }
    if ($failure -notmatch 'requires -SignApplication and a 40-digit certificate thumbprint') {
        throw 'Incomplete Store signing options were not rejected before packaging.'
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
    # Keep signing paths literal, including spaces, apostrophes and wildcard characters.
    $signer = Join-Path $fixture "signtool's fixture.ps1"
    Set-Content -LiteralPath $signer -Value @'
$args | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'signing-arguments.json')
$global:LASTEXITCODE = [int](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'signing-exit.txt') -Raw)
'@
    $fileToSign = Join-Path $fixture "app's [output].exe"
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
                . $signingDefinition
                Set-FileSignature -FilePath $fileToSign -Certificate $testCertificate -SignToolPath $signer
            }
            $arguments = Get-Content -LiteralPath (Join-Path $fixture 'signing-arguments.json') -Raw | ConvertFrom-Json
            if ($signedResult -ne ($exitCode -eq 0) -or $arguments.Count -ne 10 -or
                $arguments[-1] -cne $fileToSign -or $arguments[-2] -cne $testCertificate.Thumbprint) {
                throw 'Signing did not preserve literal arguments or report the native exit correctly.'
            }
            $passed++
        }

        # Run the production payload signer with a fake SignTool and PE headers in real files.
        $payloadSigner = Join-Path $fixture 'payload-signer.ps1'
        Set-Content -LiteralPath $payloadSigner -Value @'
$command = $args[0]
$fileName = [IO.Path]::GetFileName($args[-1])
Add-Content -LiteralPath (Join-Path $PSScriptRoot 'payload-signing.log') -Value "$command $fileName"
$rejectFile = Join-Path $PSScriptRoot 'reject-signing.txt'
if ($command -eq 'sign' -and (Test-Path -LiteralPath $rejectFile) -and
    $fileName -eq (Get-Content -LiteralPath $rejectFile)) {
    $global:LASTEXITCODE = 7
    return
}
if ($command -eq 'sign' -and $fileName -ne 'unverifiable.exe') { Set-Content -LiteralPath "$($args[-1]).signed" -Value 'signed' }
$global:LASTEXITCODE = if ($command -eq 'sign' -or (Test-Path -LiteralPath "$($args[-1]).signed")) { 0 } else { 1 }
'@
        $payload = Join-Path $fixture 'publish'
        $nested = Join-Path $payload 'nested'
        $null = New-Item -ItemType Directory -Path $nested -Force
        $pe = [byte[]]::new(68)
        $pe[0] = 0x4D; $pe[1] = 0x5A; $pe[60] = 64; $pe[64] = 0x50; $pe[65] = 0x45
        foreach ($name in @('third-party.exe', 'DirectML.dll', 'nested\library.winmd')) {
            [IO.File]::WriteAllBytes((Join-Path $payload $name), $pe)
        }
        Set-Content -LiteralPath (Join-Path $payload 'third-party.exe.signed') -Value 'signed'
        Set-Content -LiteralPath (Join-Path $payload 'text.dll') -Value 'not an executable'
        $innoTemplate = Get-Content -LiteralPath (Join-Path $repoRoot 'extras/win-optimizer.Installer.iss') -Raw
        $payloadFunctions = $packageAst.EndBlock.Statements | Where-Object {
            $_ -is [Management.Automation.Language.FunctionDefinitionAst] -and
            $_.Name -in @('Test-EmbeddedSignature', 'Test-PortableExecutable', 'Set-PayloadSignatures', 'Set-ApplicationSignatures')
        }
        & {
            . $signingDefinition
            foreach ($function in $payloadFunctions) { . ([scriptblock]::Create($function.Extent.Text)) }
            Set-PayloadSignatures -PublishDirectory $payload -InnoContent $innoTemplate -Certificate $testCertificate -SignToolPath $payloadSigner
            $calls = Get-Content -LiteralPath (Join-Path $fixture 'payload-signing.log')
            if ($calls.Count -ne 4 -or $calls -notcontains 'verify third-party.exe' -or
                $calls -notcontains 'sign library.winmd' -or @($calls -eq 'verify library.winmd').Count -ne 2) {
                throw "The Store payload did not preserve and verify existing signatures or sign only included PE files: $calls"
            }
            [IO.File]::WriteAllBytes((Join-Path $payload 'unverifiable.exe'), $pe)
            $failure = ''
            try {
                Set-PayloadSignatures -PublishDirectory $payload -InnoContent $innoTemplate -Certificate $testCertificate -SignToolPath $payloadSigner
            } catch { $failure = $_.Exception.Message }
            if ($failure -notmatch 'Could not sign and verify packaged executable: .*unverifiable.exe') {
                throw 'Store packaging accepted an unverifiable payload signature.'
            }
        }
        $passed += 2

        # Exercise the standard signing branch on the same layout produced by .NET publish.
        $appPayload = Join-Path $fixture 'application-payload'
        $null = New-Item -ItemType Directory -Path $appPayload
        $hosts = @('win-optimizer.exe', 'win-optimizer-preview.exe', 'win-optimizer-widgets.exe')
        $projects = @('win-optimizer.dll', 'WinOptimizer.Infrastructure.dll', 'WindowsPackageManager.Interop.dll')
        foreach ($name in $hosts + $projects + @('third-party.dll')) {
            [IO.File]::WriteAllBytes((Join-Path $appPayload $name), $pe)
        }
        $manifestPath = Join-Path $appPayload 'win-optimizer.deps.json'
        $runtimeTarget = '.NETCoreApp,Version=v10.0/win-x64'
        $manifest = @{ runtimeTarget = @{ name = $runtimeTarget }; targets = @{ $runtimeTarget = @{} }; libraries = @{} }
        foreach ($name in $projects + @('third-party.dll')) {
            $id = "${name}/1.0.0"
            $manifest.libraries[$id] = @{ type = if ($name -in $projects) { 'project' } else { 'package' } }
            $manifest.targets[$runtimeTarget][$id] = @{ runtime = @{ $name = @{} } }
        }
        $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath
        $signingLog = Join-Path $fixture 'payload-signing.log'
        & {
            . $signingDefinition
            foreach ($function in $payloadFunctions) { . ([scriptblock]::Create($function.Extent.Text)) }
            $publishOutputPath = $appPayload
            $shouldSign = $true
            $SignPackagePayload = $false
            $certificate = $testCertificate
            $signToolPath = $payloadSigner
            Clear-Content -LiteralPath $signingLog
            . $applicationSigningBlock
            $expected = ($hosts + $projects | ForEach-Object { "sign $_"; "verify $_" }) | Sort-Object
            $actual = Get-Content -LiteralPath $signingLog | Sort-Object
            if (Compare-Object $expected $actual) { throw "Unexpected application signing inputs: $actual" }

            # A refused DLL signature must propagate before Inno Setup can run.
            Set-Content -LiteralPath (Join-Path $fixture 'reject-signing.txt') -Value 'WinOptimizer.Infrastructure.dll'
            $failure = ''
            try { . $applicationSigningBlock } catch { $failure = $_.Exception.Message }
            if ($failure -notmatch 'Could not sign published application executable: .*WinOptimizer\.Infrastructure\.dll') {
                throw 'Application packaging accepted a refused Infrastructure signature.'
            }

            # A signer can return success without producing a verifiable signature.
            Remove-Item -LiteralPath (Join-Path $fixture 'reject-signing.txt')
            $manifest.libraries['unverifiable.exe/1.0.0'] = @{ type = 'project' }
            $manifest.targets[$runtimeTarget]['unverifiable.exe/1.0.0'] = @{ runtime = @{ 'unverifiable.exe' = @{} } }
            [IO.File]::WriteAllBytes((Join-Path $appPayload 'unverifiable.exe'), $pe)
            $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath
            $failure = ''
            try { . $applicationSigningBlock } catch { $failure = $_.Exception.Message }
            if ($failure -notmatch 'Could not verify published application executable signature: .*unverifiable.exe') {
                throw 'Application packaging accepted an unverifiable signature.'
            }

            # Validate the entire set before invoking the signer, including paths from the manifest.
            Clear-Content -LiteralPath $signingLog
            $manifest.targets[$runtimeTarget]['WindowsPackageManager.Interop.dll/1.0.0'].runtime = @{ '..\outside.dll' = @{} }
            $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath
            $failure = ''
            try { . $applicationSigningBlock } catch { $failure = $_.Exception.Message }
            if ($failure -notmatch 'Invalid application file' -or (Get-Item -LiteralPath $signingLog).Length) {
                throw 'An invalid project asset path reached the application signer.'
            }
        }
        $passed += 4
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
                        $command = Get-Content -LiteralPath $state.Batch -Raw
                        if ($command -notlike '* /sha1 TEST %1*') { throw 'Missing signing batch input.' }
                        if ($command -notlike '* verify /pa %1*') { throw 'Missing Inno signature verification command.' }
                    } elseif ($args.Count -ne 1) { throw 'Unsigned compilation received signing arguments.' }
                    if ($outcome -eq 'Exception') { throw 'Fixture compiler exception' }
                    $global:LASTEXITCODE = $(if ($outcome -eq 'ExitFailure') { 42 } else { 0 })
                }
                $solutionDir = $fixture
                $innoCompiler = 'Invoke-TestCompiler'
                $innoContent = 'fixture content'
                $shouldSign = $signed
                $signToolPath = 'C:\Windows SDK\signtool.exe'
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
    foreach ($scenario in @(
        @{ Signed = $false; Valid = $false; Checks = 0; Fails = $false },
        @{ Signed = $true; Valid = $true; Checks = 1; Fails = $false },
        @{ Signed = $true; Valid = $false; Checks = 1; Fails = $true }
    )) {
        $state = @{ Checks = 0 }
        $failure = & {
            function Test-EmbeddedSignature {
                param($FilePath, $SignToolPath)
                $state.Checks++
                if ($FilePath -ne $installerPath -or $SignToolPath -ne $signToolPath) { throw 'Unexpected verification inputs.' }
                return $scenario.Valid
            }
            $shouldSign = $scenario.Signed
            $installerPath = Join-Path $fixture 'win-optimizer.Installer.exe'
            $signToolPath = 'C:\Windows SDK\signtool.exe'
            try { . $installerVerificationBlock; return '' } catch { return $_.Exception.Message }
        }
        $expected = if ($scenario.Fails) { 'The final installer signature could not be verified.' } else { '' }
        if ($failure -ne $expected -or $state.Checks -ne $scenario.Checks) { throw "Unexpected final installer verification result: $failure" }
        $passed++
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
