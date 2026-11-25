# Architecture

Use the [getting started guide](docs/GETTING-STARTED.md) to install and use the
app. This document describes the components that own data and operations.
Feature references describe local rules and Windows interfaces.

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
| Project manifests | Frameworks, dependencies and x64 deployment |
| `global.json` | Shared .NET SDK selection for local builds and CI |
| `Directory.Build.props` | Output roots, `WinOptimizerBuildRoot`, shared Windows projection version and WinRT metadata selection |
| `Directory.Build.targets` | Release symbol-copy policy and compiler PDB output groups |
| `Version.props` | Shared date-version defaults, validation and application metadata |
| `build.ps1` / `extras/Resolve-MSBuild.ps1` | Local build entry point, exclusive output-directory lock and Visual Studio discovery |
| UI publish targets | Runtime payload, compiled XAML resource index and distribution license files |
| `extras/build-and-package.ps1` | Dependency refresh, build/test/publish orchestration, symbol export, signing and installer inputs |
| `extras/win-optimizer.Installer.iss` | Installed payload, regular/portable integration and uninstallation |
| `build-installer.yml` / `release.yml` | CI validation/artifacts and tag-triggered publication |
| `CHANGELOG.md` | Curated release notes |
| `THIRD-PARTY-NOTICES.txt` | Dependency versions, component attribution and license notices |

`build.ps1` holds an exclusive, delete-on-close file handle per build root while
running MSBuild and validating its output. Build/restore use MSBuild's static graph.
Packaging resolves one version, refreshes
bundled dependencies, builds Release, runs checks and publishes with `NoBuild=true`.
Its UI compilation check reuses Release project outputs. Symbols are exported
before signing. Application signing uses the published dependency manifest to
select project assemblies and includes the application and companion hosts.
Inno Setup signs the installer and its generated executables. Packaging verifies
the embedded signatures before accepting the selected signed outputs.
Installer compilation cleans its unique temporary inputs on every exit.
The WinUI project excludes Mono's native assets from the Windows payload.
LibreHardwareMonitor uses the Windows hardware path on this target.
The packaged installer test runs in the disposable GitHub-hosted Windows VM in
CI and uses Windows Sandbox for local development.
See the [development guide](docs/DEVELOPMENT.md) for build commands, output
paths, and release instructions. See the [test guide](tests/README.md) for
validation commands.

## Startup, navigation and lifetime

`App` owns host and storage initialization. `MainWindow` hosts `StartupLoadingView`
and the full shell in one native window; `WindowSizeManager` owns saved placement.
The loader and shell share extended title-bar geometry. `App` uses one preference
snapshot for language, theme, window placement, sidebar, accent and main-window blur.
It applies each before its first presentation. `WindowBackgroundPreferencesViewModel`
shares the live blur setting with Settings and queues saves. The shell and Preview
use `TintedDesktopBackdrop` to manage desktop acrylic with their own tint and opacity.
`SoftwareAppsViewModel` restores list
presentation separately from inventory discovery.

`LogService.StartLogAsync` owns shared diagnostic initialization and its writer.
UI's `StartupOrchestrator` owns shell setup, deferred inventory and the initial
configuration backup; `StartupUiCoordinator` owns first-frame navigation/readiness.
The settings catalogue overlaps the power compatibility checks with construction
of the other features, and publishes all completed snapshots in catalogue order.
`WindowsAppsViewModel` shares its load task. Infrastructure's `AppStatusDiscoveryService`
combines package/component evidence. `WindowsComponentStatusSource` reads capabilities
and features in one serialized DISM session, with a separate deadline per query.
AppX metadata reuses fresh registrations from the same account when available;
the installation detection cache retains package names.
AppX artwork inventory reads lower-priority account scopes only for unresolved
names. The resolver overlaps logo reads and publishes cache entries in list order.
The initial backup follows inventory loading. Startup records completion only if
the export succeeds within its 30-second wait. Failed or timed-out attempts retry
on the next launch; timed-out exports continue with later failures logged.

`NavigationRouter` maps details to owning sections. The main Frame owns the journal;
nested frames render content. `PageFadeTransition` animates the shell frame's opacity
after navigation commits and honors the Windows animation setting.
Settings owns transient subscriptions and category
navigation. `ApplicationCloseService` obtains consent before the shutdown hook drains
operations and preference writes, then waits for the captured task to release its
resources. Closing the main window exits the shell. Updates first drain the Preview
and desktop widget companions, then launch the installer and exit the shell.

Application's `BackgroundUpdateCheckService` owns persistent daily update
scheduling. It waits 30 seconds at startup and saves the UTC attempt time before
running the enabled checks. `RemovalScriptUpdateService` refreshes the shared
sign-in script under the same lock used for saving removal selections.
Infrastructure's `VersionService` owns release lookup and installer acquisition.
The scheduler reports successful release lookups to `UpdateCheckViewModel`, which
owns UI dispatch, cancellation and the title-bar summary.
Release lookup failures propagate to the manual check's error presentation.
`VersionService` pins the installer download to the tag that supplies its
GitHub SHA-256 digest. It verifies the staged file against that digest and
validates its executable structure before making it available for relaunch.

