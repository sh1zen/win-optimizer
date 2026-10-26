# Builds, tests and packages win-optimizer, with optional certificate-store signing.
# Toolchain, options and examples: README.md#development.
# Build ownership and ordering: ARCHITECTURE.md#build-configuration.
[CmdletBinding()]
param (
    [ValidatePattern('^[0-9]{2}\.[0-9]{2}\.[0-9]{2}$')]
    [string]$Version,
    [string]$OutputDir,
    [string]$InnoCompiler,
    [string]$CertificateSubject = "",
    [string]$CertificateThumbprint = "",
    [switch]$SignApplication = $false,
    [switch]$SkipSigning = $false,
    [switch]$SkipTests = $false
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false

# Reject contradictory signing choices before creating output.
if ($SkipSigning -and ($SignApplication -or $CertificateSubject -or $CertificateThumbprint)) {
    throw '-SkipSigning cannot be combined with -SignApplication or certificate selection options.'
}

$scriptRoot = $PSScriptRoot
$solutionDir = Resolve-Path "$scriptRoot\.."
if (-not $OutputDir) { $OutputDir = Join-Path $solutionDir 'build\installers' }
$OutputDir = [IO.Path]::GetFullPath($OutputDir)

# Resolve compiler availability before creating output or downloading dependencies.
if (-not $InnoCompiler) {
    $InnoCompiler = @(
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe'
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
if (-not $InnoCompiler -or -not (Test-Path -LiteralPath $InnoCompiler -PathType Leaf)) {
    throw 'Inno Setup 6 compiler not found. Install Inno Setup 6 or pass -InnoCompiler with the ISCC.exe path.'
}
$InnoCompiler = (Resolve-Path -LiteralPath $InnoCompiler).Path

# Resolve the shared version once so a build crossing midnight keeps the same date.
$MSBuildPath = & (Join-Path $scriptRoot 'Resolve-MSBuild.ps1')
$versionArguments = @(
    (Join-Path $solutionDir 'Version.props')
    '-target:ValidateWinOptimizerVersion'
    '-getProperty:WinOptimizerVersion'
    '-verbosity:quiet'
    '-nologo'
)
if ($Version) { $versionArguments += "-property:WinOptimizerVersion=$Version" }
$resolvedVersion = & $MSBuildPath @versionArguments
if ($LASTEXITCODE -ne 0) { throw 'Application version resolution failed. Use a valid date in YY.MM.DD format.' }
$Version = ($resolvedVersion -join '').Trim()

# Function to find and select a code signing certificate
function Get-SigningCertificate {
    param (
        [string]$Subject,
        [string]$Thumbprint
    )

    # If thumbprint is provided, use it directly
    if ($Thumbprint) {
        $cert = Get-ChildItem -Path Cert:\CurrentUser\My | Where-Object { $_.Thumbprint -eq $Thumbprint }
        if ($cert) {
            return $cert
        }
        else {
            Write-Host "Certificate with thumbprint '$Thumbprint' not found." -ForegroundColor Red
        }
    }

    # If subject is provided, try to find matching certificates
    if ($Subject) {
        $certs = Get-ChildItem -Path Cert:\CurrentUser\My | Where-Object { $_.Subject -like "*$Subject*" }
        if ($certs -and $certs.Count -gt 0) {
            if ($certs.Count -eq 1) {
                return $certs[0]
            }
            else {
                Write-Host "Multiple certificates found with subject '$Subject'. Please select one:" -ForegroundColor Yellow
                for ($i = 0; $i -lt $certs.Count; $i++) {
                    Write-Host "[$i] $($certs[$i].Subject) (Thumbprint: $($certs[$i].Thumbprint))" -ForegroundColor Cyan
                }
                $selection = Read-Host "Enter the number of the certificate to use"
                if ($selection -match '^\d+$' -and [int]$selection -ge 0 -and [int]$selection -lt $certs.Count) {
                    return $certs[[int]$selection]
                }
                else {
                    Write-Host "Invalid selection." -ForegroundColor Red
                    return $null
                }
            }
        }
        else {
            Write-Host "No certificates found with subject '$Subject'." -ForegroundColor Red
        }
    }

    # If no certificate found yet, list all certificates and let user select
    $certs = Get-ChildItem -Path Cert:\CurrentUser\My | Where-Object { $_.HasPrivateKey -and ($_.EnhancedKeyUsageList -and $_.EnhancedKeyUsageList.ObjectId -contains "1.3.6.1.5.5.7.3.3") }
    
    if ($certs -and $certs.Count -gt 0) {
        Write-Host "Available code signing certificates:" -ForegroundColor Green
        for ($i = 0; $i -lt $certs.Count; $i++) {
            Write-Host "[$i] $($certs[$i].Subject) (Thumbprint: $($certs[$i].Thumbprint))" -ForegroundColor Cyan
        }
        $selection = Read-Host "Enter the number of the certificate to use (or press Enter to skip signing)"
        if ($selection -match '^\d+$' -and [int]$selection -ge 0 -and [int]$selection -lt $certs.Count) {
            return $certs[[int]$selection]
        }
        elseif ($selection -eq "") {
            Write-Host "Signing skipped." -ForegroundColor Yellow
            return $null
        }
        else {
            Write-Host "Invalid selection. Signing skipped." -ForegroundColor Red
            return $null
        }
    }
    else {
        Write-Host "No code signing certificates found in your certificate store." -ForegroundColor Red
        return $null
    }
}

# Function to sign a file using signtool
function Set-FileSignature {
    param (
        [string]$FilePath,
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [string]$TimestampServer = "http://timestamp.digicert.com"
    )

    if (-not $Certificate) {
        Write-Host "No certificate provided for signing." -ForegroundColor Yellow
        return $false
    }

    if (-not (Test-Path $FilePath)) {
        Write-Host ("File not found: {0}" -f $FilePath) -ForegroundColor Red
        return $false
    }

    $signtoolPath = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"
    if (-not (Test-Path $signtoolPath)) {
        # Try to find signtool in other Windows Kit directories
        $possiblePaths = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue
        if ($possiblePaths.Count -gt 0) {
            $signtoolPath = $possiblePaths[0].FullName
        }
        else {
            Write-Host "signtool.exe not found. Please ensure Windows SDK is installed." -ForegroundColor Red
            return $false
        }
    }

    Write-Host ("Signing {0}..." -f $FilePath) -ForegroundColor Cyan
    $thumbprint = $Certificate.Thumbprint
    
    try {
        $result = & $signtoolPath sign /tr $TimestampServer /td sha256 /fd sha256 /sha1 $thumbprint $FilePath
        if ($LASTEXITCODE -eq 0) {
            Write-Host ("Successfully signed {0}" -f $FilePath) -ForegroundColor Green
            return $true
        }
        else {
            Write-Host ("Failed to sign {0}. Error code: {1}" -f $FilePath, $LASTEXITCODE) -ForegroundColor Red
            Write-Host $result -ForegroundColor Red
            return $false
        }
    }
    catch {
        Write-Host ("Error signing {0}: {1}" -f $FilePath, $_.Exception.Message) -ForegroundColor Red
        return $false
    }
}

# MSBuild owns the publish payload; test outputs and local tools stay outside it.
$publishOutputPath = [IO.Path]::GetFullPath((Join-Path $solutionDir 'build\publish'))
$innoSetupScript = "$scriptRoot\win-optimizer.Installer.iss"

# Declare certificate variable at script scope so it's accessible throughout
$certificate = $null
$shouldSign = $false

# Ensure output directory exists
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

# Resolve signing certificate UP FRONT, before any subprocess runs.
# Resolve certificate problems before spending time on the build. The cert and
# shouldSign state are reused by Step 4 (exe signing), Step 5/6 (Inno Setup
# uninstaller signing), and Step 7 (installer signing) below.
if (-not $SkipSigning -and ($SignApplication -or (Read-Host "Do you want to sign the application? (y/n)").ToLower() -eq 'y')) {
    $certificate = Get-SigningCertificate -Subject $CertificateSubject -Thumbprint $CertificateThumbprint
    if ($certificate) {
        Write-Host "Selected certificate: $($certificate.Subject)" -ForegroundColor Green
        Write-Host "Thumbprint: $($certificate.Thumbprint)" -ForegroundColor Green
        $shouldSign = $true
    }
    else {
        if ($SignApplication) {
            throw 'No code-signing certificate was selected for the requested signed package.'
        }
        Write-Host "No certificate selected. Continuing with unsigned build..." -ForegroundColor Yellow
    }
}
else {
    Write-Host "Skipping signing for this build..." -ForegroundColor Yellow
}

Write-Host "Building win-optimizer v$Version..." -ForegroundColor Cyan

# Step 0: Update bundled WinGet CLI and VC++ Runtime DLLs
Write-Host "Updating bundled WinGet and VC++ Runtime DLLs..." -ForegroundColor Green
& "$scriptRoot\Update-BundledWinGet.ps1"
if ($LASTEXITCODE -ne 0) {
    Write-Host "Failed to update bundled WinGet" -ForegroundColor Red
    exit 1
}

# Steps 1 and 2: Restore and incrementally build with the shared toolchain setup.
Write-Host "Building solution..." -ForegroundColor Green
& (Join-Path $solutionDir 'build.ps1') -Configuration Release -Version $Version -MSBuildPath $MSBuildPath

# Step 3: Run tests
if (-not $SkipTests) {
    Write-Host "Running tests..." -ForegroundColor Green
    & "$scriptRoot\run-win-optimizer-tests.ps1" -UseExistingReleaseBuild -MSBuildPath $MSBuildPath
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Tests failed. Fix the failing tests before building the installer." -ForegroundColor Red
        exit 1
    }
}
else {
    Write-Host "Skipping tests (-SkipTests)..." -ForegroundColor Yellow
}

# Publish the existing Release build into the dedicated, reusable payload directory.
# Only this generated directory is replaced; application output and intermediates remain intact.
$expectedPublishPath = [IO.Path]::GetFullPath((Join-Path $solutionDir 'build\publish'))
if ($publishOutputPath -ne $expectedPublishPath) { throw 'Unexpected publish directory.' }
if (Test-Path -LiteralPath $publishOutputPath) {
    if ((Get-Item -LiteralPath $publishOutputPath).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'The publish directory must not be a filesystem link.'
    }
    [IO.Directory]::Delete($publishOutputPath, $true)
}
& $MSBuildPath (Join-Path $solutionDir 'src\WinOptimizer.UI\WinOptimizer.UI.csproj') `
    -target:Publish -property:Configuration=Release -property:Platform=x64 -property:NoBuild=true `
    "-property:WinOptimizerVersion=$Version" `
    -verbosity:minimal -nologo
if ($LASTEXITCODE -ne 0) { throw "Application publishing failed with exit code $LASTEXITCODE." }

# Preserve the exact build's symbols outside the payload, before any signing.
& (Join-Path $scriptRoot 'export-debug-symbols.ps1') -MSBuildPath $MSBuildPath -Version $Version

# Step 4: Sign the application executable using the cert resolved at script start
$mainExecutable = "$publishOutputPath\win-optimizer.exe"
if ($shouldSign -and $certificate) {
    Write-Host "Signing application executable..." -ForegroundColor Cyan
    $signResult = Set-FileSignature -FilePath $mainExecutable -Certificate $certificate
    if ($signResult) {
        Write-Host "Application executable signed successfully." -ForegroundColor Green
    }
    else {
        throw 'Application signing failed. Installer creation has been stopped.'
    }
}
else {
    Write-Host "Skipping application signing..." -ForegroundColor Yellow
}

# Step 5: Update the InnoSetup script with correct paths
Write-Host "Preparing InnoSetup script..." -ForegroundColor Green
$innoContent = Get-Content -Path $innoSetupScript -Raw

# Update version
$innoContent = $innoContent -replace '#define MyAppVersion ".*"', "#define MyAppVersion `"$Version`""
# Update AppVerName to include version in the installer header
$innoContent = $innoContent -replace 'AppVerName=win-optimizer', "AppVerName=win-optimizer v$Version"

# Update paths
$publishPath = $publishOutputPath.Replace("\", "\\")
$outputPath = $OutputDir.Replace("\", "\\")
$licensePath = "$solutionDir\LICENSE.txt".Replace("\", "\\")
$iconPath = "$solutionDir\src\WinOptimizer.UI\Assets\AppIcons\win-optimizer.ico".Replace("\", "\\")

$innoContent = $innoContent -replace 'LicenseFile=C:\\win-optimizer\\LICENSE.txt', "LicenseFile=$licensePath"
$innoContent = $innoContent -replace 'OutputDir=C:\\win-optimizer\\build\\installers', "OutputDir=$outputPath"
$innoContent = $innoContent -replace 'SetupIconFile=C:\\win-optimizer\\src\\WinOptimizer\.UI\\Assets\\AppIcons\\win-optimizer\.ico', "SetupIconFile=$iconPath"
$innoContent = $innoContent -replace 'Source: "C:\\win-optimizer\\build\\publish\\', "Source: `"$publishPath\\"
$innoContent = $innoContent -replace 'Source: "C:\\win-optimizer\\extras\\portable\.marker"', "Source: `"$scriptRoot\\portable.marker`""

# Add uninstaller signing directives if code signing is enabled
if ($shouldSign -and $certificate) {
    $innoContent = $innoContent -replace '(SolidCompression=yes)', "`$1`r`nSignTool=mysigntool`r`nSignedUninstaller=yes"
}

# Each compilation owns its temporary inputs, including when the compiler fails.
$temporaryDirectory = Join-Path $solutionDir 'build\temp'
foreach ($directory in @((Join-Path $solutionDir 'build'), $temporaryDirectory)) {
    if ((Test-Path -LiteralPath $directory) -and
        ((Get-Item -LiteralPath $directory).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'The packaging temporary directory must not be a filesystem link.'
    }
}
$null = New-Item -ItemType Directory -Path $temporaryDirectory -Force
$temporaryName = 'installer-' + [guid]::NewGuid().ToString('N')
$tempInnoScript = Join-Path $temporaryDirectory "$temporaryName.iss"
$signBatPath = Join-Path $temporaryDirectory "$temporaryName.bat"
try {
    Set-Content -LiteralPath $tempInnoScript -Value $innoContent

    # Step 6: Run the InnoSetup compiler.
    Write-Host "Creating installer..." -ForegroundColor Green
    if ($shouldSign -and $certificate) {
        # Inno invokes this command with its generated uninstaller path.
        $signtoolExe = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"
        if (-not (Test-Path $signtoolExe)) {
            $possiblePaths = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue
            if ($possiblePaths.Count -gt 0) {
                $signtoolExe = $possiblePaths[0].FullName
            }
        }
        Set-Content -LiteralPath $signBatPath -Value "@`"$signtoolExe`" sign /tr http://timestamp.digicert.com /td sha256 /fd sha256 /sha1 $($certificate.Thumbprint) %1"

        Write-Host "Creating installer with signed uninstaller..." -ForegroundColor Green
        & $innoCompiler "/Smysigntool=$signBatPath `$f" $tempInnoScript
    } else {
        & $innoCompiler $tempInnoScript
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Installer compilation failed with exit code $LASTEXITCODE."
    }
} finally {
    foreach ($temporaryFile in @($tempInnoScript, $signBatPath)) {
        if (Test-Path -LiteralPath $temporaryFile) { Remove-Item -LiteralPath $temporaryFile -Force }
    }
}

# Sign the installer if the executable was signed
$installerPath = Join-Path $OutputDir 'win-optimizer.Installer.exe'
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw "Installer compiler completed without producing the expected file: $installerPath"
}
if ($shouldSign -and $certificate -and (Test-Path $installerPath)) {
    Write-Host "Signing the installer..." -ForegroundColor Cyan
    $installerSignResult = Set-FileSignature -FilePath $installerPath -Certificate $certificate

    if ($installerSignResult) {
        Write-Host "Installer successfully signed." -ForegroundColor Green
    }
    else {
        throw 'Installer signing failed. The package is not ready for distribution.'
    }
}
elseif (-not $shouldSign) {
    Write-Host "Skipping installer signing (executable was not signed)." -ForegroundColor Yellow
}

Write-Host "Build and packaging completed successfully!" -ForegroundColor Cyan
Write-Host "Installer created at: $installerPath" -ForegroundColor Green

# Display signing status summary
if ($shouldSign) {
    Write-Host "`nSigning Summary:" -ForegroundColor Cyan
    Write-Host "  Certificate: $($certificate.Subject)" -ForegroundColor Green
    Write-Host "  Executable: Signed" -ForegroundColor Green
    Write-Host "  Uninstaller: Signed (embedded in installer)" -ForegroundColor Green
    if ($installerSignResult) {
        Write-Host "  Installer: Signed" -ForegroundColor Green
    }
    else {
        Write-Host "  Installer: Failed to sign" -ForegroundColor Red
    }
}
else {
    Write-Host "`nSigning Summary: No files were signed" -ForegroundColor Yellow
}
