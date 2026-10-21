# Execution pipelines

[README.md](README.md) contains commands and user guidance;
[ARCHITECTURE.md](ARCHITECTURE.md) maps code owners and storage. The sequences below
describe state transitions, ordering and failure handling.

## Application storage

1. Build the host without triggering storage mutations in constructors.
2. Await `AppDataStorage.InitializeAsync` on a worker thread before loading
   preferences or starting feature services. Establish protected shared directories
   and the interactive account's private folder. Normalize directories owned by
   the launching account to administrator ownership and protected permissions
   before use; reject other owners except Administrators and SYSTEM.
3. Continue normal startup. Feature writes use the same central layout; temporary
   work remains under `Temp` and is removed by its operation owner.

## Application startup and exit

Activate the main window with its lightweight loader and await its first rendered
frame before loading feature resources, building the host or preparing storage.
Automatic widget launches keep the window hidden and proceed without a frame wait.
After storage is ready, load saved localization, construct the full shell in the
same window and apply the saved theme and placement. Keep the same loader visible
until shell initialization completes.

Open diagnostic logging after storage is ready. Collect its hardware summary
on a worker thread while startup proceeds; log writes remain available during
collection. Observe initialization failures through the startup logger. Disposal
suppresses a late header without reopening the session.

1. Render the loading shell before resolving startup services. Initialize compatible
   settings, materializing filtered and decorated authoring snapshots once, then
   index each definition together with its feature owner.
   Preload global setting state from the authoring snapshot, initialize tooltip
   subscriptions and warm the technical-details icon.
2. Navigate to Software & Apps, mark the shell ready and hide the startup overlay.
   Start the deferred update loop and restore extension preferences explicitly.
3. After the first visible frame, start the shared Windows Apps load and initialize
   External Apps in the background. Each tab owns its loading indicator. Installation
   discovery and local icon extraction overlap. Installed package artwork takes priority;
   remaining installed apps use matching uninstall registrations' local `DisplayIcon`
   resources. Desktop icons retain their colors and transparency; file identity and version
   key the PNG cache. External Apps refresh resolves icons for its reconciled inventory
   in the background and notifies bound rows. Entries without readable artwork retain
   their category glyph. AppX enumeration overlaps serialized native
   capability/feature DISM queries. Required WinGet fallback overlaps DISM for independent
   app identities; shared identities retain component-first precedence.
4. After Windows Apps inventory is available, attempt the initial full configuration
   backup when it is not recorded as complete. The backup wait is bounded to 30 seconds;
   failure/timeout is logged and leaves it eligible for retry on a later launch.
   Offer a first-launch restore point when applicable and warm the sponsor cache.

On close, save window placement and drain utility work and pending preference
writes through the shutdown hook. Running operations require the close/cancel
confirmation.
After pending extension transitions finish, a running SysMonitor keeps the process
alive with the main window hidden. Otherwise stop sampling and shut down. Widget
settings or a redirected manual launch restore the same window. Disabling the last
widget while it is hidden exits; unexpected widget failure reopens it for diagnosis.

## Section navigation

Overview cards, search suggestions and Settings category selections create entries
in the main Frame journal, retaining their detail destination and search parameter.
Back and Forward restore that entry before rendering nested content. Inner frames
keep no separate journal. Sidebar clicks open the owning section's overview;
clicking an overview that is already open keeps the current history.
Settings opens with no category selected. Its category list keeps selection
independent of the collection view's current item, so initialization and search
refreshes preserve the navigation destination.
Category switches and history navigation reuse the Settings page and loaded preferences,
update its inner panel and suppress the outer-page transition. Leaving Settings releases
the page and its transient subscriptions; returning initializes a new page.

## Configuration and Test mode