## Configuration and application mode

Core's `ConfigurationItem` defines `.win-optimizer` records. Application owns
presets and load/export/execution/review through its configuration services.
UI adapters supply app selections, pickers, dialogs and overlays.
Direct and reviewed imports use the same executor for settings results, verified
Windows Apps removal outcomes and shell refresh after writes. A completed import
can start the separately selected external-app removal.
`FeatureConfigurationSnapshot` collects and groups discovered state for configuration
and answer-file exports; each exporter owns its item format.
Exports use interpreted state without the per-device registry reads needed by UI tooltips.
The answer-file generator reads the active power plan for AC or DC values that
still need system evidence after resolving the configuration.
Its registry command emitter formats toggle and selection writes, including
individual binary bytes and bits. Definition-based toggles use the selected state
for bit writes. Captured values supply their own bit state.
`SelectedAppsProvider` initializes the Windows Apps list and supplies its current
selection to Autounattend and the WIM workflow.
`WindowsAppConfigurationMapper` owns record projection for exports, WIM and
Autounattend, including localized names and AppX/capability/feature precedence.

Core's singleton `ConfigReviewService` owns application mode, review differences,
Test-mode drafts (`Builder`) and typed `BuilderEdit` snapshots. Infrastructure aliases
its mode/review/diff/badge interfaces to that instance. `SettingViewModelFactory`
restores drafts; setting view models retain their presentation baselines.
System-operation reservations prevent real writes from overlapping Test mode.
The power-plan deletion command holds a reservation through deletion and list refresh.
Exit restores presentation before clearing drafts; Apply captures edits, exits the
mode and invokes normal configuration execution. Failed/cancelled exports retain drafts.

`BuildVersionGate` owns Windows setting compatibility and message tokens, shared by
import checking, filtering, catalogue decoration and feature recommendations.
`ConfigActionSelection` owns action-only sections for direct/reviewed imports.
The executor translates approved Taskbar and Start Menu cleanup into a single
action per feature. The settings bridge handles the results of imported writes and
routes wallpaper-only requests through the theme handler using the current theme.
The registered special handler owns each setting's apply result. Windows Defaults
clears individual catalogue-owned policy values through the registry service.
`WimUtilViewModel` coordinates its steps; Infrastructure owns image/ISO operations
and UI owns wizard presentation and file pickers. Its XML step uses the local
Autounattend generator or a user-selected answer file; `WimCustomizationService`
validates the answer-file XML and copies its bytes and selected drivers into the
extracted media. `WindowsSetupMedia` shares required-file validation between
extraction and selection of existing media. Extraction owns the ISO mount through
drive lookup and cleanup and verifies dismount before reporting completion.
Step completion is bound to media revisions, input/output paths and the current
attempt. Extraction rejects sources inside the workspace and invalidates dependent
steps before replacing media. XML, driver and image edits invalidate ISO evidence;
asynchronous results publish only for their captured media revision.
WIM and ESD deletion share one confirmation, media invalidation and format refresh
flow in the image-format view model.
`IsoService` owns a temporary ISO beside the destination, validates it and publishes
through atomic replacement or a move. Failure or cancellation cleans the temporary
artifact and preserves the existing destination.
Task owners use `CompleteOwnedTask` with their captured cancellation source.
Progress callbacks bind to the task active at creation and discard updates after
completion or replacement, including parallel-script slots.
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
`SystemSettingsDiscoveryService` queries scheduled task states through one scheduler
session per discovery batch, retaining each setting's independent result.
Setting application commits a preset selection after its compatible children
succeed. Partial writes publish observed state to the setting view model.
`IPowerSchemeOperations` supplies native Windows results to the power settings applier.
Core's `PowerSettingUnits` defines the scale between display and stored values.
Configuration import, native application, recommendation resolution and setting
controls use that scale. Catalogue toggle resolution and numeric badges share
`SettingValueComparison` to match observed values with recommendations and defaults.
`TooltipDataService` owns technical-detail eligibility and metadata snapshots;
`TechnicalDetailsButton` and `TechnicalDetailsManager` own their presentation/lifetime.

`ContextMenuViewModel` owns program groups and filtering; `ProgramIconSource` loads
local artwork with bounded concurrency. `ContextMenuService` owns guarded operations
and inventory authority; `WindowsContextMenuStore` owns registry scopes, reversible
registration edits and readback.

`NetworkOptimizationViewModel` and `FeatureOptimizationViewModel` use
`OptimizationToolsService` for guarded native/settings operations. Core's
`NetworkTuningCatalog` owns control IDs, source/write parameters, constraints and suggestions,
including the machine policy for Delivery Optimization background bandwidth.
`NetworkTuningOriginalStore` saves each first available value under the protected
machine state directory. The service supplies it alongside live Windows readback.
The [network guide](src/WinOptimizer.Infrastructure/Features/Optimize/README.md)
defines their boundary formats. The Internet page uses `IInternetSpeedTest` for
explicit, cancellable measurements. `CloudflareInternetSpeedTest` owns HTTPS
probes, payload limits and timing. The view model keeps the two latest successful
results in memory for comparison. The Internet page also uses `IConnectionService`
for Windows VPN inventory and settings navigation. Its DNS control uses
`ICompatibleSettingsRegistry` and `ISettingApplicationService` for the existing
`gaming-dns-server` setting. Feature actions use `ISettingApplicationService` and
`ISystemSettingsDiscoveryService`.

