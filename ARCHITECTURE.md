# Architecture

[README.md](README.md) covers setup and usage. This document maps components,
state ownership and execution contracts to the implementation; feature guides
cover local rules and native boundaries.

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

Test hosts, boundaries and commands are in the [test guide](tests/README.md).

## Build configuration

| Owner | Responsibility |
| --- | --- |
| `global.json` / project manifests | SDK selection, frameworks, dependencies and x64 deployment |
| `Directory.Build.props` | Output roots, `WinOptimizerBuildRoot` and WinRT metadata selection |
| `Directory.Build.targets` | Release symbol-copy policy and compiler PDB output groups |
| `Version.props` | Shared date-version defaults, validation and application metadata |
| `build.ps1` / `extras/Resolve-MSBuild.ps1` | Local build entry point and Visual Studio discovery |
| UI publish targets | Runtime payload and compiled XAML resource index |
| `extras/build-and-package.ps1` | Dependency refresh, build/test/publish orchestration, symbol export, signing and installer inputs |
| `extras/win-optimizer.Installer.iss` | Installed payload, regular/portable integration and uninstallation |
| `build-installer.yml` / `release.yml` | CI validation/artifacts and tag-triggered publication |
| `CHANGELOG.md` | Curated release notes |

Build/restore use MSBuild's static graph. Packaging resolves one version, refreshes
bundled dependencies, builds Release, runs checks and publishes with `NoBuild=true`.
Its UI compilation check reuses Release project outputs. Symbols are exported
before signing; installer compilation cleans its unique temporary inputs on every exit.
The [development guide](README.md#development) owns commands, output paths and
release instructions; the [test guide](tests/README.md) owns validation commands.

## Startup, navigation and lifetime

`App` owns host and storage initialization. `MainWindow` hosts `StartupLoadingView`
and the full shell in one native window; `WindowSizeManager` owns saved placement.
The loader and shell share extended title-bar geometry. `App` restores language,
theme and shell preferences before their first presentation; `SoftwareAppsViewModel`
restores list presentation separately from inventory discovery.

`LogService.StartLogAsync` owns shared diagnostic initialization and its writer.
UI's `StartupOrchestrator` owns shell setup, deferred inventory and the initial
configuration backup; `StartupUiCoordinator` owns first-frame navigation/readiness.
`WindowsAppsViewModel` shares its load task. Infrastructure's `AppStatusDiscoveryService`
combines package/component evidence; `WindowsComponentStatusSource` serializes DISM.
The initial backup waits for that app load and has a 30-second wait limit without
cancelling the export. Its completion flag follows task return; the exporter logs
write failures internally, so the flag does not verify the backup file.

`NavigationRouter` maps details to owning sections. The main Frame owns the journal;
nested frames render content. Settings owns transient subscriptions and category
navigation. `ApplicationCloseService` owns close confirmation; the shutdown hook
drains operations and preference writes. `ExtensionApplicationLifetime` owns widget-only
lifetime and redirected launches; `SystemMonitorLaunch` identifies logon activation.

Application's `BackgroundUpdateCheckService` owns persistent update scheduling.
Infrastructure's `VersionService` owns release lookup/installer acquisition;
`UpdateCheckViewModel` owns UI dispatch and cancellation. Automatic scheduling
persists the UTC attempt time before enabled checks, including failed attempts.

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
restores drafts; setting view models retain their presentation baselines.
System-operation reservations prevent real writes from overlapping Test mode.
Exit restores presentation before clearing drafts; Apply captures edits, exits the
mode and invokes normal configuration execution. Failed/cancelled exports retain drafts.

`BuildVersionGate` owns Windows setting compatibility and message tokens, shared by
import checking, filtering, catalogue decoration and bulk actions.
`ConfigActionSelection` owns action-only sections for direct/reviewed imports.
`WimUtilViewModel` coordinates its steps; Infrastructure owns image/ISO operations
and UI owns wizard presentation and file pickers. Its XML step uses the local
Autounattend generator or a user-selected answer file; `WimCustomizationService`
copies selected XML and drivers into the extracted media.
`WimImageService` exports every edition and verifies the destination inventory before
replacing the source. `DriverCategorizer` owns package-relative payload paths;
`WimCustomizationService` integrates a returning driver script with existing
`SetupComplete.cmd` commands. Cancellation stops owned processes and retains verified output.

## Settings and presentation

`SettingsViewModel` owns categories, search and reset scope. Application's
`PreferenceSaveQueue<T>` orders snapshots; feature owners validate values and
Infrastructure's `UserPreferencesService` persists typed JSON. Shared preference
owners synchronize sidebar, color, updates and extension consumers.
Its atomic field-update operation owns read/merge/write synchronization;
`WindowSizeManager` submits only its five placement fields.

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
encoding. `AppIconResolver` owns versioned PNG caches and glyph fallbacks;
`BaseDefinition` owns the default icon-pack value. Vector authoring and generation
are documented in the [artwork guide](src/WinOptimizer.UI/Assets/ICON-ATTRIBUTION.md#interface-artwork).

`ExternalAppsService` merges the installation catalogue with `IInstalledProgramSource`
registrations, retaining catalogue identities for matching products and leaving
matching Windows Apps with their owning catalogue. `WindowsInstalledProgramSource`
reads machine and interactive-user uninstall roots in both registry views through
`IWindowsRegistryService`, including its alternate-account elevation redirection.
Discovered entries carry stable registry-derived IDs and exact registration paths;
installation status, metadata and fresh uninstall-command lookup use that identity.
`WindowsUninstallCommandSource` uses this shared inventory for catalogue entries,
requiring one exact-name or fingerprint match before rereading the command.
`WindowsAppxDeployment` owns interactive-account selection and the WinRT/process
boundary shared by AppX discovery, external-app removal and Store downloads.
Calling-account deployment uses WinRT; alternate-account removal targets the
interactive SID, while installation uses its process token and private staging.
`AppPaths` shares application-path expansion across metadata, icons, portable
discovery and removal through `IInteractiveUserService` folder bindings.

`AppStatusDiscoveryService` refreshes each installed definition's nullable `AppMetadata`
through `IAppMetadataSource`. Infrastructure's `WindowsAppMetadataSource` reads uninstall
registrations, matching AppX packages and interactive-user UserAssist history.
Discovery owns shared provider generations and registry-expression caches; metadata
owns package-size caches. Page view models reconcile inventory and batch presentation
updates. Shared WinGet/AppX reads survive until invalidation; old reads cannot
repopulate a new generation. UI's `AppSortHelper` supplies the comparison shared by
table, card and compact views, with localized groups and name/ID ties.

## Application removal

`ILeftoverService` owns standalone leftover scan selections, separately from app
uninstallation and configuration selection. Infrastructure's `WindowsLeftoverSource`
combines account-bound uninstall registrations, `IInstallerInventory` references
and cache discovery; `LeftoverDirectoryScanner` owns installation-folder evidence.
`IUninstallScanFolders` distinguishes dedicated installation roots from shared app-data
roots. Folder evidence is an obsolete installation record or an independently verified
empty installation leaf; recorded executable paths can establish missing optional ARP
locations. Current product identity and exact paths protect active data. Per-user cache
evidence retains its SID and Local AppData binding through backup and recovery.
`WindowsInstallerInventory` owns product/patch states; `WindowsInstallerSourceLists`
owns native repair-source enumeration and exact registration checks.
`WindowsLeftoverSource` also owns MSI activity, restart and queued-file protections.
Native all-user MSI coverage governs its managed cache. Account-hive gaps protect
bundle caches and data-containing folders and remain visible in scan notices; empty
installation leaves have an independent filesystem proof. Exact cache metadata
references protect their target or named directory without claiming sibling caches.
`LeftoverService` revalidates scan-issued IDs and uses
`UninstallBackupStore`/`UninstallResourceEditor` for typed registry and
file backup, deletion and recovery. Evidence retains its account and filesystem
boundary. `LeftoverAppsViewModel` owns the third tab's results and confirmation;
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

UI's `UninstallResidueReview` owns dialogs, dispatch and deferred review lifetimes;
`UninstallResidueNode` builds selection trees. `ExternalAppUninstallService` owns
native and automatic removal. The workflow passes outcomes to page view models,
which reconcile only verified removals under their inventory gate before residue
review. Provider invalidation does not trigger discovery. Configuration workflows
defer review until enclosing overlays close. Cleanup accepts current selectable IDs,
revalidates evidence and publishes a verified backup before deletion; recovery
preserves conflicting destination data.

`WinGetPackageInstaller` shares CLI execution and progress across install/uninstall.
Its removal check distinguishes explicit package absence from inventory failure
and propagates cancellation. `WinGetCliRunner` translates unlimited CLI timeouts
to `Timeout.Infinite` at the interactive-user process boundary. It owns linked
wall-clock and idle deadlines for all transports, renewing only the idle deadline
on output. `WinGetExitCodes` maps HRESULTs from the bundled WinGet release.
`WinGetOutputLines` owns transcript callbacks; `ConPtyProcess` owns terminal escape
parsing and progress formatting.

## Cleaner

The singleton `CleanerViewModel` owns scan/review presentation for both Cleaner
pages. `CleanerSettingsViewModel` owns validated options and per-area account
selections; `Cleaner.SelectedAreas` owns scan-area persistence.

Core's `CleanerService` deduplicates file paths across categories in stage order,
serializes scan/apply operations and retains the current
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

`CleanerPaths` owns directory boundaries and traversal; account sessions cache
folder bindings until disposal. `CleanerProgressReporter` coalesces pending UI
measurements. Privacy/junk inspection uses batches of 64 and up to four workers;
backup copies use at most four concurrent file payloads. Completed scans publish
immutable plans; selected backup preparation must finish before any mutation.
Failed/cancelled scans cannot supply a cleanup plan, and consumed findings cannot replay.

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
Only completed scans publish applicable reports. Fixes require confirmation and
normal mode, recheck evidence and verify readback; cancellation retains completed
receipts and active native write units finish before stopping.

## Drivers

Core's `DriverScanService` owns classification, matching, deduplication, version
comparison and WHQL filtering. `DriverRemovalService` owns selection revalidation
and per-item results. `DriverSettingsViewModel` owns saved scan options.

Infrastructure supplies inventory/update sources through PowerShell, DISM and
Windows Update. `WindowsDriverInfInspector` owns INF/catalog inspection;
`DriverScanWorkspace` owns private exports and paths; `WindowsDriverRemovalBackend`
executes PnPUtil identities. UI's transient `DriversViewModel` owns tasks/selection
and `DriverResultItem` owns row metadata. Removal rechecks exact package/device
identities; bound, inbox and boot-critical packages remain protected. Cancellation
stops between native removals and preserves completed results.

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

`ManagementPowerShellRunner` materializes embedded scripts into exclusive temporary
files. An encoded loader hashes the bytes read from disk before executing that
same buffer; request JSON travels separately as base64 data. The runner deletes
temporary scripts after command completion. `Sysinternals-Cache.ps1` owns
allowlisted Microsoft tool acquisition, Authenticode validation, reparse checks
and executable read locks for Autorunsc and Handle. Each feature guide defines its
consent, executable, arguments and cache subdirectory.

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
discovery. `SystemMonitorMetric` carries typed readings across layers. Samples do
not overlap; new/reset counters establish baselines. Histories stay bounded in
memory, and shutdown drains native reads before releasing handles.

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

`AppDataPaths` in Core owns the layout beneath `%ProgramData%/win-optimizer`;
path resolution performs no IO. Infrastructure's `AppDataStorage` owns explicit
initialization and validates ownership, permissions and filesystem boundaries.
Shared executables, machine state and recovery data are writable only by
Administrators and SYSTEM; account folders additionally grant their account access.
`InitializeAsync` completes before preferences, logging and feature work. It
normalizes launching-account-owned directories to administrator ownership and
protected permissions, rejects other owners except Administrators and SYSTEM,
and rejects links and junctions before use.

Inno Setup owns retention/deletion of the shared store and targeted Windows trace
cleanup, separately from tracked installation files. Profile registry sessions
mount offline hives only for cleanup and release them in a `finally` block. Task Scheduler
owns task and folder removal through its COM API.
[User guidance](README.md#preferences-and-app-updates) documents retention choices
and recovery consequences. Full cleanup validates the store/profile/Prefetch
boundaries before deleting traces and shared data; access, mount or deletion
failures stop uninstallation before tracked payload removal.

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
