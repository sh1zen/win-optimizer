# Windows status and restore

Control exposes servicing for the running Windows installation. Opening the
page performs no checks or repairs. Every operation requires administrator rights
and normal application mode; Test mode and configuration review block execution.

## Choosing an operation

| Action | Ordered operations | Meaning |
| --- | --- | --- |
| Quick status | DISM CheckHealth | Reads corruption already flagged by servicing; it is not a new full scan. |
| Full diagnosis | DISM ScanHealth → AnalyzeComponentStore → SFC `/verifyonly` | Scans the store, reports its actual usage and verifies protected files without repairing them. |
| Analyze component store | DISM AnalyzeComponentStore | Reports Windows' component-store measurements and cleanup recommendation. Explorer's directory size includes hard links. |
| Repair image and system files | DISM RestoreHealth → SFC `/scannow` | Repairs the store first, then protected files. SFC starts only after a successful, healthy DISM result without a restart request. |
| Clean component store | DISM CheckHealth → AnalyzeComponentStore → StartComponentCleanup | Performs explicit component cleanup after a healthy quick check. |

Image health comes from the structured `ImageHealthState` returned by the built-in
DISM PowerShell module. Unknown values stay unknown. A successful SFC process exit
is not presented as proof that every protected file is healthy: inspect the
retained native SFC output and CBS log for that separate result.

Repair and cleanup require confirmation. ResetBase is an advanced cleanup option,
off by default, with its own consequence notice and unchecked acknowledgement.
It removes superseded component versions and prevents uninstalling existing update
packages. Future update packages retain their own uninstall behavior. Keep a suitable
system backup and verify Windows stability before choosing this option.

## Repair sources

An empty source uses configured servicing sources and Windows Update as permitted
by machine policy. LimitAccess prevents contacting Windows Update, including as a
fallback; configured local/network sources may still be used.

Custom sources are an existing Windows source directory or a `.wim` file with a
positive image index. WIM metadata is queried before RestoreHealth so an invalid
index fails before repair. The index must identify the appropriate edition.
Match the Windows release, architecture and required language components, and
keep source files updated to the cumulative update level the installation needs.
An older source can lack required files; source availability is ultimately checked
by Windows servicing. An ISO file must first be mounted outside this tool so its
Windows folder or WIM can be selected.

User paths are validated at the backend boundary and passed to PowerShell as JSON
data. Executable paths and servicing switches are fixed by the implementation.
The source and LimitAccess apply to repair, while ResetBase applies only to cleanup.

## Completion, cancellation and errors

The service serializes its workflows. Existing CBS/Windows Update restart flags
block mutating workflows, and a native restart request stops subsequent phases.
The tool never initiates a restart. Restart Windows when requested, then run a new
check or repair. A phase failure stops the workflow with its output and error code
or HRESULT, retaining earlier receipts.

Cancel stops the active owned DISM/PowerShell/SFC process tree and skips remaining
phases. The task and system-operation reservation remain active until the worker
exits and its output drains. The dated report retains completed phase receipts;
completed changes remain applied. No arbitrary short timeout is applied during
normal execution of these potentially long-running maintenance commands.

Windows owns the detailed logs:

- `%WINDIR%\Logs\DISM\dism.log`
- `%WINDIR%\Logs\CBS\CBS.log`

Missing repair content, including `0x800f081f`, calls for checking source compatibility
and Windows Update access. Other failures retain their native details rather than
being reported as a successful repair. A non-repairable DISM result requires a
separate recovery decision; this tool does not automatically reinstall Windows.

## Microsoft references

- [Repair a Windows image](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/repair-a-windows-image)
- [Repair-WindowsImage: structured health, source, LimitAccess and NoRestart](https://learn.microsoft.com/en-us/powershell/module/dism/repair-windowsimage)
- [Configure a Windows repair source](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/configure-a-windows-repair-source)
- [Component-store analysis and actual size](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/determine-the-actual-size-of-the-winsxs-folder)
- [Component cleanup and ResetBase consequences](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/clean-up-the-winsxs-folder)
- [Microsoft's DISM-before-SFC repair sequence](https://support.microsoft.com/en-us/windows/experience/backup-recovery/using-system-file-checker-in-windows)
- [SFC verification and repair switches](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/sfc)

## Validation

Infrastructure tests use fake servicing boundaries and isolated PowerShell workers
to verify cancellation of the real process runner and its child processes. The
presentation host runs the real view-model commands and page with simulated
reports; tests do not run DISM, SFC, native repairs or component cleanup.
Commands and domain filters are in the
[test guide](../../../../tests/README.md).