| Shared owner | Responsibility |
| --- | --- |
| `PageHeader`, `NavigationCard`, `PageStyles.xaml` | Shared card, segmented control and page layout styles, including a 12-DIP gap between page blocks and cards |
| `InteractiveSurfaceResources.xaml` | Shared hover and pressed colors resolved by the toolkit card and WinUI templates |
| `AppColorResources`, `FeatureIcons.xaml`, `CustomIconCatalog` | Theme-aware canvas, surfaces and live accent brushes, original vector artwork and semantic icon-name lookup |
| `PopupHandler`, `PopupStyles.xaml`, `DialogService` | Modal queue, native popup construction, theme/bounds and domain dialog adapter |
| `TaskProgressService` / `TaskProgressViewModel` | Shared task state, queue position, task output and cancellation in the shell footer; pages can own presentation |
| `TerminalProgressBar` | Block character recognition shared by WinGet, task progress and the terminal dialog |
| `ScrollBarAutoHide` | Control-scoped idle visibility and input subscriptions |
| `AppBranding` | UI image URI and shell icon path; [application artwork](src/WinOptimizer.UI/Assets/ICON-ATTRIBUTION.md) |

The title bar and Settings support commands use `IProcessExecutor` to open the
PayPal destination owned by UI's `SupportLinks` in the user's browser.

Page stacks use `PageCardSpacing` between visible blocks. Inline notices leave the
layout when closed. `SettingsCardStackPanel` also skips zero-height item containers
in grouped settings lists so search filtering keeps the same card spacing.

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
The registry service resolves the hive independently of its native root handle.
Registry tests supply private hive handles at that boundary.
Discovered entries carry stable registry-derived IDs and exact registration paths;
installation status, metadata and fresh uninstall-command lookup use that identity.
`WindowsUninstallCommandSource` uses this shared inventory for catalogue entries,
requiring one exact-name or fingerprint match before rereading the command.
`WindowsAppxDeployment` owns interactive-account selection and the WinRT/process
boundary shared by AppX discovery and package removal. Calling-account removal
uses WinRT, and alternate-account removal targets the interactive SID.
`AppPaths` shares application-path expansion across metadata, icons, portable
discovery and removal through `IInteractiveUserService` folder bindings.

`AppStatusDiscoveryService` refreshes each installed definition's nullable `AppMetadata`
through `IAppMetadataSource`. Infrastructure's `WindowsAppMetadataSource` reads uninstall
registrations, matching AppX packages and interactive-user UserAssist history.
It indexes exact identities and latest usage once per refresh. External Apps shares
one registry snapshot between catalogue discovery, status checks and desktop icons.
The icon resolver checks the current resource when it reads the icon. The program
inventory reads each registration's fields through a single open registry key.
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
Uninstall backup recovery observes the shared task lifecycle and completes only its
captured task; its dialog releases the progress subscription when closed.

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

UI's `UninstallResidueReview` owns result-page navigation, recovery dialogs, dispatch
and deferred review lifetimes. Its native-uninstaller scope uses `UninstallWindowSuspension`
to disable/minimize the window and restore its previous enabled/maximized state on every exit.
`MainWindow` rejects closing while that scope is active. `UninstallResiduePage` keeps the session alive until
navigation away, blocks navigation during scan/cleanup/recovery and expires its journal
entries on exit. `UninstallResidueNode` builds selection trees. `ExternalAppUninstallService` owns
registered desktop commands and automatic providers. The Windows-app workflow
passes selected catalogue entries to `WindowsAppUninstallService`. It routes them
through those commands or the shared Windows servicing job and returns one native
outcome per entry. Windows servicing reports restart requirements.
`BloatRemovalService` executes the current selection in a temporary script, reads
per-target outcomes and serializes saved sign-in changes with template maintenance.
Scheduled removal and Autounattend share the generator. `ScriptPaths` defines the
sign-in task name and script location for removal, template maintenance and
answer-file generation. The scheduled task must be enabled before the sign-in plan
can be confirmed. Cancelled uninstall batches retain completed native outcomes
for review. The interactive servicing script checks a cancellation event between
servicing operations and keeps both AppX removal phases together.
`UninstallInspection` checks Windows servicing state for
capabilities and optional features. Session verification publishes outcomes after removal
completes; leftover scanning starts on request. The review presents files/folders
and registry in separate steps, retaining selections and limiting cleanup to the
current step. Page view models reconcile verified removals before review, then
invalidate status caches and recheck inventory before releasing their busy state,
including after failure or cancellation. Configuration workflows defer review until
enclosing overlays close. Cleanup accepts current selectable IDs,
revalidates evidence and publishes a verified backup before deletion; recovery
preserves conflicting destination data.