1. Load typed configuration records from an embedded recommended/default preset or
   the file-picker boundary. Compatibility filtering precedes import execution.
   Imports and backups require an explicit version `2.0`. Exports retain typed fields, `SelectedIndex`, AppX package-name arrays
   and stable power-plan GUIDs. System backups use installed Windows apps; normal
   exports use selected Windows and external apps.
2. Import choices determine selection, review or explicit execution. Configuration
   actions reserve system-operation state; dialogs and overlays use UI adapters.
   Review membership comes from the authoritative diff queue and combines with search.
   Direct and reviewed execution use the same action-section selection: retain
   selected feature sections and add missing action-only sections in taskbar,
   Start Menu and theme order. Action-only sections do not import other settings.
3. Test mode starts only when real operations permit it, retaining compatible cards
   and discovered values. Edits record typed draft snapshots; recreated pages restore
   them over their presentation baselines. System writes are guarded while preferences
   and read-only scans remain available.
4. Export overlays a copy of the draft onto the configuration. Exiting Test mode
   offers Export, Apply or Discard. Export closes the draft only after a successful
   write; cancellation/failure retains it. Apply captures only edited settings, exits
   Test mode and uses normal configuration execution. App selections remain exportable.
5. Exit restores presentation baselines before clearing edits or entering review.
   Discard needs no Windows rediscovery or page reconstruction.

The installation-media XML step generates an answer file from current selections
or validates a selected local XML file and copies it into the extracted media.
Successful generation and selection mark only their own action card complete.

Unattended XML exports overlay configured power values on the current-system
snapshot. Selection indices resolve through the setting catalogue; numeric AC/DC
values retain their stored system units. The generated script creates the chosen
plan when necessary, applies power values, then activates that plan even when no
individual power values are available.

Configuration and answer-file snapshots share feature discovery and grouping through
`FeatureConfigurationSnapshot`, retaining their respective index/raw-value formats.

## Preferences, dialogs and operation progress

Preference owners explicitly load and validate typed JSON values. Both preference
readers require JSON booleans for boolean options. Initial loads do not
write defaults. Edits queue snapshots in order; successful writes publish the new
persisted snapshot. Failures remain visible and later edits can retry. Cleaner and
Drivers wait for pending saves before scanning; unresolved errors block those scans.
Category reset uses the same queue for its scope; full reset covers preferences
exposed in Settings. Shutdown drains pending writes.

Dialogs enter `PopupHandler.RunModalAsync` before content preparation. The shared
queue serializes native dialogs; feature validation may keep one open. The handler
applies host theme/bounds, starts requested live updates and stops them on completion
or error. Native flyout dismissal resets bound open state; unload releases subscriptions.
Technical-details dialogs recheck the setting's open state and loaded card after
entering the queue. Closing or unloading clears that state and cancels pending display.
The title-bar heart and Settings Info support card open the support dialog on demand
through the same modal queue.

Operation owners start footer tasks and report stages with measured or indeterminate
progress. The shell aligns task slots with the shared page container, constraining
text while keeping log and cancellation actions visible. Cancellation requests the
owning token; the task stays active until work
stops. Completed changes and errors remain visible. XML generation creates a
non-cancellable task after confirmation/file selection and completes it before its
result dialog. Initial reads use page-local loading indicators.

Logs and history opens one query across saved sessions and receipts, without a file
selector. Read source pages backwards with UTF-8 byte cursors and merge their
timestamped heads, retaining each source's buffers. Each page publishes up to 200
matching records with a bounded scan budget; sparse queries can return no matches
with further activity available. Topic, activity-type and search filters apply to
typed JSON Lines fields, including stored topics, original values and exception details. Unreadable rows and
failed sources remain explicit alongside successful records.
Topic groups start collapsed; focused searches, a selected topic and problem filters
expand matching groups. Loading older activity preserves expansion. Refresh replaces
the query; filter changes and page exit cancel reads and reject stale results.
Only completed setting changes produce receipts; empty batches write nothing.
Confirmed log clearing serializes with writes, deletes saved diagnostic sessions and
truncates the active stream. Change history is retained.

