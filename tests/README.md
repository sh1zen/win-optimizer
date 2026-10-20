# Tests

Run commands from the repository root using the Windows toolchain in the
[development guide](../README.md#development). Native UI hosts need cached NuGet
packages and use synthetic data with fake OS boundaries. Test output and previews
belong under `build/`.

UI coverage focuses on configuration and task workflows, persistence, cancellation,
navigation and native page rendering with isolated Windows boundaries.
Core checks authored catalogue metadata and export routing for every feature group;
Infrastructure covers bulk-action requests and recommendation selection.

## Main suites

```powershell
./extras/run-win-optimizer-tests.ps1
```

The runner executes Core, Application, Infrastructure and integration suites,
saving TRX results under `build/test-results/<run ID>/`. It separately **builds**
UI tests under `build/ui-test-build/`; run those tests in Visual Studio Test Explorer.
`-SkipUITests` omits that build check; `-IntegrationOnly` runs only integration tests.

Focused native UI runners share `Build-NativeTestHost.ps1` for MSBuild discovery,
cached NuGet restore and executable validation. Each runner owns its build location
and scenarios. `NUGET_PACKAGES` selects the cache; otherwise the user cache is used.
Startup and Software & Apps share `NativeTestCases` for fact/inline-data execution
and fixture disposal on the WinUI dispatcher.

## Focused checks

| Area | Command |
| --- | --- |
| Build-date version, release overrides and application metadata | `./tests/versioning.tests.ps1` |
| Packaging signing decisions and noninteractive option validation | `./tests/packaging.tests.ps1` |
| Installer compilation, destination/mode navigation and PowerShell launcher | `./tests/installer.tests.ps1` (Inno Setup 6; optional `-InnoCompiler <path-to-ISCC.exe>`) |
| Built installer's version, portable payload, bundled runtimes and languages | `./tests/packaged-payload.tests.ps1` (after packaging) |
| Configuration and preference queues | `dotnet test ./tests/WinOptimizer.Application.Tests/WinOptimizer.Application.Tests.csproj` |
| Optimize network/feature orchestration and fake native scripts | `dotnet test ./tests/WinOptimizer.Infrastructure.Tests/WinOptimizer.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~OptimizationToolsServiceTests\|FullyQualifiedName~NetworkTuningTests'` |
| Network UI | `./tests/run-network-tests.ps1` |
| Optimize production scripts with fake Windows boundaries | `powershell.exe -NoProfile -File ./tests/optimization-tools-scripts.tests.ps1` |
| Security scan authority | `dotnet test ./tests/WinOptimizer.Core.Tests/WinOptimizer.Core.Tests.csproj --filter FullyQualifiedName~SecurityScanServiceTests` |
| Security Windows boundary | `dotnet test ./tests/WinOptimizer.Infrastructure.Tests/WinOptimizer.Infrastructure.Tests.csproj --filter FullyQualifiedName~WindowsSecurityBackendTests` |
| Security production scripts with fake Windows boundaries | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./tests/security-scripts.tests.ps1` |
| Security UI | `./tests/run-security-tests.ps1` |
| Context-menu UI | `./tests/run-context-menu-tests.ps1` |
| Software & Apps UI | `./tests/run-software-apps-tests.ps1` |
| Desktop/AppX icon extraction, caching and registration matching | `dotnet test ./tests/WinOptimizer.Infrastructure.Tests/WinOptimizer.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~AppIconResolverTests\|FullyQualifiedName~DesktopAppIconSourceTests\|FullyQualifiedName~WindowsInstalledProgramSourceTests'` |
| Autorun storage | `./tests/autoruns-storage.tests.ps1` |
| Autorun UI | `./tests/run-autoruns-tests.ps1` |
| Process monitor UI | `./tests/run-process-monitor-tests.ps1` |
| SysMonitor preferences and widget scaling | `./tests/run-system-monitor-tests.ps1` |
| Windows servicing UI | `./tests/run-windows-status-tests.ps1` |
| Popups and technical details | `./tests/run-popup-tests.ps1` |
| Topic journal and history | `./tests/run-journal-tests.ps1` |
| Startup, technical-detail rows, navigation, saved Wi-Fi and original icon coverage/rendering | `./tests/run-startup-tests.ps1` |

## Test boundaries

Installer regressions create isolated installations in the Windows temporary
folder, which must support local NTFS junctions. They use unique registration IDs
and redirect application storage into the fixture.
Uninstaller scenario logs are saved under `build/logs/`; installer checks create
the directory before collecting logs.

See the [language-resource guide](../src/WinOptimizer.UI/Features/Common/Localization/README.md)
for translation coverage and format validation.

## Domain filters

Run a test project with `dotnet test <project> --filter <filter>`. These filters
complement the focused scripts above:

| Area | Project under `tests/` | Filter |
| --- | --- | --- |
| Autoruns | `WinOptimizer.Infrastructure.Tests/WinOptimizer.Infrastructure.Tests.csproj` | `FullyQualifiedName~Autorun` |
| Leftover discovery, MSI product states, installation folders and recovery | `WinOptimizer.Infrastructure.Tests/WinOptimizer.Infrastructure.Tests.csproj` | `FullyQualifiedName~LeftoverServiceTests\|FullyQualifiedName~WindowsInstallerInventoryTests\|FullyQualifiedName~UninstallResidueServiceTests` |
| Offline registry hive arguments, lifecycle and Windows error reporting | `WinOptimizer.Infrastructure.Tests/WinOptimizer.Infrastructure.Tests.csproj` | `FullyQualifiedName~WindowsCleanerRegistryHiveAccessTests\|FullyQualifiedName~CleanerRegistrySessionsTests` |
| Windows servicing and cancellation | `WinOptimizer.Infrastructure.Tests/WinOptimizer.Infrastructure.Tests.csproj` | `FullyQualifiedName~WindowsServicingTests\|FullyQualifiedName~ProcessExecutorTests\|FullyQualifiedName~DismProcessRunnerTests` |
| Language resources | `WinOptimizer.IntegrationTests/WinOptimizer.IntegrationTests.csproj` | `FullyQualifiedName~Localization` |

Quote filters containing `|` when invoking PowerShell.
`network-tuning-scripts.tests.ps1` is a fixture worker invoked by
`NetworkTuningTests`; its payload is supplied by that suite.
