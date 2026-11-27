# Changelog

## [Unreleased]

## [v26.11.27] - 2026-11-27

### Added

- Open the Dashboard at startup with hardware details, live CPU and memory usage, system drive capacity, and quick actions.
- Search pages, tools, and settings in every available language, with full-page results over a blurred background.
- Open Bench from the Dashboard to inspect hardware and measure CPU, GPU, memory, and disk performance.
- Open dedicated tools in Disks to unlock files, repair permissions, restore previous versions, verify SHA-256, safely eject USB storage, recover deleted files, and optimize drives.

### Changed

- Reorder the sidebar and place Extension above Advanced Tools.
- Open Windows diagnosis and repair from Repair in the sidebar and view requirements and logs from the Info button.
- Use the Quick status switch beside Diagnose to select a quick health check.
- Show Repair progress inline and present the outcome, affected components and next action in one report card.
- Show verified compatible driver updates in a single candidate list.
- Show shared task progress and controls on demand from the title bar.
- Show package extraction and INF verification in the Drivers scan progress card.
- Configure DNS directly in the Internet page’s Network card.
- Adjust TCP auto-tuning and RSS individually in Network settings.

### Fixed

- Keep Diagnose as the main action when returning to the Repair overview.
- Show a toast and return to the starting page when a scan is interrupted.
- Show update results as toasts and keep the version menu open during checks.
- Match the configuration import dialog to the app theme and clarify the selected source.
- Align the Cleaner confirmation buttons and show details for the selected operations.
- Size the terminal output popup to its content and keep long logs scrollable.
- Apply shared popup styling to Drivers, Autoruns, and Security.
- Localize the Disks page in every supported language.
- Verify eligible NVIDIA driver packages after license review, including large installers.
- Keep lengthy driver scans running until they finish or the user cancels.
- Compare the INF version that applies to each device and recognize trusted Authenticode signatures.
- Resolve compatible Microsoft Catalog offers for the running Windows version.
- Retry catalog access after temporary blocks and report package progress and verification limits.


## [v26.11.25] - 2026-11-25

### Changed

- Offer the soonest supported charging restart and keep valid resume selections when changing the charge limit.

### Fixed

- Report rejected Acer battery settings and warn when charging continues above the reported limit.
- Exit win-optimizer when the main window closes while SysMonitor and Notch run in a separate desktop companion.
- Complete missing UI translations and clarify battery charge limits.
- Include application libraries and desktop companions in signed installer builds.

### Security

- Verify downloaded updates against the SHA-256 digest published by GitHub.
- Reject signed installer builds when signature verification fails.
- Keep the Windows installer free of Mono's Unix hardware helpers.


## [v26.11.24] - 2026-11-24

### Added

- Measure and compare internet speeds, adjust TCP auto-tuning and RSS, and limit Windows background downloads on the Internet page.
- Add TimeMachine with a daily change timeline, settings snapshots, selective restore for supported changes, and configurable retention.
- Add Notch as an attached or floating desktop widget with a choice of display, customizable Overview tiles, media scrubbing, a focus timer, and a calendar.
- Pin and reorder Notch readings, including system metrics, game FPS, audio activity, and PC and Bluetooth battery levels. Only the clock is pinned by default.
- Show system charts, live network rates, and daily network usage in Notch Sys info.
- Pin apps and files in Notch Quick access and send files to a linked phone through Windows Share.
- Show local weather in Notch with hourly and seven-day forecasts.
- Adjust volume, brightness, Wi-Fi, Bluetooth, Night light, Do not disturb, and power mode from Notch Quick Settings where supported.
- Show Notch alerts for battery, Bluetooth, USB, audio devices, volume, and brightness changes.
- Preview files, folders, and shortcuts with Space in File Explorer or on the desktop. Open items from the preview and inspect folder contents, totals, and size on disk.
- Manage experimental Windows features with importable compatibility profiles, technical details, restart status, and undo.
- Schedule registry compaction for the next restart with verified backups, automatic rollback during maintenance, and status reporting in Cleaner.

### Changed