## App inventory sorting

External Apps enumerates machine and interactive-user uninstall registrations in
both registry views, merges catalogue matches and adds other visible programs under
Other Utilities. System components, child entries and update registrations are
excluded from the added inventory. Each status refresh reconciles additions and
removals before detection. Discovered programs require their exact registration for
installed status and uninstall-command lookup; commands are read again before use.

After installation discovery, read matching installed products' date, size and shell
usage metadata on a worker thread. Unavailable evidence remains null; removed products
lose their prior metadata. Publish property notifications and refresh the active sort
on the UI thread, even when installation state did not change. Refresh repeats the
metadata read. All views share the menu ordering; grouped views retain their sections.
Table-header sorting applies until another menu choice. Saved menu choices restore on
initialization; unsupported saved values use Groups A-Z.

## Application removal and residue recovery

The standalone **Leftover** tab scans when selected or refreshed. It reads MSI
products, every installed-product patch state and repair-source references, then uninstall records
for the machine and registered accounts in both views. Advertised products retain
their source references. Offline-account hive failures report the Windows error
code and description and leave reference coverage incomplete. Installed-only properties and patches are queried for
installed products. A source-list configuration error before any source is read
is resolved only when the exact product registration exists and has no `SourceList`
key in either registry view. Unreadable, damaged or changing lists retain incomplete
coverage. Explicit missing application evidence identifies stale
registrations and their remaining installation folders within machine application
roots or account AppData. Folder trees must be aged, fully readable and free of
executable payloads and active/shared references. Eligible folders include their
stale registration in the verified backup and cleanup; files are removed before
empty directories. Only selectable leftovers are published. Installer caches retain
age, use and coverage protections. Incomplete/cancelled scans cannot authorize a
cleanup with selections from an earlier scan.

Confirmed cleanup reserves normal mode and checks opaque selections against fresh
inventory. It creates and verifies a shared uninstall recovery backup, rechecks
ownership, then deletes only captured files, unchanged registry values and empty
selected keys. Changes, failures and cancellation preserve recovery information.
These selections do not enter configuration exports or startup app inventory.

Windows Apps and External Apps support direct removal and configuration actions.
External Apps imports default to Select Only. Review captures checked external app
IDs before exiting review, then removes them after enclosing overlays/dialogs close.

The inactive-app switch queries device-retained AppX packages on demand and matches
existing catalogue entries. All views and Select All use the same visibility/search
filter. Turning it off hides and deselects inactive entries. Inventory errors remain
visible; backups still use current-account installation status.

1. Reserve system-operation state and capture selected product registrations,
   package identities, executables and paths across registered user/service profiles.
   Monitor only discovered product locations; inaccessible sources become notices.
2. Await native uninstallers and retain per-app or shared-batch outcomes. Registered
   external uninstallers require exit code 0; 3010/1641 mean restart-deferred removal.
   Failed, skipped and deferred outcomes cannot authorize residue deletion.
3. Recheck package, registration, executable and process evidence while folder activity
   settles: two quiet seconds, bounded by a 30-second monitoring deadline. A deadline
   does not establish removal. Inspect owned trees and shared registry references,
   protecting resources for active or unverifiable installations/accounts.
4. Present initially unselected registry/path trees. Rescan rebuilds evidence and
   selection while retaining native outcomes. Configuration workflows defer these
   reviews until their progress overlay closes.
5. Explicit cleanup accepts only known selectable IDs, revalidates products/resources
   and publishes a complete verified backup before deletion. Delete leaves before
   parents; remove parents only when empty. Cancellation retains completed counts
   and the published backup ID.
6. Recovery authenticates metadata and verifies every file payload before mutation.
   Restore parents before leaves and preserve conflicting destination data. Release
   monitors, sessions, footer tasks and reservations when their owner completes.

## Cleaner

