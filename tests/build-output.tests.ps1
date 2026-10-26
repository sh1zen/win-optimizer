#requires -Version 7.0
# Verify compiler settings and the real published binaries against their external symbols.
[CmdletBinding()]
param(
    [string]$PublishDirectory,
    [string]$SymbolArchive
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $PublishDirectory) { $PublishDirectory = Join-Path $repoRoot 'build/publish' }
$PublishDirectory = (Resolve-Path -LiteralPath $PublishDirectory).Path
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $PublishDirectory 'win-optimizer.dll')).ProductVersion.Trim()
if (-not $SymbolArchive) { $SymbolArchive = Join-Path $repoRoot "build/symbols/win-optimizer-v$version-win-x64.zip" }
if ((Resolve-Path -LiteralPath $SymbolArchive).Path.StartsWith($PublishDirectory.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The symbol artifact must be outside the runtime payload.'
}
$msbuild = & (Join-Path $repoRoot 'extras/Resolve-MSBuild.ps1')
$assemblies = @('win-optimizer', 'WinOptimizer.Core', 'WinOptimizer.Application', 'WinOptimizer.Infrastructure', 'WindowsPackageManager.Interop')
foreach ($assembly in $assemblies) {
    $projectName = if ($assembly -eq 'win-optimizer') { 'WinOptimizer.UI' } else { $assembly }
    $project = Join-Path $repoRoot "src/$projectName/$projectName.csproj"
    foreach ($configuration in @('Release', 'Debug')) {
        $result = & $msbuild $project -nologo -verbosity:quiet "-property:Configuration=$configuration" -property:Platform=x64 `
            -getProperty:DebugType,DebugSymbols,Optimize,CopyOutputSymbolsToOutputDirectory,CopyOutputSymbolsToPublishDirectory,DefineConstants
        if ($LASTEXITCODE -ne 0) { throw "Could not evaluate $projectName/$configuration." }
        $properties = (($result -join [Environment]::NewLine) | ConvertFrom-Json).Properties
        if ($properties.DebugType -ne 'portable') { throw "Missing portable symbols for $projectName/$configuration." }
        if ($configuration -eq 'Release') {
            if ($properties.Optimize -ne 'true' -or $properties.CopyOutputSymbolsToOutputDirectory -ne 'false' -or
                $properties.CopyOutputSymbolsToPublishDirectory -ne 'false' -or $properties.DefineConstants -match '(^|;)DEBUG(;|$)') {
                throw "Incorrect Release optimization/symbol settings: $projectName."
            }
        } elseif ($properties.DebugSymbols -ne 'true' -or $properties.Optimize -ne 'false' -or $properties.CopyOutputSymbolsToOutputDirectory -eq 'false' -or
            $properties.CopyOutputSymbolsToPublishDirectory -ne 'true' -or $properties.DefineConstants -notmatch '(^|;)DEBUG(;|$)') {
            throw "Development debugging changed: $projectName."
        }
    }
}

if (@(Get-ChildItem -LiteralPath $PublishDirectory -Recurse -File -Filter '*.pdb').Count) { throw 'Debug symbols leaked into the runtime payload.' }
foreach ($file in @('win-optimizer.exe', 'win-optimizer.pri', 'coreclr.dll', 'Microsoft.UI.Xaml.dll', 'WindowsPackageManager.Interop.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PublishDirectory $file) -PathType Leaf)) { throw "Missing runtime file: $file" }
}
$archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $SymbolArchive).Path)
try {
    if ($archive.Entries.Count -ne $assemblies.Count) { throw 'The symbol archive must contain exactly the application and project PDBs.' }
    foreach ($assembly in $assemblies) {
        $stream = [IO.File]::OpenRead((Join-Path $PublishDirectory "$assembly.dll"))
        $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            $entries = @($pe.ReadDebugDirectory())
            if (@($entries | Where-Object Type -eq EmbeddedPortablePdb).Count) { throw "Embedded debug information in $assembly." }
            $codeView = @($entries | Where-Object Type -eq CodeView)
            if ($codeView.Count -ne 1) { throw "Missing symbol identity for $assembly." }
            $identity = $pe.ReadCodeViewDebugDirectoryData($codeView[0])
            $entry = $archive.GetEntry("$assembly.pdb")
            if (-not $entry) { throw "Missing external symbols for $assembly." }
            $compressed = $entry.Open()
            $memory = [IO.MemoryStream]::new()
            try {
                $compressed.CopyTo($memory)
                $memory.Position = 0
                $provider = [Reflection.Metadata.MetadataReaderProvider]::FromPortablePdbStream($memory)
                try {
                    $reader = $provider.GetMetadataReader()
                    $id = [Reflection.Metadata.BlobContentId]::new($reader.DebugMetadataHeader.Id)
                    if ($id.Guid -ne $identity.Guid -or $id.Stamp -ne $codeView[0].Stamp -or $identity.Age -ne 1) {
                        throw "External PDB does not match the published $assembly.dll."
                    }
                } finally { $provider.Dispose() }
            } finally { $memory.Dispose(); $compressed.Dispose() }
        } finally { $pe.Dispose(); $stream.Dispose() }
    }
} finally { $archive.Dispose() }

Write-Host "PASS: Release/Debug settings, runtime prerequisites and $($assemblies.Count) matching external portable PDBs."
