# Architecture

[README.md](README.md) covers setup and usage; [PIPELINE.md](PIPELINE.md) covers
execution order, cancellation and recovery. This document maps responsibilities
and state ownership to the implementation.

## Project boundaries

| Project | Owns | Project dependencies |
| --- | --- | --- |
| `WinOptimizer.Core` | Domain models, catalogues, state, contracts and domain services such as Cleaner and driver classification | None |
| `WinOptimizer.Application` | Configuration presets/load/export/execution/review, uninstall orchestration, background update scheduling and preference save queues | Core |
| `WinOptimizer.Infrastructure` | Windows APIs/processes, setting handlers, app discovery/removal, persistence and hardware access | Core, WindowsPackageManager.Interop |
| `WinOptimizer.UI` | WinUI pages, controls, view models, navigation and desktop adapters | Core, Application, Infrastructure |
| `WindowsPackageManager.Interop` | WinGet WinRT projections, metadata and COM factories | None |

`CompositionRoot` calls `AddInfrastructureServices`, `AddApplicationServices` and
`AddUIServices`. Application and Infrastructure do not reference each other or UI.
Infrastructure's `AddSettingServices` registers application/discovery dispatchers
and their handlers; the power interface and handler share one singleton.

Tests isolate external boundaries; native hosts exercise production pages with
synthetic data. `tests/Build-NativeTestHost.ps1` owns their common build setup;
individual runners own scenarios. See the [test guide](tests/README.md).

## Build configuration

`global.json` selects the SDK. Project manifests own frameworks, dependencies and
x64 deployment; `Directory.Build.props` owns output roots and WinRT metadata
selection. `WinOptimizerBuildRoot` redirects generated output. CsWinRT projections
and WinGet runtime/metadata copies are incremental.