1. Restore `Cleaner.SelectedAreas` once, preserving empty selections and edits made
   during loading. Load validated `CleanerOptions` separately. Await preference
   writes and account-dependent package-cache refreshes, ignoring stale completions.
   Each scan copies its options and four independent SID selections. Null uses the
   interactive account; empty selections skip user targets. Enabled common locations
   run once. Unavailable accounts produce notices.
2. Invalidate the prior plan, then scan selected stages in this order: privacy,
   junk files, shortcuts, registry values, registry compaction, disk analysis.
   Discovery reports inventory counts; validation reports measured completed/total
   units. The aggregate percentage reflects work stages, not elapsed-time estimates.
3. Resolve account-specific paths and package registrations through scoped sessions.
   Offline registry mounts remain alive until their handles close, including failure
   and cancellation. Registry reference probes cache within the scan/account and
   respect registry views. Ambiguous/unreadable references produce notices.
4. Inventory enabled junk categories in profile and machine locations, applying
   diagnostic file patterns before inspection and deduplicating paths. Retain
   recent junk files as non-actionable until their modification age meets the
   configured limit (0–365 days, default seven). Zero includes recent inactive
   files. Keep the scan start time as the access boundary for backup and deletion.
   Keep active-app/locked privacy files and locked junk files non-actionable. Shortcut checks require
   confirmed missing local targets and use the independent recent-shortcut switch.
   Deduplicate overlapping privacy/registry values and privacy/shortcut paths,
   retaining the first occurrence. Temporary hive analysis files are removed.
5. Publish an immutable GUID-identified plan only after the entire scan succeeds.
   Failed/cancelled scans publish no partial plan. Once the footer task completes,
   the visible scan page opens results, including a successful empty scan. Grouping
   retains original items and first-occurrence category order; collapse preserves
   selection. Diagnostic findings cannot be applied; registry values require review.
6. Confirm cleanup and validate the complete ID selection against the current plan.
   If enabled, prepare and verify all selected file/value backups before the first
   mutation. Failed/cancelled preparation leaves resources untouched. File copies
   use exclusive handles with access timestamps preserved; publish and reread the
   account-bound manifest only after payload checks succeed.
7. Apply file/privacy cleanup and shortcut removal before registry corrections,
   then compact hives and optimize the disk. Reopen account targets, validate root
   bindings, paths, file metadata and exact registry type/data/reference evidence.
   Consume each finding before execution, preventing replay. Configured hive backups
   are verified immediately before native compaction. Disk analysis uses `defrag /A`;
   confirmed media-appropriate optimization uses `/O`.
8. Cancellation stops between operations and cancels external disk work; an active
   native compaction finishes first. Completed changes/backups remain. Individual
   supported failures are reported while later selections continue. A new scan
   refreshes evidence; stopped operations cannot publish late UI progress.

Cleanup and hive-backup switches are independent and default on. Uninstall residue
cleanup always requires its own verified backup regardless of these Cleaner options.

### Cleaner recovery

1. Refresh the backup list in Settings and confirm a selected session. Authenticate
   account-bound metadata and verify protected storage ownership/permissions.
2. Verify every file payload before restoring anything. A missing/corrupt payload
   blocks recovery. Reopen registered account targets and reject changed roots,
   reparse paths and active supported applications.
3. Restore missing files through exclusive handles, verify resolved paths/content
   and restore modification times. Remove partial failed copies. Restore only the
   deleted registry value, retaining its type/view and requiring an existing parent.
4. Preserve differing current data as conflicts; identical content is already
   recovered. Report individual conflicts and retain completed recovery on
   cancellation. Backups remain available for retry. Whole-hive backups use their
   accompanying offline recovery instructions.

## Security

1. Page construction performs no Windows discovery. Explicit Scan/Deep scan starts
   a cancellable footer task, clears previous selections and invalidates the prior
   report. Standard checks cover five categories; deep scan adds five policy/privacy
   checks. Native reads use one isolated process; existing privacy settings use
   their authoritative discovery service.