`WinGetPackageInstaller` runs WinGet removals and reports command progress.
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
`CleanerCategoryReport` references the same selectable items and preserves the areas
selected at scan start, including empty categories. Cleanup freezes the confirmed
selection and aggregates per-finding outcomes into numerical category reports;
technical diagnostics remain separate from report counters.
During review, it owns references to the area's actionable result groups and cached
selection totals. `CleanerViewModel.SelectedCategory` controls the right pane while
the left pane retains all scanned areas, including empty and protected-only areas.
Groups and findings retain their identity across category changes; cleanup uses the
global selection. Protected findings stay in scan evidence and category counts.

Core's `CleanerService` deduplicates file paths across categories in stage order,
serializes scan/apply operations and retains the current
immutable evidence and scan-owned options. Callers submit finding IDs, not rebuilt
targets. `ICleanerBackend` separates domain ordering from Windows work:

| Infrastructure owner | Responsibility |
| --- | --- |
| `CleanerFileScanner` / `CleanerAccounts` | Profile-scoped file/privacy/shortcut discovery, source labels and account/root evidence |
| `CleanerFileDeletion` | Exclusive-handle path/metadata validation and deletion |
| `CleanerRegistryScanner` / `WindowsCleanerRegistryEditor` | Typed reference discovery, registry views, per-account probe caches and revalidation before deletion |
| `RegistryHiveCompactor` | Hive size estimates and revalidation of reviewed Windows/profile files |
| `WindowsRegistryDefragBoot` | One batch per reviewed selection, Windows PE image and boot scheduling, offline receipts |
| `CleanerBackupService` | File/value payload verification, manifests and conflict-preserving recovery |
| `WindowsCleanerBackupVault` | Protected directories, ownership/reparse checks and account-bound DPAPI metadata |

Disk maintenance uses `defrag.exe`. `ICleanerProcessMonitor` isolates application
activity checks. `GroupedResultsList` and `SelectableResultGroup` reference the
same result items as cleanup and own category selection/expansion presentation.
The shared selection contract is `ISelectableResultItem`.
`CleanerRegistryValue` owns native payload-size estimates shared by registry and
privacy scans. Findings carry those bytes through row/group totals and the selection
cache; they describe data size rather than registry-hive disk-space recovery.
Its optional `ResultReport` supplies ordered presentation sections and label/value
rows. `CleanerDiskReport` structures native disk output without changing the scan
evidence; `GroupedResultsList` selects the inline report template for these items.

`CleanerPaths` owns directory boundaries and traversal; account sessions cache
folder bindings until disposal. `CleanerProgressReporter` coalesces pending UI
measurements. Scan events carry the ordered areas and completed count so the
page can show the current area and retain completed status across coalesced
updates. The page uses the shared task output and cancellation token.
Privacy/junk inspection uses batches of 64 and up to four workers;
backup copies use at most four concurrent file payloads. Completed scans publish
immutable plans; selected backup preparation must finish before any mutation.
Failed/cancelled scans cannot supply a cleanup plan, and consumed findings cannot replay.
Apply results retain original findings with confirmed successes and failures, including
partial outcomes when cancelled during execution. File-length estimates from successful
deletions, removed registry payloads, completed maintenance operations and scheduled
registry work remain distinct.

Registry defragmentation uses a Windows ADK WinPE RAM disk staged under an
administrator-owned job directory. The scheduler adds a temporary BCD loader and
records the attempt when arming the one-time boot sequence. WinPE resolves
volumes by per-job markers and claims each job once. It verifies the backup and
compact copy for every hive before replacement. Old transaction logs are parked
and same-volume atomic moves publish clean hive files. Detected failures restore
every attempted hive in reverse order. WinPE saves a receipt, records the outcome
of removing temporary BCD objects and boot images, and restarts after a
successful run or verified rollback. A failed rollback retains the job and leaves
the recovery console available. The receipt and backups survive the return to Windows.

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
the report, result categories, selection and task presentation. `SecurityPage`
displays scan coverage and uses the shared task output and cancellation token.
Results show counts, unavailable checks, and technical evidence from the report.
Only completed scans publish applicable reports. Fixes require confirmation and
normal mode, recheck evidence and verify readback; cancellation retains completed
receipts and active native write units finish before stopping.

## Drivers

Core's `DriverScanService` shares the inventory and immutable preferences with each
enabled source, bounds concurrent discovery and collects per-source outcomes. It
reports workflow phases to `DriversViewModel` for the page's progress card. The
card shares task cancellation and live output with the shell footer.
`DriverCatalogMatching` owns candidate applicability, INF-level comparisons,
branch-aware newest releases and the identity rules for source revisions and verified
payloads. WUA applicability and INF match evidence have separate states. Source
metadata, package digests and contained INF/catalog evidence retain their provenance.
`DriverRemovalService` owns selection revalidation and per-item results.

