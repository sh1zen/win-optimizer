# Build, package, and release win-optimizer

## Build requirements

- Windows.
- The .NET 11 SDK version in [global.json](../global.json).
- Visual Studio 2026 or Build Tools with MSBuild 18 or later.
- The .NET desktop and C++ desktop workloads.
- The Windows SDK.

Open [win-optimizer.sln](../win-optimizer.sln) in Visual Studio. GitHub
Actions uses the same .NET SDK. The application targets .NET 10 and the
installer includes its runtime.

## Build locally

Run these commands from the repository root. The first command builds
Release for x64 by default.

```powershell
./build.ps1
./build.ps1 -Configuration Debug
./build.ps1 -NoRestore
```

The build writes `build/win-optimizer.exe` and `build/logs/build.log`.
Use `-Clean` for a clean rebuild. Use `-MSBuildPath` to select MSBuild.
Use `-NoRestore` for incremental code builds after a successful restore.
Restore again after a dependency or SDK change.

Only one build can use an output folder at a time. A second build for that
folder reports that a build is active and exits. Windows releases the lock
when the build process exits. Use `-BuildRoot ./build/isolated` for a
parallel build in a separate, reusable folder.

If MSBuild reports that a DLL is in use, close its process or use a different
`-BuildRoot`. Preview and the SysMonitor and Notch companion keep shared DLLs
open while active. Disable those extensions before rebuilding in the same folder.

If NuGet reports `NU1900` because it cannot write to its HTTP cache, set
`NUGET_HTTP_CACHE_PATH` to a writable folder.

## Run tests

Use the [test guide](../tests/README.md) for suite commands and isolation
requirements. It also lists checks for individual features.

## Build the installer and export symbols

Install Inno Setup 6. Run the installer build from the repository root.

```powershell
./extras/build-and-package.ps1
```

The script packages `build/publish/` into
`build/installers/win-optimizer.Installer.exe`. Use `-SkipSigning` to build
an unsigned installer without signing prompts. Use
`-InnoCompiler <path-to-ISCC.exe>` with a portable Inno Setup installation.
See [build configuration](../ARCHITECTURE.md#build-configuration) for build
ownership, reuse of test and build outputs, and signing order.

For distribution that requires embedded Authenticode signatures on every
packaged PE file, use `-SignApplication -SignPackagePayload` with
`-CertificateThumbprint <thumbprint>`. Packaging preserves valid signatures
and signs and verifies the remaining executables before compiling the installer.

Release builds use optimized IL and keep portable PDB files in compiler
intermediates. Deployment and publish folders exclude these files. Debug
builds keep debugger settings and put symbols beside the binaries.

Packaging exports the matching Release symbols to
`build/symbols/win-optimizer-vYY.MM.DD-win-x64.zip`. Keep the symbol ZIP
with its release for debugging and crash analysis. To export symbols from
an existing Release build, run the following command.

```powershell
./extras/export-debug-symbols.ps1 -Version YY.MM.DD
```

## Use build artifacts from GitHub Actions

**Build Windows installer** runs for pushes and pull requests to `main`.
The workflow tests the packaged installer in the disposable GitHub-hosted
Windows VM before uploading it.
To start it manually, open **Actions → Build Windows installer → Run workflow**.
The optional version uses `YY.MM.DD`. The default is the build date.

| Artifact | Retention |
| --- | --- |
| Validated unsigned installer, `win-optimizer-windows-x64-vYY.MM.DD` | 30 days |
| Matching symbols, `win-optimizer-symbols-windows-x64-vYY.MM.DD` | 30 days |
| Test results and build logs, `win-optimizer-validation-vYY.MM.DD` | 14 days, including failures |

If version resolution fails, the validation artifact uses `vunresolved` in
the version field.

## Publish a release

[`Version.props`](../Version.props) supplies the application version to the
scripts and Visual Studio. The default is the local build date. Use
`-Version YY.MM.DD` to select a release date explicitly. Product and file
metadata use `YY.MM.DD`. Release and update tags use `vYY.MM.DD`.

Keep pending changes under `## [Unreleased]` in the
[changelog](../CHANGELOG.md). Before a release, move those changes into
`## [vYY.MM.DD] - YYYY-MM-DD` with the exact tag and date. Keep
`Unreleased` at the top.

Commit the sources and release notes. To tag and publish a release for the
current date, run the following commands.

```powershell
$releaseTag = 'v' + (Get-Date -Format 'yy.MM.dd')
git tag $releaseTag
git push origin $releaseTag
```

The **Release** workflow builds and validates the installer for the tag.
It publishes the body of the matching changelog section as UTF-8 Markdown
release notes. A missing, empty, or duplicate tag section stops publication.

## Update documentation and assets

Use ASD-STE100, Issue 9, for technical prose. Use the precise names of app
controls, commands, and paths. Link to the guide that contains the procedure.
See the [ASD-STE100 writing rules and dictionary](https://www.asd-ste100.org/assets/files/ASD-STE100_ISSUE9.pdf).

Capture the full app window for each screenshot. Include the title bar and
sidebar. Use a window size that shows the controls in the guide. When you show
a popup or desktop widget, keep the main window in the same screenshot. Use
installed app icons in inventory screenshots.

Use the full-window captures to make guide illustrations in `docs/images/`.

See [application artwork](../src/WinOptimizer.UI/Assets/ICON-ATTRIBUTION.md)
for icon authoring, generation, and attribution. Examine the
[third-party notices](../THIRD-PARTY-NOTICES.txt) when you change package
references or update the WinGet bundle. The notices cover resolved
dependencies, build tools, bundled WinGet components, and optional downloads.