2. Failed individual reads produce unavailable observations. Known active Defender
   detections do not constitute a new malware scan. Windows Update reads only its
   local cache; an empty result cannot establish current patch coverage.
3. Publish the immutable, dated report only when its complete expected check set
   returns successfully. Cancellation/fatal failure publishes no report. The UI
   shares observation rows between category summaries, selection and the table.
4. Capture selected IDs and show each change's effects in confirmation. Validate the
   complete selection against the current report and reserve normal application
   mode before any writes. Re-read exact evidence immediately before mutation;
   changed or unavailable evidence blocks that item. Privacy fixes delegate to
   their existing setting owner. Firefox retains an existing policy homepage URL
   or explicitly selects `about:home`, then locks it; restart Firefox to activate.
5. Verify native/setting readback and report each success or failure. Consume
   attempted evidence before execution, requiring a fresh scan before retrying.
   Cancellation stops between writes and retains completed receipts; an active
   native write unit finishes first. Navigation cancels active work; shutdown drains
   pending fixes. Test/review modes retain read access and block fixes.

## Drivers

1. Load validated saved `DriverScanOptions`. Reserve a scan task and collect PnP
   registrations, presence, INF bindings and driver-store inventory. Unreferenced
   candidates exclude inbox/boot-critical packages and every registered device's
   bindings. Incomplete binding evidence suppresses that classification.
2. Query Microsoft's public Windows Update service for non-hidden, uninstalled
   driver offers. Download/export payloads as needed without accepting outstanding
   licenses. Failed acquisition remains unverified evidence with notices.
3. Match hardware IDs and OS architecture against INF models, read versions and
   verify catalogs with SetupAPI. All matching INFs must provide WHQL/logo signer
   evidence for positive certification. Deduplicate update IDs, exclude known
   equal/older versions and apply the saved WHQL policy. Preserve unknown versions.
4. Publish local candidates and applicable offers with the exact scan options.
   An online failure retains local results with `UpdatesChecked=false`. Cancellation
   or navigation stops further processing and cleans private exports; Windows owns
   its download cache. Changed preferences require a fresh scan.
5. Confirm selected package/device removal in normal mode. Read fresh inventory and
   revalidate exact identities, preserving reconnected devices and changed/bound/
   protected packages. Process device registrations before packages using PnPUtil,
   without force or automatic reboot. Cancellation waits for the active native
   command, then stops before the next item. Remove successful rows; retain failed
   or unprocessed selections and report restarts. Complete the task on every exit.

## Extension and Control

Extension opens the SysMonitor overview and settings. Control opens environment,
hosts and battery pages alongside its connection and management tools. Navigating
to these pages selects Control and preserves native Back/Forward history. Cleaner
opens Events cleanup as a separate page, retaining Cleaner as the sidebar owner.

Optimize's Network tool performs read-only discovery on entry/Refresh. Confirmed
edits validate the catalogue ID/value, reserve normal mode, compare the current
baseline, write one parameter and verify readback. Failures retain partial results
and require Refresh. Quick enhancement applies normal TCP auto-tuning and global
RSS. Source batching, ranges and legacy overrides are documented in the
[network guide](src/WinOptimizer.Infrastructure/Features/Optimize/README.md).

Optimize's Features tool reads restriction states on entry/Refresh. A toggle is on
when all applicable settings are restricted; missing service groups are unavailable.
Confirmed changes resolve all settings before applying them sequentially through
their owner. On applies restrictions; off enables policies and restores declared
Windows default service startup choices. Absent services are excluded; an empty
selection reports no change. Failure stops later writes and retains completed work.
Copilot applies Windows/Edge policies, then removes the identified interactive user's
standalone app and verifies its absence. Removal failure reports the applied policies.
Its toggle reflects policy state; off does not reinstall the app. Every write rereads
restriction state, including partial failures; unreadable state disables the toggle
until Refresh. Service startup changes require a manual restart. Both tools block
Test/review writes, drain work at shutdown and retain preset/export behavior.

