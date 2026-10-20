# Builds and renders production popup controls with isolated data and services.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repositoryRoot = Split-Path -Parent $PSScriptRoot
# Keep native popup construction at its authoritative owner as new features are added.
$uiRoot = Join-Path $repositoryRoot 'src/WinOptimizer.UI'
$popupOwner = Join-Path $uiRoot 'Features/Common/Helpers/PopupHandler.cs'
$duplicates = Get-ChildItem -LiteralPath $uiRoot -Recurse -File -Include '*.cs','*.xaml' |
    Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' -and $_.FullName -ne $popupOwner } |
    Select-String -Pattern 'new\s+(ContentDialog|Flyout|MenuFlyout|ToolTip)\b|<(ContentDialog|Flyout|MenuFlyout|ToolTip)(\s|>)'
if ($duplicates) { throw "Native popup construction outside PopupHandler: $($duplicates -join [Environment]::NewLine)" }
Write-Output 'PASS: application popup construction is centralized.'
$buildRoot = Join-Path $repositoryRoot 'build/technical-details-validation/'
$executable = & (Join-Path $PSScriptRoot 'Build-NativeTestHost.ps1') `
    -ProjectName WinOptimizer.Popup.Tests -BuildRoot $buildRoot -DisableSharedCompilation
& $executable (Join-Path $repositoryRoot 'build/technical-details-previews')
if ($LASTEXITCODE -ne 0) { throw "Popup checks failed: $LASTEXITCODE" }
