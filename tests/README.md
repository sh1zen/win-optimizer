# Tests

Run commands from the repository root. Use the Windows toolchain in the
[development guide](../docs/DEVELOPMENT.md#build-requirements). Native UI hosts
need cached NuGet packages. They use test data and simulate Windows interfaces.
Put test output and previews under `build/`.

## Main suites

```powershell
./extras/run-win-optimizer-tests.ps1
```

The runner executes Core, Application, Infrastructure and integration suites,
saving TRX results under `build/test-results/<run ID>/`. It separately **builds**
UI tests under `build/ui-test-build/`; run those tests in Visual Studio Test Explorer.
Packaging uses `-UseExistingReleaseBuild` to compile UI tests against the Release
application already built under `build/`, preserving its binaries and reusing its
project outputs. Standalone runs keep the separate Debug build.
Failed test names, messages and stack traces from TRX are printed in the CI log.
`-SkipUITests` omits that build check; `-IntegrationOnly` runs only integration tests.

The network script fixture runs in a collection that does not overlap other
Infrastructure tests. Its noninteractive Windows PowerShell worker has a two-minute
deadline; a timeout terminates its process tree and reports captured output.
WinGet deadline tests advance a controlled clock to check inactivity renewal and
expiration across output, error and progress channels without real-time delays.
Abandoned MSI mutex checks wait for native thread termination before inspecting ownership.
Update scheduling tests advance a controlled clock to check the startup delay,
daily interval and saved preferences. Script refresh tests verify that replacing
the shared sign-in script preserves the saved package selections.

Focused native UI runners share `Build-NativeTestHost.ps1` for MSBuild discovery,
cached NuGet restore and executable validation. Each runner owns its build location
and scenarios. `NUGET_PACKAGES` selects the cache; otherwise the user cache is used.
Startup and Software & Apps share `NativeTestCases` for fact/inline-data execution
and fixture disposal on the WinUI dispatcher.
The application owns Windows App SDK runtime initialization for these hosts.

## Focused checks

Build and packaging checks:

| Area | Command |
| --- | --- |
| Graph build/restore, build lock contention and cleanup, native build failures and environment restoration | `./tests/build-script.tests.ps1` |
| Build-date version, overrides and application metadata | `./tests/versioning.tests.ps1` |
| TRX diagnostics, failure detection and UI-build reuse | `./tests/test-runner.tests.ps1` |
| Signing decisions/paths, noninteractive options and temporary-file cleanup | `./tests/packaging.tests.ps1` |
| Release/Debug settings, runtime prerequisites and matching PDBs | `./tests/build-output.tests.ps1` (after publishing and exporting symbols; PowerShell 7) |
| UI suites against published assemblies/runtimes | `./tests/run-published-ui-tests.ps1 -Suite Startup` or `-Suite SoftwareApps` (after publishing) |
| Installer version, portable payload, license files, compiled resources, runtimes and languages | `./tests/packaged-payload.tests.ps1` (Windows Sandbox, after packaging) |

Native UI hosts:

| Area | Command |
| --- | --- |
| Internet speed test, network tuning, DNS and VPN controls, and Connection traffic | `./tests/run-network-tests.ps1` |
| Security | `./tests/run-security-tests.ps1` |
| Context menus | `./tests/run-context-menu-tests.ps1` |
| Software & Apps, inventory updates and post-uninstall reconciliation | `./tests/run-software-apps-tests.ps1` |
| Autoruns | `./tests/run-autoruns-tests.ps1` |
| Process monitor | `./tests/run-process-monitor-tests.ps1` |
| SysMonitor preferences and widget scaling | `./tests/run-system-monitor-tests.ps1` |
| Windows servicing | `./tests/run-windows-status-tests.ps1` |
| Popups, technical details, Preview and Notch | `./tests/run-popup-tests.ps1` |
| Topic journal and history | `./tests/run-journal-tests.ps1` |
| Startup, safe close/restart, widget shutdown, battery controls, update presentation, support links, backup/task ownership, environment drafts, window placement, Cleaner, Drivers, WIM, navigation, saved Wi-Fi, settings and icons | `./tests/run-startup-tests.ps1` |

Windows PowerShell fixtures with fake OS boundaries:

```powershell
powershell.exe -NoProfile -File ./tests/optimization-tools-scripts.tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./tests/security-scripts.tests.ps1
./tests/autoruns-storage.tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./tests/registry-defrag.tests.ps1
```

## Test boundaries

Registry service and context-menu tests exercise native `RegistryKey` APIs on
empty temporary hives opened with `RegLoadAppKey`. Each fixture owns its handles
and file. Context-menu notifications and hardware inventory use synthetic
providers. The default test runner can run with host registry writes denied.
Power-setting write tests simulate the native scheme operations, including Windows
errors during lookup, AC/DC writes and activation.
Configuration regressions verify policy cleanup against the private hive and
simulate Windows Update, theme and Explorer failures at the OS boundaries.
Update and WIM regressions use fake HTTP and ISO executors with disposable
fixtures for installer staging, answer-file bytes and media validation.

Registry defragmentation tests replace the registry export boundary and execute
the WinPE worker's backup, file replacement and rollback against NTFS fixtures.
They leave the host registry and boot configuration untouched.

Autounattend checks parse the assembled script with Windows PowerShell and verify
package and registry output, including byte values and definition-based bit writes.
Answer-file integration tests cover real catalogue settings and AC/DC values.
Core tests cover fractional display-unit truncation, and native WIM tests exercise
the image deletion commands. Package removal tests execute the generated servicing
logic with fake Windows cmdlets and verify preservation of saved sign-in selections.
The native Software & Apps and Startup hosts cover app removal reconciliation and
TimeMachine retention reset with fake providers.

Cleaner hard-link regressions use isolated files under the system temporary folder,
which must support local NTFS hard links. Workload timings report discovery,
inspection, verified backup and deletion separately; timings have no pass/fail threshold.

Published UI checks run a temporary copy of the production payload. The local
installer test requires Windows Sandbox. The guest runs
with networking and device redirection disabled, reads from read-only input
folders, and writes its result to a dedicated temporary host folder. The test
copies the guest log to `build/logs/PackagedPayload-Sandbox.log`.

GitHub Actions runs the installer test with Windows PowerShell in the fresh
`windows-2025` VM for the job. The `-GitHubHostedRunner` switch requires a
GitHub-hosted Windows runner.

See the [language-resource guide](../src/WinOptimizer.UI/Features/Common/Localization/README.md)
for translation coverage and format validation.

## Domain filters

For focused Infrastructure checks, set the project once and choose a filter below:

```powershell
$infrastructureTests = './tests/WinOptimizer.Infrastructure.Tests/WinOptimizer.Infrastructure.Tests.csproj'
dotnet test $infrastructureTests --filter 'FullyQualifiedName~PowerShellRunner'
```

Quote combined filters containing `|` in PowerShell.

| Area | Filter |
| --- | --- |
| WIM/ISO conversion, safe ISO replacement, setup commands and driver packages | `FullyQualifiedName~AdvancedTools` |
| Process cancellation and PowerShell validation | `FullyQualifiedName~ProcessExecutorTests\|FullyQualifiedName~PowerShellRunner` |
| Native uninstall ownership, WinGet deadlines and removal verification | `FullyQualifiedName~WindowsUninstallCommandSourceTests\|FullyQualifiedName~WindowsInstalledProgramSourceTests\|FullyQualifiedName~WinGetPackageInstallerTests\|FullyQualifiedName~ExternalAppUninstallServiceTests` |
| AppX account discovery and removal | `FullyQualifiedName~AppxPackageSourceTests\|FullyQualifiedName~WindowsAppxDeploymentTests\|FullyQualifiedName~ExternalAppUninstallServiceTests` |
| Discovery caching, invalidation, provider overlap and registry matching | `FullyQualifiedName~AppStatusDiscoveryServiceTests` |
| Portable-app account paths and unavailable folders | `FullyQualifiedName~PortableAppAccountTests` |
| Desktop/AppX icons, caching and registration matching | `FullyQualifiedName~AppIconResolverTests\|FullyQualifiedName~DesktopAppIconSourceTests\|FullyQualifiedName~WindowsInstalledProgramSourceTests` |
| Leftover discovery, MSI states, installation folders and recovery | `FullyQualifiedName~LeftoverServiceTests\|FullyQualifiedName~WindowsInstallerInventoryTests\|FullyQualifiedName~UninstallResidueServiceTests` |
| Cleaner discovery, backup/recovery and path checks | `FullyQualifiedName~Cleaner` |
| Offline registry hives, lifecycle and Windows errors | `FullyQualifiedName~WindowsCleanerRegistryHiveAccessTests\|FullyQualifiedName~CleanerRegistrySessionsTests` |
| Windows servicing and cancellation | `FullyQualifiedName~WindowsServicingTests\|FullyQualifiedName~ProcessExecutorTests\|FullyQualifiedName~DismProcessRunnerTests` |
| Optimize orchestration, fake native scripts and speed measurement | `FullyQualifiedName~OptimizationToolsServiceTests\|FullyQualifiedName~NetworkTuningTests\|FullyQualifiedName~InternetSpeedTestTests` |
| Security Windows boundary | `FullyQualifiedName~WindowsSecurityBackendTests` |
| Driver inventory, provider response fixtures, cache and package acquisition, and fake Windows Update scripts | `FullyQualifiedName~Drivers` |
| Autoruns | `FullyQualifiedName~Autorun` |
| Shared task ownership, queue progress and terminal output | `FullyQualifiedName~TaskProgressServiceTests` |

To display the isolated 1,000-file workload timings and allocations:

```powershell
dotnet test $infrastructureTests --filter FullyQualifiedName~CleanerFilePerformanceTests --logger 'console;verbosity=detailed'
```

Other projects:

```powershell
# Configuration and preference queues
dotnet test ./tests/WinOptimizer.Application.Tests/WinOptimizer.Application.Tests.csproj
# Security scan authority
dotnet test ./tests/WinOptimizer.Core.Tests/WinOptimizer.Core.Tests.csproj --filter FullyQualifiedName~SecurityScanServiceTests
# Driver matching, package identity, source outcomes and removal protection
dotnet test ./tests/WinOptimizer.Core.Tests/WinOptimizer.Core.Tests.csproj --filter FullyQualifiedName~Drivers
# Language resources
dotnet test ./tests/WinOptimizer.IntegrationTests/WinOptimizer.IntegrationTests.csproj --filter FullyQualifiedName~Localization
# Preview file and folder metadata, text, Office packages and path checks
dotnet test ./tests/WinOptimizer.IntegrationTests/WinOptimizer.IntegrationTests.csproj --filter FullyQualifiedName~FilePreview
```

The startup host covers Drivers preferences, separate download and removal
selection and task cancellation. Infrastructure tests run the driver script
fixtures in Windows PowerShell with owned temporary directories and fake Windows
boundaries.

```powershell
dotnet test $infrastructureTests --filter 'FullyQualifiedName~UpdateScript_SearchesOptionalOffers|FullyQualifiedName~InventoryScript_MeasuresOnlySelectedPackages'
```


`network-tuning-scripts.tests.ps1` is a worker invoked by `NetworkTuningTests`;
its payload is supplied by that suite.

## Preview desktop checks

Use an installed or portable build for these checks. See the
[Preview guide](../docs/PREVIEW.md) for controls and supported formats.

1. Turn on **Preview with Space**. Verify that the popup stays closed until
   you select a local file and press **Space**. Minimize the main window.
   Verify that the popup opens on the first press. Press **Esc** to close it.
2. Select a File Explorer folder with subfolders. Open its preview.
   Compare size and counts with Windows **Properties**. Open **Folder
   contents** to verify the direct entries.
3. Open previews for Unicode text, a DOCX, a two-page PDF, a PNG, and an audio
   file. Verify the content, PDF page controls, and playback. Verify the
   result for a shortcut to the text file, a broken shortcut, and an
   Internet shortcut.
4. Switch File Explorer tabs. Select a different file and open its preview.
   Verify that the popup shows the file from the active tab. Verify that
   **Space** still works in search, the address bar, a rename field, and a
   text editor.
5. Turn off **Preview with Space**. Verify that **Space** does not open the
   popup. Turn the same switch on. Verify that the popup opens again.
6. Run win-optimizer with administrator permissions. Verify both Preview
   switches from the desktop account. Verify background activation at
   sign-in. Verify that the background Preview process exits during an
   update. Repeat for both installation types.