Customize's context-menu page discovers on entry/Refresh on a worker thread, groups
registrations by program and loads local icons with bounded concurrency and glyph
fallbacks. Single entries have a toggle; groups have individual toggles, enabled
counts and a header that stays on while any entry is enabled. Search preserves group
identity, icons and expansion while expanding matching children. Captions identify
target/user scope; tooltips hold registry locations.
Changes reserve normal mode, validate the issued entry and reread its exact evidence.
Static verbs use `LegacyDisable`; COM handlers retain their GUID with a reversible
disabling prefix. Only verified readback updates the model; failed edits retain the
baseline until Refresh. Group changes use this path sequentially, skip matching states,
retain successes and report failures while continuing. Closing drains writes.
Test/review modes permit reads; registrations are outside configuration import/export.

Overview pages do not run utility discovery. Tool pages explicitly load their data
and retain editor drafts across navigation. Writes hold normal-mode reservations;
shutdown drains in-flight work.

- **Environment:** validate the raw variable draft, serialize Save, compare the
  original registry entry with the current one, write only that variable and
  broadcast `WM_SETTINGCHANGE`. Report persistence separately from notification
  delivery. PATH rows and raw mode edit the same value without expansion.
- **Hosts:** validate the shared draft and compare its revision with disk. Write and
  verify an exact-byte backup, preserve encoding/BOM, recheck the revision and
  atomically replace the file with its permissions retained. Undo uses the same
  save sequence and creates another backup.
- **Battery:** discover manufacturer/battery and matching OEM capability without
  writing. Apply rereads capabilities, validates the advertised percentage, writes
  and rereads firmware; only exact readback confirms it. Discovery, rechecks and
  successful changes collect fresh telemetry. Setup links require normal mode and
  open official routes for manual installation. Protocol and unit details belong
  in the [provider guide](src/WinOptimizer.Infrastructure/Features/Utilities/README.md).
- **Connection:** load dated adapter/DNS/VPN data and Windows-retained usage for
  30 local calendar days, respecting DST/current-day boundaries. While visible,
  sample every two seconds into a 15-minute window. New/reset/missing counters or
  gaps over ten seconds establish new baselines; unknown intervals break chart
  lines. Navigation cancels sampling and rejects stale results. DNS Apply uses its
  catalogue option index, then refreshes addresses; VPN management opens Windows
  Settings. Both changes require normal mode.
  The **Saved Wi-Fi passwords** link navigates to a separate page. Entry/Refresh
  enumerates WLAN interfaces and saved profiles, requesting plaintext keys under
  Windows access checks. Encrypted keys stay unavailable; per-profile failures
  preserve other results and report diagnostics. Alternate-account elevation is
  rejected before discovery. Leaving cancels reads, rejects late results and clears
  the in-memory credential snapshot. These reads remain available in Test mode.
- **Events:** inventory channels, confirm explicit selections, reserve normal mode
  and validate elevation/names before creating protected archive storage. Clear
  selected logs sequentially with Windows' clear-with-backup API, verify archives
  and refresh metadata. Per-log failures allow later channels to continue.
  Cancellation stops between native calls and retains completed receipts.

Windows servicing cancellation stops the active owned process tree, drains output
and retains completed phase receipts before releasing the operation reservation.
Phase plans, source constraints and cancellation are maintained
in the [servicing guide](src/WinOptimizer.Infrastructure/Features/Management/README.md).
Autorun mode changes clear the prior inventory and automatically scan the selected
sources. Advanced scans wait for explicit license/download consent; accepting it
starts the pending scan. Refresh retries failed scans and replaces dated snapshots.
Discovery/mutation and optional scanner acquisition are described in the
[autorun guide](src/WinOptimizer.Infrastructure/Features/Management/AUTORUNS.md).
Process sampling, identity checks and selected capture lifetimes are described in
the [process monitor guide](src/WinOptimizer.Infrastructure/Features/Management/PROCESS-MONITOR.md).