- Show progress, live details, and cancellation during Cleaner scans, and open cleanup results by category.
- Show Security scan progress and coverage, deep scan options, and results by category with expandable evidence and navigation to unavailable checks.
- Check multiple official driver catalogs, save source and NVIDIA channel preferences, and show progress and coverage by source.
- Compare driver versions, verify or download selected packages, and search disconnected devices.
- Start Windows repair from the health result and access component cleanup from the Windows status page.
- Configure elevated app startup in Autorun management and manage sign-in startup for SysMonitor, Notch, and Preview in Extension.
- Reorganize Software & Apps with responsive cards, a selection toolbar, and individual Windows app and feature selection in each layout.
- Use registered desktop uninstallers and Windows package servicing with individual removal results.
- Refresh the title bar and sidebar, add brief page fades, and show installed and latest versions with update status in the title bar.
- Add optional background blur for the main window and adjustable blur opacity for Preview.
- Consolidate the Environment Variables and Hosts controls into toolbars and add a reset action to Settings category headers.
- Leave the high-contrast keyboard shortcut under Windows control.
- Update Cleaner, Connection, and Security translations.
- Load connection history independently and reduce background work on the Connection page.
- Resume charging on compatible Dell laptops at a fixed level or 10 or 20 percentage points below the charge limit.
- Clarify SysMonitor readings and show expanded reading choices as labeled icons.
- Report when another build is using the output directory.
- Include the Apache 2.0 license and updated third-party notices in the app package.

### Fixed

- Preserve verified app removal results on cancellation, keep saved sign-in plans during one-time use, and confirm the scheduled task before reporting deferred removal.
- Honor configuration review selections and cancellation, keep Explorer open during selection-only imports, and use the current theme for wallpaper-only imports.
- Report unsuccessful configuration and quick actions, and preserve unrelated registry policies when applying Windows Defaults.
- Keep confirmed power settings and require all necessary changes to succeed before reporting a visual effects preset as applied.
- Show recommended and default badges for battery power settings on devices with a battery.
- Keep confirmed TimeMachine restore results after cancellation and readable snapshots accessible when other recovery data is damaged.
- Verify driver backups during restore review and report restart-pending removals separately from completed removals.
- Protect registry branches when Cleaner runs under another administrator account and retain access to Cleaner if offline maintenance reporting is incomplete.
- Reject incomplete or invalid update downloads before marking an installer ready.
- Apply first-logon customizations correctly in generated answer files, preserve imported answer-file encodings, validate setup media, and report failed ISO dismounts.


## [v26.11.18] - 2026-11-18

- Use a brief blur-to-sharp animation when navigating between pages.
- Refresh the app with rounded cards and controls, segmented tabs and a brighter blue accent.
- Fix hover flashes on cards and controls.
- Skip unrelated driver-package size checks before removal.
- Bound Cleaner file inspection memory while scanning large folders.
- Reduce blocking file I/O during verified cleanup backups and restores.
- Reduce repeated work when checking uninstall residues.
- Speed up scheduled-task settings discovery.
- Reduce repeated preference reads when opening power settings.
- Reduce startup storage writes, preference reads and catalogue preparation time.
- Speed up installed app inventory, metadata matching, icons, searches and card layout.
- Save space in the Software & Apps toolbar and use an eye button to show hidden or inactive apps.
- Reuse a DISM session when checking capabilities and optional features.
- Bound WinGet activation time before falling back to the command line.
- Download driver offers together while preserving individual results.
- Speed up Process Monitor list updates and endpoint sampling.
- Speed up event channel loading, log rotation and activity journal paging.
- Reduce widget redraw work and load times for system information, battery telemetry and network history.
- Speed up Leftover cache checks.
- Speed up Autoruns and context menu inventories.
- Reduce repeated filesystem work when saving preferences and checking security settings.
- Avoid tooltip reads when exporting settings or verifying privacy settings.
- Load AppX icon scopes on demand and read uncached logos together.
- Verify manual Explorer restarts immediately when the process is ready.
- Verify completed WIM conversions and newly visible restore points sooner.
- Reduce directory reads during ISO extraction and creation.
- Speed up generation of unattended Windows configurations.
- Improve optional driver discovery, bundled-package verification and upgrade comparisons; show the active WHQL filter beside results.
- Simplify Optimize and Customize category cards to custom and configuration-review indicators.
- Keep the loading screen centered and steady while startup progress changes.
- Open PayPal directly from the title bar and Settings support actions.
- Automatically refresh app inventories after uninstallation finishes, including partial or cancelled operations.
- Use the .NET 11 SDK specified in `global.json` for development and CI.
- Align Windows runtime references and initialization across the application and native test hosts.
- Clarify that win-optimizer is licensed under Apache 2.0.


