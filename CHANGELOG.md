# Changelog

## [Unreleased]

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