## SysMonitor

1. Explicitly load/validate `Extensions.SysMonitor`. Register its SID-scoped logon
   task when both Enabled and StartWithWindows are true; otherwise remove that task.
   An absent task or scheduler folder already satisfies the disabled state;
   other scheduler failures remain visible. Startup-affecting edits update the
   task before preference persistence and roll it back on save
   failure. Other valid edits apply to the widget after successful persistence.
2. Automatic logon activation restores only the widget. Regular shell initialization
   waits for a manual open; stale task activations exit, and startup failures open
   the shell for diagnosis. Manual launches reuse the existing process/window.
3. Enabling creates one window on the UI dispatcher and excludes it from taskbar/
   window switchers. Publish Running only after it opens. Failed starts release
   partial resources and remain retryable through Show without faulting saves.
4. On a worker thread, read native counters and lazily open the hardware
   session. Supplement missing sensor types from existing WMI providers. Validate
   measurements, preserve missing data and expose independent provider notices.
   Compute CPU/network deltas from matching counters and monotonic elapsed time;
   new/reset adapters cannot contribute rate spikes.
5. Publish a typed snapshot, retain at most 60 CPU readings and wait the configured
   interval; reads never overlap. Expanded-section visibility reuses current readings
   and remeasures the native window. Theme/size/scale/refresh changes use saved options.
   Scale applies to the entire compact or expanded panel in addition to display DPI;
   native sizing retains work-area bounds and scrolling for tall content.
   Optional hardware charts retain 60 samples per sensor, aligned across readings;
   missing samples break lines, and sensors with no remaining history are released.
   Percentage charts use 0–100%; other axes fit retained measurements in their physical units.
6. Disabling cancels sampling and closes the window. Active WMI work finishes or times
   out before handles close. Re-enabling awaits that cleanup before resetting baselines.
   Main-window navigation/minimizing/closing preserves a running widget.

## System information and Restore

System information reuses the session's last completed snapshot. First entry or
Refresh queries declared local WMI properties on a worker thread, normalizes typed
values and records category-level failures while continuing other categories.
Cancellation stops between queries/enumeration waits; cancelled/failed refreshes
retain the prior cache. Successful completion publishes a timestamped snapshot.
Localization and category selection reuse its records. Navigation rejects late
publication; the cache lasts until exit without disk persistence.

Restore loads available restore points on a worker thread and publishes newest-first
only after success. Creation verifies the point before refreshing. Deletion confirms
and removes the exact sequence number; failure retains its row. Cancellation before
native work prevents it, while an active native call finishes. Windows defaults
reserves an operation, confirms, loads version-compatible defaults and executes with
`IsWindowsDefaults`, including policy cleanup. Test mode permits inventory/export
and blocks system changes.

## In-app updates

After startup's 30-second delay, read the persisted UTC attempt time. Enforce the
24-hour interval across restarts. Read `Updates` at each attempt and run only enabled
checks; both disabled means wait another interval without recording an attempt.
Persist the attempt timestamp before work; write failure skips checks and is logged.
Failed attempts count toward the interval.

Removal-script maintenance reserves system-operation state; failure or a rejected
reservation is logged while version lookup can continue. Cancellation stops delays
and version requests; active script work finishes first. Notifications dispatch to
the UI. Manual checks run immediately and independently of automatic switches.

`VersionService` reads the latest published stable release, validates its date-based
tag and compares it with the installed date-version. Available updates download and
launch the stable installer while preserving install directory and portable mode.
Unsupported release states/tags produce no available update.

## Build and packaging