## [v26.11.04] - 2026-11-04

- Protect source ISOs contained in the extraction workspace and preserve preferences after read failures.
- Block power-plan deletion in Test mode and keep backup recovery from replacing active tasks.
- Confirm closing before cleanup; wait for operations and saves before closing or installing updates.
- Invalidate built ISOs after media edits and reject obsolete image-format detections.
- Report failed update checks as errors and confirm disposal of environment-variable drafts.
- Honor restore-point cancellation before preparation and between native operations.
- Preserve existing ISO files when replacement creation fails or is cancelled.
- Keep late parallel-uninstall progress from reviving old indicators or adding output to newer tasks.
- Invalidate WIM extraction, XML and driver results when media changes or extraction
  fails; preserve other tasks when driver or conversion prompts are cancelled.
- Keep delayed progress and queued interface updates from overwriting newer task status.
- Retry failed initial configuration backups on the next launch.
- Keep ISO creation and tool downloads from cancelling or completing other tasks;
  reset ISO completion after failed attempts or path changes.
- Split Cleaner review into scanned categories with numerical summaries on the left
  and the selected category's actionable findings on the right; preserve selections
  between categories.
- Count registry-value data in Cleaner size totals and show small results in bytes or KB.
- Show numerical Cleaner reports for every selected scan category, with removed items,
  errors, estimated file space freed and separate registry data totals. Retain partial
  results, continue past protected registry entries and offer rescanning; keep technical
  diagnostics collapsed.
- Block and minimize the app during native uninstallation, then offer an optional
  leftover scan with separate file and registry selection steps.


## [v26.10.27] - 2026-10-27

- Speed up MSBuild/NuGet with static graphs, `-NoRestore` and Release-output reuse
  for packaging's UI tests. Isolate packaging temporary files, clean up after failures
  and preserve literal signing paths.
- Export matching Release symbol ZIPs separately from runtime deployments, preserve
  Debug builds and publish release notes from the matching dated changelog section.
- Restore window placement before loading, merge it atomically with preferences and
  keep title bars reachable. Apply saved language, theme, sidebar and app-list layout
  at startup with consistent title-bar geometry.
- Adapt Software & Apps cards and toolbars to narrow windows; square adjoining
  Back/Forward button corners.
- Match desktop uninstallers unambiguously, run them interactively and confirm automatic
  removal. Update cached lists only after verified removal; reject failed or cancelled
  inventories as proof. Correct WinGet failure codes, idle deadlines and unlimited timeouts.
- Scope AppX inventory, external-app removal, portable-app paths and Store installation
  to the interactive account under alternate-account elevation. Review owned Start Menu resources.
- Speed up app checks with shared WinGet/AppX discovery, concurrent providers, cached
  registry patterns/package sizes and batched list updates.
- Find verified installation remnants, empty old installation folders and per-user
  installer caches while protecting active apps, shared data and required installers.
- Fix cache references hiding unrelated leftovers; report incomplete scan areas and
  distinguish active installations, queued operations and app execution aliases.
- Preserve malformed uninstall registrations and their recorded installation folders.
- Speed up Cleaner inspection and verified backups with bounded concurrency, reusable
  buffers, long-path support and fewer UI updates; process overlapping cache selections once.
- Preserve all WIM/ESD editions, setup commands and complete driver packages during media
  customization; support apostrophes in paths and cancel only owned processes.
- Report PowerShell, registry-import, configuration and installer failures accurately;
  await interactive installers and locate PowerShell in the configured Windows system directory.
- Handle skipped/repeated local midnights in connection history.
- Clean usage, compatibility and shortcut traces, startup tasks, empty scheduler folders
  and Prefetch during full app uninstallation.
- Consolidate regression tests, remove unused UI/test helpers and simplify documentation.


## [v26.10.21] - 2026-10-21

- Baseline release for subsequent changes.
