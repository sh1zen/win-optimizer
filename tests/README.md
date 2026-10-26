# Tests

Run commands from the repository root using the Windows toolchain in the
[development guide](../README.md#development). Native UI hosts need cached NuGet
packages and use synthetic data with fake OS boundaries. Test output and previews
belong under `build/`.

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

Focused native UI runners share `Build-NativeTestHost.ps1` for MSBuild discovery,
cached NuGet restore and executable validation. Each runner owns its build location
and scenarios. `NUGET_PACKAGES` selects the cache; otherwise the user cache is used.
Startup and Software & Apps share `NativeTestCases` for fact/inline-data execution
and fixture disposal on the WinUI dispatcher.

## Focused checks

Build and packaging checks:

| Area | Command |
| --- | --- |
| Graph build/restore, native build failures and environment restoration | `./tests/build-script.tests.ps1` |
| Build-date version, overrides and application metadata | `./tests/versioning.tests.ps1` |
| TRX diagnostics, failure detection and UI-build reuse | `./tests/test-runner.tests.ps1` |
| Signing decisions/paths, noninteractive options and temporary-file cleanup | `./tests/packaging.tests.ps1` |
| Release/Debug settings, runtime prerequisites and matching PDBs | `./tests/build-output.tests.ps1` (after publishing and exporting symbols; PowerShell 7) |
| UI suites against published assemblies/runtimes | `./tests/run-published-ui-tests.ps1 -Suite Startup` or `-Suite SoftwareApps` (after publishing) |
| Installer compilation, destination/mode navigation and launcher | `./tests/installer.tests.ps1` (Inno Setup 6; optional `-InnoCompiler <path-to-ISCC.exe>`) |
| Installer version, portable payload, compiled resources, runtimes and languages | `./tests/packaged-payload.tests.ps1` (after packaging) |

Native UI hosts:

| Area | Command |
| --- | --- |
| Network | `./tests/run-network-tests.ps1` |
| Security | `./tests/run-security-tests.ps1` |
| Context menus | `./tests/run-context-menu-tests.ps1` |
| Software & Apps, inventory updates and post-uninstall reconciliation | `./tests/run-software-apps-tests.ps1` |
| Autoruns | `./tests/run-autoruns-tests.ps1` |
| Process monitor | `./tests/run-process-monitor-tests.ps1` |
| SysMonitor preferences and widget scaling | `./tests/run-system-monitor-tests.ps1` |
| Windows servicing | `./tests/run-windows-status-tests.ps1` |
| Popups and technical details | `./tests/run-popup-tests.ps1` |
| Topic journal and history | `./tests/run-journal-tests.ps1` |
| Startup, window placement, Cleaner, navigation, saved Wi-Fi, settings and icons | `./tests/run-startup-tests.ps1` |

Windows PowerShell fixtures with fake OS boundaries:

```powershell
powershell.exe -NoProfile -File ./tests/optimization-tools-scripts.tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./tests/security-scripts.tests.ps1
./tests/autoruns-storage.tests.ps1
```

## Test boundaries

Cleaner hard-link regressions use isolated files under the system temporary folder,
which must support local NTFS hard links. Workload timings report discovery,
inspection, verified backup and deletion separately; timings have no pass/fail threshold.

Installer regressions create isolated installations in the Windows temporary
folder, which must support local NTFS junctions. They use unique registration IDs
and redirect application storage into the fixture.
Offline-profile fixtures are removed after each scenario; junction targets stay intact.
Uninstaller scenario logs are saved under `build/logs/`; installer checks create
the directory before collecting logs.
Registry trace containers, the profile catalogue, Prefetch and disabled startup
tasks use unique fixture boundaries. Checks cover exact and GUID-based ownership,
unrelated values/tasks, empty scheduler folders, offline profile errors, denied
registry writes and locked or redirected cleanup paths.

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
| WIM/ISO conversion, setup commands and driver packages | `FullyQualifiedName~AdvancedTools` |
| Process cancellation and PowerShell validation | `FullyQualifiedName~ProcessExecutorTests\|FullyQualifiedName~PowerShellRunner` |
| Direct/Store installer results and account deployment | `FullyQualifiedName~DirectDownloadServiceTests\|FullyQualifiedName~StoreDownloadServiceTests\|FullyQualifiedName~WindowsAppxDeploymentTests` |
| Native uninstall ownership, WinGet deadlines and removal verification | `FullyQualifiedName~WindowsUninstallCommandSourceTests\|FullyQualifiedName~WindowsInstalledProgramSourceTests\|FullyQualifiedName~WinGetPackageInstallerTests\|FullyQualifiedName~ExternalAppUninstallServiceTests` |
| AppX account discovery and removal | `FullyQualifiedName~AppxPackageSourceTests\|FullyQualifiedName~WindowsAppxDeploymentTests\|FullyQualifiedName~ExternalAppUninstallServiceTests` |
| Discovery caching, invalidation, provider overlap and registry matching | `FullyQualifiedName~AppStatusDiscoveryServiceTests` |
| Portable-app account paths and unavailable folders | `FullyQualifiedName~PortableAppAccountTests` |
| Desktop/AppX icons, caching and registration matching | `FullyQualifiedName~AppIconResolverTests\|FullyQualifiedName~DesktopAppIconSourceTests\|FullyQualifiedName~WindowsInstalledProgramSourceTests` |
| Leftover discovery, MSI states, installation folders and recovery | `FullyQualifiedName~LeftoverServiceTests\|FullyQualifiedName~WindowsInstallerInventoryTests\|FullyQualifiedName~UninstallResidueServiceTests` |
| Cleaner discovery, backup/recovery and path checks | `FullyQualifiedName~Cleaner` |
| Offline registry hives, lifecycle and Windows errors | `FullyQualifiedName~WindowsCleanerRegistryHiveAccessTests\|FullyQualifiedName~CleanerRegistrySessionsTests` |
| Windows servicing and cancellation | `FullyQualifiedName~WindowsServicingTests\|FullyQualifiedName~ProcessExecutorTests\|FullyQualifiedName~DismProcessRunnerTests` |
| Optimize orchestration and fake native scripts | `FullyQualifiedName~OptimizationToolsServiceTests\|FullyQualifiedName~NetworkTuningTests` |
| Security Windows boundary | `FullyQualifiedName~WindowsSecurityBackendTests` |
| Autoruns | `FullyQualifiedName~Autorun` |

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
# Language resources
dotnet test ./tests/WinOptimizer.IntegrationTests/WinOptimizer.IntegrationTests.csproj --filter FullyQualifiedName~Localization
```

`network-tuning-scripts.tests.ps1` is a worker invoked by `NetworkTuningTests`;
its payload is supplied by that suite.