Infrastructure supplies PnP, DISM and SMBIOS inventory and uses native WUA for offers
and exact-revision exports. Other adapters collect metadata through `OfficialDriverHttp`,
which confines each request and redirect to publisher distribution hosts and caches
raw responses and usable scoped catalogs. Publisher parsers and descriptor resolution
remain with their adapters. `OfficialDriverPackageService` owns archive acquisition,
hash checks, the content cache and atomic download publication. `DriverPackageInspector`
uses `DriverScanWorkspace` for isolated extraction and `WindowsDriverInfInspector` for
SetupAPI Models selection. The catalog signature worker releases Windows trust
handles before workspace cleanup. `WindowsDriverRemovalBackend`
executes PnPUtil identities. UI's transient `DriversViewModel` owns tasks, result
filters and selection. `DriverResultItem` projects scan evidence into display rows,
and `DriverResultsView` arranges candidate comparisons and inventory at the
available width. Removal rechecks exact package/device
identities; bound, inbox and boot-critical packages remain protected. Cancellation
stops between native removals and preserves completed results.
Removal discovery reads all package and device bindings and normally measures
only the selected packages before comparing them with the original inventory.

## Experimental Windows features

Core owns the typed configuration, build profiles and recovery contracts.
Application's `ExperimentalCatalog` selects a bundle by feature, build,
revision and OS architecture. Bundles can carry different component IDs,
states, variants and dependencies. Built-in declarations take precedence.
The UI explicitly imports local profile sets through the existing picker.
Infrastructure parses the bounded JSON document and Application validates the
profiles before saving them to protected state. Discovery and writes resolve
the authoritative mapping for the installed release independently. Missing
evidence keeps activation unavailable. Enable, disable and restore check
shared IDs in the selected bundles before touching either store.

The native backend also enumerates runtime and startup feature configurations
in bulk with bounded buffers and independent error reporting. Application
groups both views by ID, orders configurations by priority, and excludes IDs
with a verified mapping for the current release. The discovery snapshot holds
the inventory in memory.