`build.ps1` is the local/packaging entry point; `extras/Resolve-MSBuild.ps1` owns
Visual Studio discovery. `Version.props` owns version defaults, validation and
assembly/product metadata. The UI imports it; packaging passes its resolved value
through build, publish and Inno Setup. MSBuild owns the payload in `build/publish/`.
The [pipeline](PIPELINE.md#build-and-packaging) defines stage ordering;
the [development guide](README.md#development) contains commands and CI usage.

## Startup, navigation and lifetime

`App` activates `MainWindow` with a service-independent `StartupLoadingView` before
loading feature resources or building the host. After storage and localization are
ready, `MainWindow.InitializeShell` retains that loader inside the full shell in
the same native window. Automatic widget launches keep the window hidden.

`LogService.StartLogAsync` owns shared diagnostic initialization and its writer.
UI's `StartupOrchestrator` owns shell setup, deferred inventory and the initial
configuration backup; `StartupUiCoordinator` owns first-frame navigation/readiness.
`WindowsAppsViewModel` shares its load task. Infrastructure's `AppStatusDiscoveryService`
combines package/component evidence; `WindowsComponentStatusSource` serializes DISM.

`NavigationRouter` maps details to owning sections. The main Frame owns the journal;
nested frames render content. Settings owns transient subscriptions and category
navigation. `ApplicationCloseService` owns close confirmation; the shutdown hook
drains operations and preference writes. `ExtensionApplicationLifetime` owns widget-only
lifetime and redirected launches; `SystemMonitorLaunch` identifies logon activation.

Application's `BackgroundUpdateCheckService` owns persistent update scheduling.
Infrastructure's `VersionService` owns release lookup/installer acquisition;
`UpdateCheckViewModel` owns UI dispatch and cancellation. Execution order is in
[startup](PIPELINE.md#application-startup-and-exit) and [updates](PIPELINE.md#in-app-updates).

## Configuration and application mode

Core's `ConfigurationItem` defines `.win-optimizer` records. Application owns
presets and load/export/execution/review through its configuration services.
UI adapters supply app selections, pickers, dialogs and overlays.
`FeatureConfigurationSnapshot` collects and groups discovered state for configuration
and answer-file exports; each exporter owns its item format.
`WindowsAppConfigurationMapper` owns record projection for exports, WIM and
Autounattend, including localized names and AppX/capability/feature precedence.

Core's singleton `ConfigReviewService` owns application mode, review differences,
Test-mode drafts (`Builder`) and typed `BuilderEdit` snapshots. Infrastructure aliases
its mode/review/diff/badge interfaces to that instance. `SettingViewModelFactory`
restores drafts; setting view models retain their presentation baselines. System-operation reservations
coordinate real writes with Test mode; the [configuration pipeline](PIPELINE.md#configuration-and-test-mode)
defines transitions and export/apply semantics.

`BuildVersionGate` owns Windows setting compatibility and message tokens, shared by
import checking, filtering, catalogue decoration and bulk actions.
`ConfigActionSelection` owns action-only sections for direct/reviewed imports.
`WimUtilViewModel` coordinates its steps; Infrastructure owns image/ISO operations
and UI owns wizard presentation and file pickers. Its XML step uses the local
Autounattend generator or a user-selected answer file; `WimCustomizationService`
copies selected XML and drivers into the extracted media.

## Settings and presentation

`SettingsViewModel` owns categories, search and reset scope. Application's
`PreferenceSaveQueue<T>` orders snapshots; feature owners validate values and
Infrastructure's `UserPreferencesService` persists typed JSON. Shared preference
owners synchronize sidebar, color, updates and extension consumers.

Optimize/Customize share `SectionPageView`, `BaseSettingsFeatureViewModel` and
`SettingsLoadingService`. `CompatibleSettingsRegistry` owns filtered/authoring
snapshots and an ID-to-definition/owner index. `ComboBoxValueResolver` matches
observations to catalogue options; `ComboBoxResolver` adds discovery/index access.
`TooltipDataService` owns technical-detail eligibility and metadata snapshots;
`TechnicalDetailsButton` and `TechnicalDetailsManager` own their presentation/lifetime.

`ContextMenuViewModel` owns program groups and filtering; `ProgramIconSource` loads
local artwork with bounded concurrency. `ContextMenuService` owns guarded operations
and inventory authority; `WindowsContextMenuStore` owns registry scopes, reversible
registration edits and readback.

`NetworkOptimizationViewModel` and `FeatureOptimizationViewModel` use
`OptimizationToolsService` for guarded native/settings operations. Core's
`NetworkTuningCatalog` owns control IDs, source/write parameters and constraints;
the [network guide](src/WinOptimizer.Infrastructure/Features/Optimize/README.md)
defines their boundary formats. Feature actions use `ISettingApplicationService`
and `ISystemSettingsDiscoveryService`.

| Shared owner | Responsibility |
| --- | --- |
| `PageHeader`, `NavigationCard`, `PageStyles.xaml` | Page/card layout, shared margins and scrollbar insets |
| `AppColorResources`, `FeatureIcons.xaml`, `CustomIconCatalog` | Theme/accent/high-contrast resources, original vector artwork and semantic icon-name lookup |
| `PopupHandler`, `PopupStyles.xaml`, `DialogService` | Modal queue, native popup construction, theme/bounds and domain dialog adapter |
| `TaskProgressViewModel` | Operation lifecycle and footer cancellation; pages own local loading state |
| `ScrollBarAutoHide` | Control-scoped idle visibility and input subscriptions |
| `AppBranding` | UI image URI and shell icon path; [application artwork](src/WinOptimizer.UI/Assets/ICON-ATTRIBUTION.md) |

## App inventory and sorting

`AppIconResolver` reads installed-package artwork through `IAppxIconSource` and
registered desktop `DisplayIcon` resources through `IDesktopAppIconSource`.
`DesktopAppIconSource` validates local paths, expands interactive-user variables,
preserves signed resource indices and releases native icon/GDI handles after PNG
encoding. Serialized cache writes use definition ID plus package version or desktop
path/index, size and modification time. Missing artwork uses category glyphs.
Navigation and the Custom icon pack share original drawings in `FeatureIcons.xaml`;
`extras/generate-icons.py` generates the Custom resources. Explicit Fluent symbols
use the declared library; `BaseDefinition` owns the default icon-pack value.

`ExternalAppsService` merges the installation catalogue with `IInstalledProgramSource`
registrations, retaining catalogue identities for matching products and leaving
matching Windows Apps with their owning catalogue. `WindowsInstalledProgramSource`
reads machine and interactive-user uninstall roots in both registry views through
`IWindowsRegistryService`, including its alternate-account elevation redirection.
Discovered entries carry stable registry-derived IDs and exact registration paths;
installation status, metadata and fresh uninstall-command lookup use that identity.
The External Apps view model reconciles inventory on refresh while preserving
existing view models and selections.

`AppStatusDiscoveryService` refreshes each installed definition's nullable `AppMetadata`
through `IAppMetadataSource`. Infrastructure's `WindowsAppMetadataSource` reads uninstall
registrations, matching AppX packages and interactive-user UserAssist history. Metadata
failures are logged independently of installation status. UI's `AppSortHelper` owns one
comparison for table, card and compact ordering, with localized groups and name/ID ties.

## Application removal

`ILeftoverService` owns standalone leftover scan selections, separately from app
uninstallation and configuration selection. Infrastructure's `WindowsLeftoverSource`
combines account-bound uninstall registrations, `IInstallerInventory` product/patch
references and bounded cache discovery. `LeftoverDirectoryScanner` captures aged,
non-executable installation remnants
from stale registration paths within machine application roots and account AppData;
active directory references protect overlapping paths. Scan results contain only
eligible selections. `WindowsInstallerInventory` distinguishes advertised products
from installed products before querying installed-only cache properties.
`WindowsInstallerSourceLists` owns native source enumeration and read-only checks
for absent source-list keys in the product's exact registry account/context.
`LeftoverService` revalidates scan-issued IDs and uses
`UninstallBackupStore`/`UninstallResourceEditor` for typed registry and
file backup, deletion and recovery. Installer-file evidence carries an explicit
cache boundary; ordinary uninstall residue retains its existing system-directory
protections. `LeftoverAppsViewModel` owns the third tab's results and confirmation;
`SoftwareAppsViewModel` routes tab/search/actions and excludes cleanup from Builder
and configuration-review modes. Local rules are in the [leftover guide](src/WinOptimizer.Infrastructure/Features/SoftwareApps/Leftover/README.md).

Application's `AppUninstallWorkflow` is shared by Windows Apps, External Apps and
configuration execution. Core defines native outcomes, verification states and
opaque selectable resource IDs. Infrastructure's `UninstallResidueService` owns
inspection sessions, targeted folder monitoring and cleanup/recovery coordination:

- `UninstallDiscovery` resolves products, registrations and account paths;
  `UninstallInspection` rechecks installation/process evidence and shared references.
- `IUninstallRegistryProfiles` includes registered user/service profiles;
  `IUninstallAccountSessions` reuses `WindowsCleanerRegistrySessions` for live and
  offline hive lifetimes. Its native mount boundary scopes backup/restore privileges
  and preserves Windows status codes in reported failures. Cleaner retains its own selected account scopes.
- `UninstallResourceEditor` revalidates exact resource evidence before mutation.
- `UninstallBackupStore` owns authenticated `manifest.uninstall` sessions and
  verified payloads in the Cleaner vault, with a separate format/recovery list.

UI's `UninstallResidueReview` owns dialogs and dispatch; `UninstallResidueNode`
builds resource-selection trees. Deferred configuration reviews retain their
inspection session and operation reservation until presentation completes.

`WinGetOutputLines` owns transcript buffering and progress/permanent-line callbacks
for redirected and ConPTY output. `ConPtyProcess` owns terminal escape parsing and
progress-track formatting.

## Cleaner

The singleton `CleanerViewModel` owns scan/review presentation for both Cleaner
pages. `CleanerSettingsViewModel` owns validated options and per-area account
selections; `Cleaner.SelectedAreas` owns scan-area persistence.
`CleanerFileScanner` owns allowlisted profile/machine sources and inventories
protected files as non-actionable findings. Selection and scan sequencing are
defined in the [Cleaner pipeline](PIPELINE.md#cleaner).

Core's `CleanerService` serializes scan/apply operations and retains the current
immutable evidence and scan-owned options. Callers submit finding IDs, not rebuilt
targets. `ICleanerBackend` separates domain ordering from Windows work:

| Infrastructure owner | Responsibility |
| --- | --- |
| `CleanerFileScanner` / `CleanerAccounts` | Profile-scoped file/privacy/shortcut discovery, source labels and account/root evidence |
| `CleanerFileDeletion` | Exclusive-handle path/metadata validation and deletion |
| `CleanerRegistryScanner` / `WindowsCleanerRegistryEditor` | Typed reference discovery, registry views, per-account probe caches and revalidation before deletion |
| `RegistryHiveCompactor` | Selected hive analysis, optional durable backups and native compaction |
| `CleanerBackupService` | File/value payload verification, manifests and conflict-preserving recovery |
| `WindowsCleanerBackupVault` | Protected directories, ownership/reparse checks and account-bound DPAPI metadata |

Disk maintenance uses `defrag.exe`. `ICleanerProcessMonitor` isolates application
activity checks. `GroupedResultsList` and `SelectableResultGroup` reference the
same result items as cleanup and own category selection/expansion presentation.
The shared selection contract is `ISelectableResultItem`.
Its optional `ResultReport` supplies ordered presentation sections and label/value
rows. `CleanerDiskReport` structures native disk output without changing the scan
evidence; `GroupedResultsList` selects the inline report template for these items.

Cleaner's Events cleanup card opens `EventsCleanupPage` with Cleaner as its sidebar
owner. `EventLogCleanupService` owns serialization, selection authorization and mode
reservations; `WindowsEventLogStore` owns local channel APIs and protected EVTX storage.

## Security

Core's `SecurityScanService` owns serialized scans/fixes and immutable report
authority. `SecurityCheck` maps standard/deep checks; fixes carry selected check IDs
and consume their evidence. `ISecurityBackend` isolates native work.

Infrastructure's `WindowsSecurityBackend` owns encoded PowerShell requests,
interactive-user targeting and atomic read/compare/write/readback units. Existing
privacy checks use the settings owners. UI's singleton `SecurityViewModel` owns
the report, selection and task presentation; `SecurityPage` owns responsive layout.
The [security pipeline](PIPELINE.md#security) defines confirmations, cancellation,
mode gates, unavailable evidence and verification.

## Drivers

Core's `DriverScanService` owns classification, matching, deduplication, version
comparison and WHQL filtering. `DriverRemovalService` owns selection revalidation
and per-item results. `DriverSettingsViewModel` owns saved scan options.

Infrastructure supplies inventory/update sources through PowerShell, DISM and
Windows Update. `WindowsDriverInfInspector` owns INF/catalog inspection;
`DriverScanWorkspace` owns private exports and paths; `WindowsDriverRemovalBackend`
executes PnPUtil identities. UI's transient `DriversViewModel` owns tasks/selection
and `DriverResultItem` owns row metadata. See the [driver pipeline](PIPELINE.md#drivers).

## Extension and Control

`UtilitiesPage` presents SysMonitor in Extension. `ManagementPage` presents the
environment editor, hosts editor and battery charge controls alongside the other
Control tools. Detail pages retain their owning sidebar section in the shell journal.

Singleton utility view models retain drafts across navigation. `UtilityViewModel`
owns mode/localization subscriptions and operation presentation. Explicit page
entry loads tool data; writes use normal-mode reservations and shutdown draining.

| Feature | Owners and boundaries |
| --- | --- |
| Environment editor | `EnvironmentEditorService` serializes compare-before-write edits; `WindowsEnvironmentVariableStore` preserves raw registry string types and interactive-user targeting. PATH rows project the same raw draft. |
| Hosts editor | Core's `HostsText` / `HostsSyntax` own parsing and validation. `HostsEditorViewModel` owns one draft; `HostsEditorService` owns the fixed path, revision, encoding, backup and atomic replacement. |
| Battery | `BatteryChargeService` selects OEM providers and confirms firmware readback. `BatteryTelemetryReader` joins standard WMI sensors; `BatterySupportCatalog` owns official setup routes. See the [provider guide](src/WinOptimizer.Infrastructure/Features/Utilities/README.md). |
| Connection | `ConnectionService` owns adapter/DNS/VPN inventory; `ConnectionTrafficService` owns sampling and history queries. Core's `ConnectionTrafficWindow` owns bounded counter deltas; `WindowsConnectionHistory` isolates Windows-retained profile usage. DNS uses the existing `gaming-dns-server` setting. |
| Saved Wi-Fi | `SavedWifiService` checks account scope; `WindowsSavedWifi` enumerates native WLAN profiles and reads keys in memory. The transient `SavedWifiViewModel` owns a page-scoped snapshot, with no persistence or logging of credentials. |
| Windows servicing | `WindowsServicingService` owns typed phase planning, restart gates and cancellation. `WindowsServicingBackend` owns privileges, source validation and DISM/SFC process boundaries, forwarding cancellation to the shared process runner for owned process-tree termination. See the [servicing guide](src/WinOptimizer.Infrastructure/Features/Management/README.md). |
| Autoruns | `AutorunService` owns snapshot/write authority; `AutorunPowerShellBackend` owns native scripts; `AutorunCsvReader` validates advanced inventory. See the [autorun guide](src/WinOptimizer.Infrastructure/Features/Management/AUTORUNS.md). |
| Process monitor | `ProcessMonitorService` owns serialized samples and selected-process resources; `WindowsProcessSource`, `WindowsProcessPorts`, `WindowsProcessFiles` and `WindowsProcessNetwork` own native boundaries. `ProcessTrafficLedger` aggregates bounded ETW totals. See the [measurement guide](src/WinOptimizer.Infrastructure/Features/Management/PROCESS-MONITOR.md). |

`ManagementPowerShellRunner` authenticates embedded script bytes and passes encoded
request data. Its shared `Sysinternals-Cache.ps1` owns allowlisted Microsoft tool
acquisition, signature validation and executable locks for Autorunsc and Handle.

## SysMonitor

`ExtensionsViewModel` owns validated `SystemMonitorOptions` under
`Extensions.SysMonitor`. `SystemMonitorStartupService` coordinates preference
persistence and the SID-scoped logon task through `IScheduledTaskService`.

`SystemMonitorWidgetHost` owns the window and sampling lifetime.
`SystemMonitorReadingsViewModel` formats snapshots and retains CPU history;
`MonitorMetricSection` owns per-sensor histories. `SystemMonitorPanel`,
`SystemMonitorWindow` and `SystemMonitorMetricChart` own layout, native sizing and charts.

Infrastructure's `WindowsSystemMonitorService` serializes samples and calculates
deltas through `ISystemMonitorProbe`. `WindowsSystemMonitorProbe` owns native/WMI
reads; `HardwareMonitorSensors` owns a lazy LibreHardwareMonitor session and PawnIO
discovery. `SystemMonitorMetric` carries typed readings across layers. Telemetry
stays in memory; the [sampling pipeline](PIPELINE.md#sysmonitor) defines baselines,
bounded histories, cancellation and handle release.

## System information and Restore

`WindowsSystemInformationService` owns query definitions, normalization and the
last completed `ISystemInformationService.CachedSnapshot`. `WindowsSystemInformationQuery`
isolates local WMI calls and native object lifetimes. Transient page view models
reuse that snapshot; explicit refresh replaces it. Typed values, availability and
timestamp cross the UI boundary unchanged. `ISystemInfoProvider` serves the
separate diagnostic-log summary.

`RestoreViewModel` owns configuration commands and restore-point presentation.
`IConfigurationService` handles configurations; `ISystemRestoreService` owns
protection state, point inventory and targeted deletion.
`WindowsRestorePointStore` isolates WMI and `SRRemoveRestorePoint` calls on worker
threads. `ISystemBackupService` owns point creation and verification.

## Storage

`AppLogEntry.Topic` stores a stable subject. `LogService.BeginTopic` scopes it to
the current asynchronous operation; nested and concurrent flows retain independent
topics. Original setting receipts keep their typed kind, group, values and batch.
`ActivityJournalService` in Application merges saved diagnostics and receipts through
the existing storage contracts. A query session owns bounded source buffers and
reverse byte cursors, without keeping files open between pages.

`LogsAndHistoryViewModel` owns topic/type/search controls and grouped presentation.
Groups reference the original presentation rows; a new page preserves expansion.
`LogTopics` assigns message subjects when a writing workflow has no explicit topic.
Readers use the topic stored in each diagnostic record.

`AppDataPaths` in Core owns the layout beneath `%ProgramData%/win-optimizer`.
Path resolution performs no IO. Infrastructure's `AppDataStorage.InitializeAsync`
explicitly prepares administrator-owned shared storage and private account folders
before preferences, logging and feature services start. Shared executables, machine
state and recovery data are writable only by Administrators and SYSTEM; account
folders additionally grant their account access. Existing directories owned by
the launching account receive administrator ownership and protected permissions
before use; directories owned by other accounts are rejected. Storage validation
rejects links and junctions before use.

Inno Setup owns retention/deletion of the shared store, separately from tracked
installation files. The [packaging pipeline](PIPELINE.md#build-and-packaging)
documents cleanup validation and ordering; [user guidance](README.md#preferences-and-app-updates)
documents the choices and recovery consequences.

| Data | Subpath / owner |
| --- | --- |
| Personal preferences | `Users/<interactive SID>/Config/UserPreferences.json`; `UserPreferencesService` |
| Configuration snapshots including user settings | `Users/<interactive SID>/Backups/Configurations`; Application configuration services |
| Diagnostic sessions and startup log | `Logs`; Core logging services |
| Change receipts | `History/ChangeHistory.jsonl`; `ChangeHistoryService` |
| Cleaner and uninstall recovery | `Backups/Cleaner/<protecting account SID>`; authenticated, account-bound vault |
| Hosts, event archives and power plans | `Backups/Hosts`, `Backups/EventLogs`, `Backups/PowerPlans` |
| Shared app icons and WinGet exports | `Cache/Icons`, `Cache/WinGet` |
| Optional Microsoft tools | `Tools/autoruns`, `Tools/handle`; verified Sysinternals cache |
| Persistent removal scripts | `Scripts` |
| Windows installation customization scripts and logs | `Unattend/Scripts`, `Unattend/Logs` |
| Windows URL integration payloads | `OpenWebSearch`; registered Windows commands reference these files |
| Installer downloads and extracted app payloads | `Downloads`, `Apps` |
| Temporary exports, scripts and WIM work | `Temp`; operation owners clean their workspaces |
| Machine completion and disabled startup state | `State/OneDrive`, `State/Autoruns` |
| Account completion and disabled startup state | `Users/<SID>/State/Customizations`, `Users/<SID>/State/Autoruns` |

Generated and embedded PowerShell share `AppStorageScript` for path validation,
verified atomic JSON publication and completion markers. Disabled registry startup
entries retain their command and registry type in JSON; disabled startup files
retain their bytes and source metadata on disk. Registry writes implement Windows
configuration, startup restoration and Windows integration such as file
associations, uninstall registration and Registry Editor navigation.

`LogPageReader` supplies bounded forward and newest-first UTF-8 pages with byte cursors.
Newest-first reads scan backwards in blocks instead of rereading whole sessions. Readers accept
JSON Lines records with the current typed fields; diagnostic retention runs on session
startup, removing sessions older than 30 days and retaining at most 50 previous
sessions. Clearing diagnostics serializes with the active writer and keeps it
usable; change history has no clear operation. System-information snapshots,
process observations and widget telemetry remain in memory.