Commands, options, workflow triggers and artifact retention are in the
[development guide](README.md#development); test entry points are in the
[test guide](tests/README.md).

Focused native UI runners resolve MSBuild, restore cached packages and build through
`tests/Build-NativeTestHost.ps1`. A failed build or missing executable stops the runner
before its scenarios execute; each runner also checks the host's exit code.

`build.ps1` restores and builds through Visual Studio MSBuild. `Version.props`
validates the date-based version before compilation and supplies assembly, file
and product metadata. Success requires a zero native exit and the expected EXE.
The script restores temporary PATH/working-directory changes on every exit.

`extras/build-and-package.ps1` runs these stages:

1. Reject contradictory signing options, resolve Inno Setup, and resolve/validate
   the shared version once for the entire run, including across midnight. Resolve
   signing choices before building; unsigned CI bypasses prompts and certificates.
2. Refresh bundled WinGet/VC++ assets, then call `build.ps1` for incremental Release
   with the resolved version.
3. Run the main suites and UI compilation check unless explicitly skipped. Native
   exit codes and complete per-project TRX results must agree; failures stop packaging.
   Failed test names, error messages and stack traces are printed from TRX in the CI log.
4. Reject a linked `build/publish/`, recreate it and publish with `NoBuild=true` and
   the same version. The UI project adds its generated `win-optimizer.pri` to the
   publish items; a missing index fails publishing. Sign the published app when
   requested; signing failures stop.
5. Apply the resolved version to Inno's numeric/text metadata, compile the installer
   under `build/installers/`, verify its existence and sign when requested. Generated
   tests, tools and logs stay outside the payload.

CI resolves the version through `Version.props` before running checks, using the
date from a `vYY.MM.DD` tag or the manual version/build date for other runs. The
resolved value is passed to packaging and versioned artifact names. It first runs
workflow/versioning/packaging/installer checks, then the unsigned packaging script.
Before upload, it compares product/file text and numeric versions across
the installer, published EXE/DLL and a temporary portable installation, using the
published assembly's version rather than a new date calculation. It also verifies
installed bytes, runtimes, the application's compiled UI resource index and language
resources. Installer upload requires success;
TRX results and build logs upload even on failure.

Pushing a `vYY.MM.DD` tag starts `release.yml`, which calls the shared installer
workflow at the tagged commit. Invalid calendar dates fail before packaging.
Only after the complete build and validation succeed does the publishing job
download that run's versioned installer artifact and create a stable GitHub Release
with generated notes and `win-optimizer.Installer.exe`. Download/publication failures
fail the job; GitHub must already contain the pushed tag.

`win-optimizer.ps1` downloads the latest stable release installer and opens its
interactive wizard. Setup offers regular or portable installation followed by a
destination folder picker, retaining each mode's chosen folder when navigating Back.
Explicit `/DIR` destinations remain exact during silent in-app updates. Inno tracks
the installed payload and replaces it during updates. Untracked files in the chosen
folder remain intact during updates and uninstallation. Regular
installs register file associations, selected shortcuts and an uninstaller; portable
installs write `portable.marker` without these registrations. Both use the shared
application storage layout. Setup rejects an in-place installation-type change
before writing files or registrations. The PowerShell launcher reports the installer's exit
status; the wizard controls the optional app launch.

Uninstallation asks whether to keep the shared application-data store for
reinstallation or permanently remove it for all accounts. Cancellation exits
before deletion. Silent uninstall defaults to retention; `/DATA=keep` and
`/DATA=remove` select an explicit policy, and invalid values abort.
Full cleanup validates the fixed storage path and its ancestors before confirmation
and again at `usUninstall`, then removes data before tracked application files.
Child junctions are removed without traversing their targets. Cleanup failure
stops uninstall with an actionable error, allowing a retry or data retention.
Applied Windows changes remain in place; configurations depending on scripts in
the data store should be restored in the app before full cleanup.