Infrastructure's native backend follows the configuration semantics documented
in [ViVe v0.3.4](https://github.com/thebookisclosed/ViVe/tree/v0.3.4). The
runtime API reads the effective configuration and verifies each User-priority
change against that view. A higher-priority result blocks changes whose prior
User state cannot be verified. Persistence uses the selected ID under
`FeatureManagement\Overrides\8`, verifies the stored values and checks
Windows boot status. The native Boot query reflects the startup snapshot.
Pending state survives refreshes and app restarts until a later boot confirms
the expected User configuration. The page presents these three observations
separately from feature availability.

The application serializes changes under a Normal-mode system-operation
reservation. Before changing either store it atomically persists the receipt
in the protected machine-wide `State/ExperimentalFeatures.v1.json` file.
Checkpoints distinguish verified, interrupted and unstarted writes per ID and
store. Undo compares current observations with the receipt before restoring
the saved values, and can resume an interrupted recovery. Restore default
removes only the selected User-priority entries. Cancellation stops at the
next store boundary. Activity and errors appear under Windows Tools in the
journal. The WinUI page owns search, presentation and explicit user actions.
Unfinished recovery can be deferred across Windows updates and resumed only
on the receipt's original release.

## Extension and Control

`UtilitiesPage` presents SysMonitor, Preview and Notch in Extensions. `ManagementPage` presents the
environment editor, hosts editor and battery charge controls alongside the other
Control tools. Detail pages retain their owning sidebar section in the shell journal.

Singleton utility view models retain drafts across navigation. `UtilityViewModel`
owns mode/localization subscriptions and operation presentation. Explicit page
entry loads tool data; writes use normal-mode reservations and shutdown draining.
The environment editor confirms draft disposal before refresh or scope changes and
publishes a scope only after its read succeeds.

`SystemBackupService` owns restore-point phase ordering, cancellation and verification
timing. `WindowsSystemBackupBackend` owns WMI, shadow-storage commands, native creation
and restoration of temporary throttle settings. Cancellation stops between native
units and propagates to process work; active synchronous native calls finish cleanup.

| Feature | Owners and boundaries |
| --- | --- |
| Environment editor | `EnvironmentEditorService` serializes compare-before-write edits; `WindowsEnvironmentVariableStore` preserves raw registry string types and interactive-user targeting. PATH rows project the same raw draft. |
| Hosts editor | Core's `HostsText` / `HostsSyntax` own parsing and validation. `HostsEditorViewModel` owns one draft; `HostsEditorService` owns the fixed path, revision, encoding, backup and atomic replacement. |
| Battery | `BatteryChargeService` selects OEM providers, validates the chosen stop and restart values against discovered ranges, and confirms firmware readback. `DellBatteryFirmware` supplies the restart range for each stop limit. `BatteryChargeViewModel` uses that range to present fixed and relative restart choices. `BatteryTelemetryReader` joins standard WMI sensors. `BatterySupportCatalog` owns official setup routes. See the [provider guide](src/WinOptimizer.Infrastructure/Features/Utilities/README.md). |
| Connection | `ConnectionTrafficService` polls adapter counters for Core's bounded `ConnectionTrafficWindow`. `WindowsConnectionHistory` keeps up to four profile reads in flight. The view model loads daily usage independently of live samples. |
| Saved Wi-Fi | `SavedWifiService` checks account scope; `WindowsSavedWifi` enumerates native WLAN profiles and reads keys in memory. The transient `SavedWifiViewModel` owns a page-scoped snapshot, with no persistence or logging of credentials. |
| Windows servicing | `WindowsServicingService` owns typed phase planning, restart gates and cancellation. `WindowsServicingBackend` owns privileges, source validation and DISM/SFC process boundaries, forwarding cancellation to the shared process runner for owned process-tree termination. See the [servicing guide](src/WinOptimizer.Infrastructure/Features/Management/README.md). |
| Autoruns | `AutorunService` owns snapshot/write authority and the app's per-account sign-in task. `ApplicationStartupExecutable` checks installation protection before `IScheduledTaskService` registers the launch. `AutorunPowerShellBackend` owns native scripts and `AutorunCsvReader` validates advanced inventory. See the [autorun guide](src/WinOptimizer.Infrastructure/Features/Management/AUTORUNS.md). |
| Process monitor | `ProcessMonitorService` owns serialized samples and selected-process resources; `WindowsProcessSource`, `WindowsProcessPorts`, `WindowsProcessFiles` and `WindowsProcessNetwork` own native boundaries. `ProcessTrafficLedger` aggregates bounded ETW totals. See the [measurement guide](src/WinOptimizer.Infrastructure/Features/Management/PROCESS-MONITOR.md). |

`ApplicationStartupViewModel` presents the app's sign-in task in the Info section
of Settings → General. It reads the scheduler state when General opens and after
changes through `IAutorunService`.

`ManagementPowerShellRunner` materializes embedded scripts into exclusive temporary
files. An encoded loader hashes the bytes read from disk before executing that
same buffer; request JSON travels separately as base64 data. The runner deletes
temporary scripts after command completion. `Sysinternals-Cache.ps1` owns
allowlisted Microsoft tool acquisition, Authenticode validation, reparse checks
and executable read locks for Autorunsc and Handle. Each feature guide defines its
consent, executable, arguments and cache subdirectory.

## Desktop widget companion

`DesktopWidgetsApplication` runs SysMonitor and Notch in the separate elevated
`win-optimizer-widgets.exe` process. Either widget's SID-scoped logon task can
start the single companion. The widget settings buttons activate the main app
at the relevant settings page. `ExtensionApplicationLifetime` drains sensor
sessions when the companion exits. `WidgetPreferencesWatcher` applies committed
settings changes on each process's dispatcher, and `UserPreferencesService` holds
an exclusive account-scoped file handle across preference transactions.

## SysMonitor

`ExtensionsViewModel` owns validated `SystemMonitorOptions` under
`Extensions.SysMonitor`. It coordinates preference persistence with
`SystemMonitorStartupService`, which shares SID-scoped logon task registration
through `IScheduledTaskService` with `NotchStartupService`.

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

## Notch

`NotchExtension` serializes the interactive user's `Extensions.Notch.Enabled`,
`Extensions.Notch.StartWithWindows` and `Extensions.Notch.Modules` preferences
and owns one desktop window. It reconciles the Notch logon task with the effective
startup choice when activation preferences change. It stores
compact pins independently in `Extensions.Notch.PinnedModules`, defaulting to
Clock. `Extensions.Notch.CompactOrder` stores the readout order, with one shared
position for the clock and focus timer. Audio visualizer combines microphone
activity and playback peaks in one compact slot. The compact window uses the
intersection of enabled and pinned modules and previews a drag before saving the drop.
`Extensions.Notch.SysInfo.Enabled` controls the dashboard readings as a group.
The Sys info settings page edits individual reading selections and pins, which
stay saved when the group is off. Device batteries takes its enabled state and
compact pin directly from the module and pin preferences.
`Extensions.Notch.Content` stores the selected weather location and Quick access pins. The Extensions
card and `NotchSettingsPage` share its confirmed selection. The companion restores
opted-in widgets during sign-in and keeps running while a widget is active.

`NotchWindow` routes icons, navigation dots, and wheel input to the same
enabled view selection. It renders the compact clock, `FocusTimer`, and system
meters. `Extensions.Notch.Placement` stores the outline and Windows display
device. `NotchFrame` shapes and positions the native window within the active
display's work area, with the primary display as the fallback.
`DesktopMediaSession` reads and controls the current Windows media session
while its view is open. Seek requests carry the displayed track revision and
are bounded by the current session's seek range. The Media view uses Notch's
default audio endpoint peak reader during playback. Peak sampling has its own
session so changes to playback leave the other readings active.
The Overview reflows the selected live views into four slots. The dedicated
tabs share those control instances and reading sessions. `Extensions.Notch.Content`
stores the independent Overview switch and slot order. Dragging a view tab onto
a slot replaces its tile or swaps it with that view's existing tile.

`NotchReadings` keeps separate sampling sessions for enabled system counters,
batteries and retained daily traffic while the widget is active. The compact
`WindowsSystemMonitorService` keeps one counter baseline and enables GPU hardware
reads independently. View-specific work can change without resetting those sessions.
`NotchDashboard` retains bounded CPU, RAM and per-sensor GPU histories while hidden
and refreshes the curves when visible. The Notch window fits Sys info to the
measured reading cards and leaves the view scrollable on shorter displays. The
shared chart draws Notch samples at fixed spacing with smooth curves through
measured points and gaps for missing data.
`NotchBatteriesPanel` renders a scrollable card per measured battery, sharing
the labels and charging state used by the compact badges.
`WindowsDesktopAudio` reads Core Audio input sessions and the default playback
endpoint's volume, mute state and output peaks. It applies playback volume and mute
changes for Quick Settings.
`WindowsDesktopStatus` reads power, supported WMI brightness, paired Bluetooth
connections and enabled USB and audio interfaces. `NotchReadings` samples
Bluetooth and local interfaces independently and uses the shared GATT reader
for new LE connections.
`NotchQuickSettings` samples only while its view is active and coalesces slider edits.
`WindowsDesktopQuickSettings` applies changes through Core Audio, WMI, WinRT radio
control and the AC or DC power-mode API. For the desktop account, `WindowsNightLight`
updates the recognized CloudStore state record and `WindowsDoNotDisturb` sets the
shell's manual notification profile through COM. The panel rereads Windows after a
change and routes unavailable radio or power controls to the corresponding Settings pages.
`NotchStatusChanges` groups interfaces by shared physical container ID and
compares confirmed samples so the window can show the latest transition in its
transient status banner. `WindowsDesktopFrameRate`
owns an opt-in, in-memory DXGI/D3D9 ETW session and samples the busiest swap chain
from the foreground fullscreen process on the primary display.
`DesktopPlaceSearch` adapts Astro's Photon location search. The Notch settings
cancel superseded lookups and save the selected label and coordinates.
`DesktopWeather` uses the selected coordinates for forecast requests.
It fetches and caches current conditions and local forecasts together while weather is
visible. `NotchWindow` shows current conditions in Overview and adds the hourly and
daily forecast in the dedicated Weather view. `NotchWeatherIcon` adapts Astro's native
vector paths. Quick access opens a native
file picker owned by the Notch window. `WindowsDesktopApplications` reads AppsFolder
entries and their Shell icons on workers. Quick access copies icon pixels into a
cached XAML bitmap before releasing the Shell bitmap. `NotchExtension` saves app targets and
display names alongside local pins in one ordered list. Pinned items open through
the interactive Explorer shell shared with desktop extension launching.
`NotchFileShare` offers a user-selected local file to
Windows Share through the Notch window's share contract.

## Preview

`PreviewSettingsViewModel` shares the confirmed settings between the Extensions
overview and Preview details and allows one settings request at a time.
`PreviewExtensionSettings` stores background opacity under the desktop account's
Preview registry key. It applies changes through the same settings channel.
`PreviewBackdrop` owns the window's desktop acrylic controller and tint.
Enabling the shortcut starts the non-elevated Preview
process in the background if needed. Settings requests use that instance when
available. Sign-in registration belongs to the desktop account and can be
changed independently of the shortcut listener.
`PreviewSpaceInput` registers the file popup's window handle as a Raw Input
keyboard sink while the feature is active. It observes a single unmodified
Space key press and passes the Windows message through. `ExplorerPreviewSelection` captures
native focus in the desktop icon view or active Explorer tab. It resolves
one selected filesystem path on a worker through typed Shell COM calls and
checks that the view still owns focus before the popup opens.

`FilePreviewLoader` prepares bounded read-only snapshots on a worker thread.
`FolderPreviewReader` collects recursive counts, logical size and allocated
file data while retaining a bounded list of direct entries separately.
`OfficePreviewText` reads Office and OpenDocument excerpts. Preview shares the
stored Shell Link reader with Cleaner. Snapshots retain the selected path separately from
the local path used for rendering. `PreviewApplication` prepares one hidden
popup for the selected file and reuses its controls between files.
`PreviewSurface` measures the summary at the available client width.
`DesktopPopupPlacement` fits the result and the native frame within the monitor work area.
`WindowsPreviewIcon` resolves Windows association artwork on a worker,
independently of file-content loading. Preview and Software & Apps share the
native icon renderer. `PreviewSurface` owns the current file's cancellation, image decode, PDF
rendering and playback. The Open action passes the selected path to
`IProcessExecutor` for Windows shell activation. Dismissing the popup releases
preview resources. Disabling
the shortcut also unregisters Raw Input. During an app update, the settings
channel asks the resident Preview process to release the popup and exit.

## System information and Restore

`WindowsSystemInformationService` owns query definitions, normalization and the
last completed `ISystemInformationService.CachedSnapshot`. `WindowsSystemInformationQuery`
isolates local WMI calls and native object lifetimes. The service overlaps up to
four independent queries and publishes sections in catalogue order. Transient
page view models reuse that snapshot; explicit refresh replaces it. Typed values, availability and
timestamp cross the UI boundary unchanged. `ISystemInfoProvider` serves the
separate diagnostic-log summary.

`RestoreViewModel` owns configuration commands and restore-point presentation.
`IConfigurationService` handles configurations; `ISystemRestoreService` owns
protection state, point inventory and targeted deletion.
`WindowsRestorePointStore` isolates WMI and `SRRemoveRestorePoint` calls on worker
threads. `ISystemBackupService` owns point creation and verification.

## Storage

TimeMachine reuses paged `IChangeHistoryService` receipts for daily history.
Account-specific records carry an interactive SID and are filtered in TimeMachine.
Opening a day searches the remaining journal pages for matching receipts and
preserves their append order. Timeline pagination keeps its own cursor.
`TimeMachineService` reads saved snapshots for inspection, owns snapshot comparison,
and routes selected resources to the
registry, scheduler, preferences, Cleaner and Drivers owners. Those owners
recheck live state before writing. Cancelled batches retain completed resource
results. Preferences retain logical values, including
absence. Registry entries retain the original value kind and raw data. Drivers
export and hash the selected package before deletion, then verify its saved files
during review and before staging it again. Cleaner file receipts store path
metadata.

`TimeMachineStore` owns account-scoped manifests, driver payloads, atomic
publication, targeted snapshot deletion and expiration. `TimeMachineService`
applies the persisted retention limit and coordinates policy changes with
expiration. Expiration isolates unreadable manifests and logs cleanup failures.

`AppLogEntry.Topic` stores a stable subject. `LogService.BeginTopic` scopes it to
the current asynchronous operation; nested and concurrent flows retain independent
topics. Original setting receipts keep their typed kind, group, values and batch.
`ChangeHistoryService` binds each batch to its originating asynchronous operation.
`ActivityJournalService` in Application merges saved diagnostics and receipts through
the existing storage contracts. A query session owns bounded source buffers and
reverse byte cursors. It merges buffered source heads by timestamp and source order
and closes files between pages.

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
checks owners, protected permissions and filesystem paths. It normalizes
launching-account-owned directories to administrator ownership, rejects other
owners except Administrators and SYSTEM, and rejects links and junctions before use.
It applies permissions when the owner or access rules differ from the required descriptor.

Inno Setup owns retention/deletion of the shared store and targeted Windows trace
cleanup, separately from tracked installation files. Profile registry sessions
mount offline hives only for cleanup and release them in a `finally` block. Task Scheduler
owns task and folder removal through its COM API.
The [uninstallation guide](docs/GETTING-STARTED.md#remove-the-app-and-its-data)
describes data retention and full cleanup. The uninstaller validates the store,
profile, and Prefetch boundaries before it removes traces and shared data.
Access, mount, or deletion failures stop uninstallation before it removes the
tracked installation files.

| Data | Subpath / owner |
| --- | --- |
| Personal preferences | `Users/<interactive SID>/Config/UserPreferences.json`; `UserPreferencesService` |
| Configuration snapshots including user settings | `Users/<interactive SID>/Backups/Configurations`; Application configuration services |
| Diagnostic sessions and startup log | `Logs`; Core logging services |
| Change receipts | `History/ChangeHistory.jsonl`; `ChangeHistoryService` |
| TimeMachine snapshots and recovery | `Backups/TimeMachine/<interactive SID>`; `TimeMachineStore` |
| Cleaner and uninstall recovery | `Backups/Cleaner/<protecting account SID>`; authenticated, account-bound vault |
| Hosts, event archives and power plans | `Backups/Hosts`, `Backups/EventLogs`, `Backups/PowerPlans` |
| Shared app icons and WinGet exports | `Cache/Icons`, `Cache/WinGet` |
| Optional Microsoft tools | `Tools/autoruns`, `Tools/handle`; verified Sysinternals cache |
| Persistent removal scripts | `Scripts` |
| Windows installation customization scripts and logs | `Unattend/Scripts`, `Unattend/Logs` |
| Installer downloads and extracted app payloads | `Downloads`, `Apps` |
| Temporary exports, scripts and WIM work | `Temp`; operation owners clean their workspaces |
| Disabled startup entries | `State/Autoruns` |
| Account completion and disabled startup state | `Users/<SID>/State/Customizations`, `Users/<SID>/State/Autoruns` |

Generated and embedded PowerShell share `AppStorageScript` for path validation,
verified atomic JSON publication and completion markers. Disabled registry startup
entries retain their command and registry type in JSON; disabled startup files
retain their bytes and source metadata on disk. Registry writes implement Windows
configuration, startup restoration and Windows integration such as file
associations, uninstall registration and Registry Editor navigation.

`LogPageReader` supplies bounded forward and newest-first UTF-8 pages with byte
cursors; reverse reads scan in blocks. Diagnostic records use typed JSON Lines.
At session startup, retention removes sessions older than 30 days and keeps at most
50 previous sessions. Clearing diagnostics serializes with the active writer;
change history has no clear operation. System-information snapshots, process
observations and widget telemetry remain in memory.
