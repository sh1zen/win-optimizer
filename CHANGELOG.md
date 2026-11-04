# Changelog

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
