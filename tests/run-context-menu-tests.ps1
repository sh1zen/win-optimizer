# Renders the production context-menu page with fake registry boundaries and local fixture artwork.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$contextRepository = Split-Path -Parent $PSScriptRoot
$contextBuildRoot = (Join-Path $contextRepository 'build/context-menu-tests') + '/'
$contextExecutable = & (Join-Path $PSScriptRoot 'Build-NativeTestHost.ps1') `
    -ProjectName WinOptimizer.ContextMenu.Tests -BuildRoot $contextBuildRoot -Configuration Debug -DisableSharedCompilation
foreach ($contextTheme in @('Dark', 'Light')) {
    & $contextExecutable (Join-Path $contextRepository 'build/context-menu-preview') `
        (Join-Path $contextRepository 'src/WinOptimizer.UI/Assets/AppIcons/win-optimizer.png') $contextTheme
    if ($LASTEXITCODE -ne 0) { throw "Context-menu presentation checks failed ($contextTheme): $LASTEXITCODE" }
}
