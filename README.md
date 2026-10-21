<img src="src/WinOptimizer.UI/Assets/AppIcons/win-optimizer.png" width="80" height="80" alt="win-optimizer — Windows optimization and maintenance app" />

# win-optimizer — Windows 10 & 11 Optimizer

win-optimizer combines Windows cleanup, app removal, privacy settings, desktop
customization and system maintenance. It also provides hardware monitoring,
process inspection and Windows diagnostics.

Preview settings in **Test mode** and review cleanup results before choosing what to apply.

## Download

**[Download win-optimizer for Windows 10 & 11](https://github.com/sh1zen/win-optimizer/releases/latest)**

Open `win-optimizer.Installer.exe`, select **regular** or **portable**, then browse
or enter a destination folder. Regular installs register with Windows and support
shortcuts and uninstallation; portable installs use a movable app folder.
Updates and uninstallation preserve files that setup did not install. Changing
installation type requires another folder or removal of the existing installation.
See [data and uninstallation](#preferences-and-app-updates) before removing shared data.

**x64 · .NET and Windows App SDK runtimes included.**

<details>
<summary><strong>Prefer PowerShell? Install with one command</strong></summary>

```powershell
irm "https://raw.githubusercontent.com/sh1zen/win-optimizer/main/win-optimizer.ps1" | iex
```

The command downloads and opens the same setup wizard to choose regular or portable
installation and its destination folder. Installation and in-app updates use the latest published stable release.
System-level operations require the applicable Windows administrator permissions;
some hardware tools also depend on compatible firmware or drivers.

</details>

## Explore more

<details>
<summary><strong>User guide — features, settings and backups</strong></summary>

## Getting started

Opening the app shows a loading window while settings and the main interface initialize.

1. **Pick a task.** Open Software & Apps, Optimize, Cleaner or another section from
   the sidebar. Overview cards lead to individual tools; Optimize and Customize
   also offer search suggestions.
2. **Inspect your choices.** Read the current values and available options. Use a
   setting's **ⓘ** button or **Ctrl+D** to inspect registry values, scripts and
   dependencies; close the dialog with **Close**, **Escape** or **Ctrl+D**.
3. **Preview settings when needed.** Use the title-bar **Test mode** button to build
   a temporary configuration draft before applying supported settings to Windows.
4. **Review and confirm.** Select scan results or changes, confirm the operation
   and follow its progress in the shared footer. Cancellation is available where
   supported; logs and history help you review the outcome.

Use **Back/Forward** or **Alt+Left/Right** to move between section overviews and
subpages, including Settings categories. Selecting a sidebar destination opens its
overview. App inventory has its own loading indicator.

## Windows optimization features

| What you want to do | Where to start |
| --- | --- |
| Remove unwanted Windows apps and installed software | [Software & Apps](#remove-unwanted-apps-and-review-leftovers) |
| Adjust privacy, gaming, power and desktop settings | [Optimize and Customize](#fine-tune-windows-settings-and-privacy) |
| Clean temporary files, caches and broken shortcuts | [Cleaner](#clean-junk-files-and-reclaim-disk-space) |
| Review antivirus, firewall and security settings | [Security](#review-windows-security-settings) |
| Inspect unused drivers and available driver updates | [Drivers](#inspect-driver-packages-and-updates) |
| Watch hardware activity with desktop widgets | [Extension](#monitor-hardware-with-desktop-widgets) |
| Manage system editors, battery charging, startup, processes and Windows health | [Control](#manage-startup-processes-and-windows-health) |
| Prepare Windows installation media and reusable configurations | [Advanced Tools](#prepare-windows-installations-and-restore-configurations) |

### Remove unwanted apps and review leftovers

**Software & Apps** lists Windows apps, capabilities, optional features and external
software for selected uninstallation. Table, card and compact views share saved
sorting by group, name, installation date, size or last usage. Dates/sizes sort
newest/largest first, with unknowns last. Cards and compact views retain groups;
table headers also offer column sorting.

Installation dates can reflect updates, sizes are estimates or packaged file sizes,
and last usage depends on the interactive account's available shell launch history.
Unavailable values appear as **—**. **Show hidden or inactive installed apps** is
an administrator-only opt-in for catalogued AppX packages retained on the device
without a current-account registration; inactive entries have orange indicators.

After uninstallation, review detected registry resources, files and folders before
choosing **Delete selected**. Active, shared, restart-deferred and unverifiable
resources remain protected, and coverage gaps are reported. **Scan again** refreshes
the evidence. Every residue cleanup requires a verified backup, which you can
restore in the review or through **Uninstall cleanup backups**.

Configuration imports can select apps for review or explicitly request removal;
External Apps defaults to selection only.

App icons use artwork from installed Windows packages and desktop programs' registered
local icons. **Refresh** reloads desktop icon references; entries without readable
artwork use category glyphs.

External Apps combines its supported app catalogue with installed desktop programs
registered by Windows for the machine or current account. **Refresh** discovers
new installations. Programs outside the catalogue appear under **Other Utilities**;
automatic reinstallation is available only for supported catalogue entries.

The **Leftover** tab lists verified obsolete app registrations, unused MSI/MSP/EXE
installer-cache files and aged installation folders identified by stale registrations
in Program Files, ProgramData and account AppData. Executable payloads, active
references, unknown ownership, incomplete coverage, ongoing installation, pending
restart or recent use exclude affected files/folders. Review paths and explanations,
select entries and choose **Delete selected**. The summary includes selections hidden
by search. Cleanup creates a verified backup under **Uninstall cleanup backups**;
**Refresh** rescans. See the [leftover scan guide](src/WinOptimizer.Infrastructure/Features/SoftwareApps/Leftover/README.md)
for locations and eligibility.

### Fine-tune Windows settings and privacy

**Optimize and Customize** bring privacy, gaming, Windows Update, power, sound,
notifications and desktop customization into searchable categories. Controls show
current settings and available choices. Advertising, suggestions and promotional
content have individual controls in Privacy & Security, Notifications and Start Menu.
Configuration review shows queued changes matching your search.

- **Context-menu customization:** enable or disable a program's supported Explorer
  registrations together, or expand a group to change individual entries. Icons,
  enabled-entry counts, search and registry-location details help identify them.
  Changes apply immediately and can be reversed with the same toggle; machine
  registrations may require elevation. Group operations report partial failures.
  Registry commands, COM handlers and file-type registrations are supported;
  packaged Windows 11 extensions may not appear.
- **Network tuning:** inspect 25 controls for TCP behavior, offloading, IPv4/IPv6
  limits and legacy name-resolution priorities. Confirm each edit and use **Refresh**
  to reread Windows values. **Clear override** removes a legacy priority override;
  those priorities may be ignored by modern resolution and require a restart after
  changes. **Enhance network performance** restores normal TCP receive-window
  auto-tuning and enables global RSS. Results depend on your network and adapter;
  these settings do not guarantee speed improvements. DNS, proxies, VPNs and
  adapter-specific choices remain intact. See the [network controls guide](src/WinOptimizer.Infrastructure/Features/Optimize/README.md).
- **Optional Windows features:** confirmed toggles control selected services,
  diagnostic data, Cortana, Copilot and advertising. On applies the named
  restriction; off restores related policies and declared service defaults.
  Previous custom service choices are not restored. The services toggle covers
  installed Retail Demo, Offline Maps and Fax services and requires a restart.
  Windows 10 also offers Timeline activity publishing. Home/Pro can still send
  required diagnostic data when telemetry restrictions are enabled.
- **Copilot controls:** apply Windows/Edge policies and remove the interactive
  account's standalone Microsoft Copilot app and local data. The toggle reflects
  policy state; turning it off restores policies. Reinstall the app from Microsoft
  Store if needed. Edge coverage depends on version and profile; web access remains
  available. Individual policy/service controls are also available under Privacy
  and Gaming and Performance.
- **Update notifications:** on shows standard Windows Update notifications; off
  suppresses them, including restart warnings. Recommended and Windows-default
  configurations leave notifications on.

Context-menu, network and optional-feature tools block changes in Test mode and
configuration review. Network edits verify Windows readback and expose discovery
errors. Optional-feature tools run separately from configuration presets and exports.

### Clean junk files and reclaim disk space

**Cleaner** covers privacy traces, junk files, shortcuts, registry cleanup, registry
defragmentation and disk optimization. Scan first, expand grouped results, select
actionable findings and confirm cleanup. Scan choices save automatically; changing
options requires another scan. Failed or cancelled scans provide no cleanup plan.
Disk analysis displays the selected drive and the full, selectable Windows report.

Use **Settings → Cleaner** to choose scan targets, file age, registry categories,
deep scanning, cache locations and the drive to optimize. Privacy, junk, registry
and shortcut scans have independent account selections. The interactive account
is selected initially; clearing all accounts skips user data while enabled common
locations still run. Unavailable profiles produce notices.

<details>
<summary><strong>Cleanup coverage, protected files and recovery</strong></summary>

- **Junk files:** user/Windows temporary folders, diagnostic logs (CBS archives,
  DISM, setup and Windows Update), WER reports, crash dumps, temporary Internet
  files, thumbnails and DirectX shader caches are enabled by default. Saved
  exclusions are respected. Installed-app LocalCache/TempState folders are separate
  opt-ins. Windows Installer, component-store and Prefetch files are outside these
  rules; event-channel maintenance is available from Cleaner.
- **File age and activity:** all detected junk appears in results, but the default
  seven-day modification-age limit protects recent files with disabled selection
  and a reason. Choose 0–365 days; zero includes recent inactive files. Locked files
  and files accessed after scanning begins remain protected. Close their app and
  rescan before cleaning unavailable files.
- **Privacy traces:** supported browser caches/history and Windows/application
  traces use known locations. Office/WinRAR history, jump lists, offline browser
  caches and Defender support logs are separate opt-ins. Clearing jump lists also
  clears pinned items. Active or locked files show a reason and cannot be selected;
  portable apps and custom data paths may fall outside coverage.
- **Broken shortcuts:** selected Desktop, Start menu and recent-document locations
  are checked for confirmed missing local targets. Unresolved or unavailable network
  targets are retained. Recent-document scanning defaults on independently of privacy
  history, and overlapping findings appear once.
- **Registry maintenance:** cleanup checks typed references in applicable registry
  views and requires explicit selection of values. Service, virtual-device and
  firewall findings are diagnostic. References and file evidence are rechecked
  before changes. Compaction uses selected registry accounts; deep scanning also
  analyzes machine hives.
- **Backups:** file/value backup and whole-hive backup options default on. Backup
  preparation must finish before selected resources change. Restore file/value
  backups from Settings; conflicting current data is preserved. Whole-hive backups
  include offline recovery notes. Cancellation retains completed work and backups;
  active native hive operations finish before stopping.

</details>

Use **Cleaner → Events cleanup** to select and confirm event channels to clear.
Windows creates EVTX backups; per-channel results report failures and
archive-verification errors. Partial cleanup shows a completion summary; complete
failures and archive issues show an error. Open archives in Event Viewer.

### Review Windows security settings

**Security** checks antivirus protection, Windows Firewall profiles, known active
Defender threats, UAC and pending security/critical updates in the local Windows
Update cache. Empty cached update results need an online Windows Update check.
Threat inspection reads existing detections; use Windows Security for a current
malware scan and verify third-party antivirus registrations with their provider.

**Deep scan** adds optional hardening and privacy checks for ISATAP, Teredo, Firefox
homepage protection, language-list access and app-launch tracking. Select supported
findings and confirm **Apply selected fixes**. Network changes can affect managed
networks or games. Firefox protection locks its policy homepage, using `about:home`
when no policy URL exists, and requires a browser restart. Custom Firefox policies
and portable installations need manual review.

Fixes recheck evidence and verify readback, with explicit results for changed,
unavailable, managed or tamper-protected settings. Rescan before retrying attempted
fixes. Cancellation retains completed changes. Test/review modes allow scanning
and block fixes; UAC, threats and updates require review in Windows settings or
your antivirus.

### Inspect driver packages and updates

**Drivers** scans unreferenced driver-store packages, disconnected device
registrations and applicable offers from Microsoft's public Windows Update service.
**Only show WHQL-tested driver updates** defaults on in Settings → Drivers;
verification uses matching INF/catalog evidence, rather than a trusted signature alone.
Scanning may download packages for inspection, but does not install updates or
automatically accept licenses. Online failures are reported separately from empty results.

Select unused packages or disconnected devices and confirm **Uninstall selected**.
Removal requires elevation and a fresh inventory check; bound, changed, inbox and
boot-critical packages are protected. Removing a disconnected device retains its
package, and that device may still be needed. Cancellation stops between native
removals and reports completed work, failures and restart requirements.

### Monitor hardware with desktop widgets

The **Extension** section provides desktop widgets:

- **SysMonitor:** follow CPU, memory, network and available hardware sensors in a
  movable compact or expanded desktop widget. Customize theme, refresh interval,
  always-on-top behavior, expanded sections and scale (50–200%, default 100%).
  Preferences save automatically; scale changes apply immediately. Expanded GPU,
  clock, power, voltage and storage cards show 60-sample charts with colored legends
  and full sensor/device names on hover.

SysMonitor stays active when you close the main window; use its gear or launch the
app again to reopen the window. Disabling the last widget while the window is hidden
exits the process. **Start with Windows** is opt-in and requires an administrator
desktop account. Reopen a moved portable installation to update the logon task path.
Low-level sensors use LibreHardwareMonitor and may require elevation and **PawnIO**,
linked from Settings; install it and restart the app for supported readings.

Sensor support depends on hardware, firmware and drivers.

### Manage startup, processes and Windows health

Open **Control** for system editors, battery charging, connection management,
diagnostics and maintenance:

- **Environment variables editor:** edit user/system variables and ordered PATH
  entries, or use raw text, then **Save**. Values remain unexpanded; user edits target
  the interactive account even under alternate-account elevation. Restart existing
  processes when needed to pick up changes.
- **Hosts File Editor:** edit IPv4/IPv6 mappings, aliases, comments and enable
  switches, or use text mode. **Save changes** validates the draft, rejects external
  changes, verifies a backup and preserves encoding and permissions. **Undo last
  save** uses the same save workflow to restore it.
- **Battery charge limit:** discover supported OEM controls, select an advertised
  threshold and choose **Apply limit**. Only matching firmware readback confirms
  success. Discovery selects 80% when available without applying it. Compatible
  Dell, HP, ASUS and Acer endpoints have direct providers; other devices receive
  manufacturer guidance. **Install OEM support…** opens an official installation
  route; **Recheck support** repeats discovery. Battery health, capacity and available
  power readings accompany the controls. See [battery provider coverage](src/WinOptimizer.Infrastructure/Features/Utilities/README.md).
- **Connection:** inspect adapters, DNS, Windows VPN profiles, 30 days of retained
  usage and a live 15-minute traffic window while the page is open. DNS changes
  require **Apply**; VPN management opens the interactive user's Windows Settings.
  Test/review modes allow reads and block changes.
- **Saved Wi-Fi passwords:** view saved profiles, adapter/security details and
  readable passwords on a separate page. Windows permissions govern access; open
  networks, enterprise authentication and unavailable keys have distinct labels.
  Credentials stay in memory and clear on leaving the page. Use the signed-in
  account; alternate-account elevation cannot read this inventory.
- **Autorun management:** review startup entries grouped by user, shared and system
  scope. Search, filter and confirm supported native enable/disable actions. Mode
  changes load automatically; Advanced mode uses Microsoft Autorunsc after explicit
  license/download consent and starts its first scan. Advanced entries are for
  inspection. See the [autorun guide](src/WinOptimizer.Infrastructure/Features/Management/AUTORUNS.md).
- **Process monitor:** explore a process tree, CPU/memory/I/O, process details and
  TCP/UDP ports. Sort by Process, PID, CPU or Memory; click again to reverse, or
  choose **Process tree** to restore the hierarchy. Open-file queries use Microsoft
  Handle after license/download consent. File queries and optional ETW traffic
  observation require elevation; traffic stops on selection change, navigation or
  shutdown. See the [measurement guide](src/WinOptimizer.Infrastructure/Features/Management/PROCESS-MONITOR.md).
- **Windows status and restore:** run quick health checks, full diagnosis, DISM/SFC
  repair or separately confirmed component cleanup. Opening the page starts no
  servicing. Pending restarts block repair/cleanup; the app never restarts Windows.
  Cancellation stops the worker and its child processes, retaining completed
  results. See the [Windows servicing guide](src/WinOptimizer.Infrastructure/Features/Management/README.md)
  for repair sources, ResetBase consequences and cancellation.

Battery support depends on hardware, firmware and drivers. System-variable, hosts
and firmware writes require the applicable Windows/BIOS permissions and are blocked
in Test mode and configuration review.

### Prepare Windows installations and restore configurations

**Advanced Tools** provides:

- **WIMUtil:** customize Windows installation media, add drivers, and generate an
  answer file from current selections or select a local XML file.
- **Autounattend generator:** build a Windows unattended-installation XML file
  from selected settings.
- **System information:** collect a dated snapshot of hardware, Windows and installed
  drivers. Later visits reuse it until **Refresh**; unavailable data is identified,
  and cancellation retains the prior snapshot. Driver signature metadata is separate
  from Drivers-page WHQL verification.
- **Restore:** import/export configurations, apply managed Windows defaults after
  confirmation, and create, list or delete Windows restore points. Test mode allows
  listing and configuration export while blocking system changes.

## Test mode, backups and change history

Use **Test mode** in the title bar to prepare a temporary draft of current compatible
settings. Edits stay in the draft. Export typed values and app selections to a
`.win-optimizer` file, or exit with **Export config**, **Apply changes** or **Discard**.
Closing the exit dialog discards the draft; cancelled or failed exports retain it.
Apply runs only edited settings through the normal configuration workflow.
System-changing actions are guarded during the session; preferences and read-only
Cleaner scans remain available.

**Logs and history** groups saved sessions and change receipts by topic, newest first.
Expand topics, search activity or filter changes, diagnostics and problems. Setting
changes show Before/After values, and technical details retain exceptions and record
provenance. **Load older activity** continues through saved records. **Clear app logs**
confirms and clears diagnostics while retaining change history.

Backup and restore options depend on the tool. Cleaner provides file/value and
whole-hive backup options, app-residue cleanup requires a verified backup, and the
Restore tools manage Windows restore points. Review the relevant operation's results
and recovery options before proceeding.

## Preferences and app updates

**Settings** offers searchable categories with automatic saves. Adjust language,
theme, app color and expanded/compact navigation. **Restore configuration** resets
the open category; **Reset all settings** resets all preferences exposed by the page.

**Settings → Info** includes manual update checks and independent automatic app and
removal-script update switches, both enabled by default. Automatic checks start
after a 30-second delay and run at most daily across restarts; manual checks are
independent of those switches.

Application data lives under `%ProgramData%\win-optimizer`, shared across
installations. Account-dependent data uses `Users\<SID>` folders. See the
[storage reference](ARCHITECTURE.md#storage) for paths, permissions and retention.

Uninstallation keeps this store by default so reinstalling retains preferences,
backups and history. Full cleanup removes the entire store, including cached tools
and scripts; it does not undo Windows changes. Restore settings that depend on
these scripts before choosing full cleanup, and close the app first. Administrator
access may be required to remove protected data. If cleanup fails, uninstall stops
and reports the remaining-data location so you can retry or keep it.

Silent uninstallation also keeps data by default. Administrators can explicitly
use `unins000.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DATA=remove` for full
cleanup, or `/DATA=keep` to retain data. An invalid data option aborts uninstallation.

</details>

<details>
<summary><strong>Developer guide — build, tests and architecture</strong></summary>

## Development

Use Windows, the .NET SDK selected by [global.json](global.json), Visual Studio
2026 / Build Tools with MSBuild 18+, the .NET desktop and C++ desktop workloads,
and the Windows SDK. Open [win-optimizer.sln](win-optimizer.sln) in Visual Studio.

```powershell
./build.ps1
./build.ps1 -Configuration Debug
./build.ps1 -BuildRoot ./build/isolated
```

The default is Release/x64 at `build/win-optimizer.exe`, with logs in
`build/logs/build.log`. `-Clean` rebuilds; `-MSBuildPath` selects MSBuild explicitly.
`-BuildRoot` redirects generated output together, useful while another build is running.
`Version.props` is the single source for the application's `YY.MM.DD` version,
defaulting to the build machine's local date. `./build.ps1 -Version 26.01.05`
selects an explicit release date. Visual Studio uses the same version file.
Product/file metadata uses `YY.MM.DD`; application update tags use `vYY.MM.DD`.

Test commands, focused suites and isolation requirements are in the
[test guide](tests/README.md).

Packaging uses `./extras/build-and-package.ps1` with Inno Setup 6 and a
`YY.MM.DD` version (`-Version`, defaulting through `Version.props` to the current date).
It resolves the version once for the application and installer, builds Release,
runs checks and produces an installer under
`build/installers/`. See [PIPELINE.md](PIPELINE.md#build-and-packaging) for ordering.
Use `-SkipSigning` to create an unsigned package without signing prompts, and
`-InnoCompiler <path-to-ISCC.exe>` for a portable Inno Setup installation. Packaging
uses the clean MSBuild payload under `build/publish/`, including the application's
`win-optimizer.pri` compiled UI resources. Requested signing failures
stop packaging.

The **Build Windows installer** GitHub Actions workflow runs on pushes/pull requests
to `main` and through **Actions → Build Windows installer → Run workflow**. Its optional
version accepts `YY.MM.DD`, defaulting to the build date. It runs the packaging script,
main suites, installer checks, UI compilation and portable-payload validation,
including the required application resource index.
Installer, published app and installed app must have matching product/file versions,
including numeric Windows metadata, before upload. Download the unsigned installer
from **Artifacts → win-optimizer-windows-x64-vYY.MM.DD** after success.
Installers are retained for 30 days; test results/build logs for 14 days, including failures.
Validation artifacts use `win-optimizer-validation-vYY.MM.DD` (or `vunresolved`
if version resolution fails).

The **Release** workflow runs when a tag matching `vYY.MM.DD` is pushed. It reuses
the installer workflow, validates the calendar date and builds with that exact version.
After all checks succeed, it publishes a stable GitHub Release with generated notes
and the unsigned `win-optimizer.Installer.exe` asset used by installation and in-app updates.
For example, after committing the desired sources and workflows:

```powershell
git tag v26.10.21
git push origin v26.10.21
```

[ARCHITECTURE.md](ARCHITECTURE.md) describes ownership and storage;
[PIPELINE.md](PIPELINE.md) describes execution, cancellation and recovery.
[Application artwork](src/WinOptimizer.UI/Assets/ICON-ATTRIBUTION.md)
and [sponsor data instructions](sponsors/README.md) cover those assets.

The Custom icon pack uses original vector drawings. Edit their authoring definitions
in `extras/generate-icons.py`, then run `python extras/generate-icons.py` to update
the resources and name lookup. `python extras/generate-icons.py --check` verifies
the generated files; generation uses Python's standard library.

</details>

## Support and feedback

Found a problem or have a feature idea? Use the
[GitHub issue tracker](https://github.com/sh1zen/win-optimizer/issues).

To support development, open the heart icon in the title bar or **Settings → Info →
Support win-optimizer**, then choose **Buy me a coffee**. You can also
[donate through PayPal](https://www.paypal.com/donate/?hosted_button_id=8G8VR4APG9JRU).

win-optimizer is an independent project, unaffiliated with Microsoft. Windows is
a registered trademark of Microsoft Corporation.

## License

Except where otherwise stated (see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)),
the content of this repository is provided under the
[PolyForm Shield 1.0.0](https://polyformproject.org/licenses/shield/1.0.0) license by
Andrea Frolli. See [LICENSE.txt](LICENSE.txt) for the full terms.
