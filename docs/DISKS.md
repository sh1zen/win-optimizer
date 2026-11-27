# Disks

Open **Disks** in the sidebar and select a tool card. Use the app's **Back**
button to return to the overview. Actions that modify
files, permissions, or devices are available in **Normal** mode.

| Operation | How to use it |
| --- | --- |
| **Unlock and force delete** | Select one file. Use **Find locking processes** to see names and PIDs, **Unlock file** to close the processes holding it open, or **Force delete** to permanently remove the file and close its lockers if needed. |
| **Repair permissions** | Select a file or folder and confirm **Reset inherited permissions**. Windows replaces the selected item's custom access rules with the defaults inherited from its parent. |
| **Previous versions** | Select a file or folder, or enter its original path if it is missing. Find a version in an existing Windows shadow copy, select it, and choose a folder for the restored copy. The restored item keeps its name. Choose a destination where that name is available. |
| **Verify SHA-256** | Select a file or folder. Paste a 64-character digest to compare, or leave the digest field empty to calculate one. A folder digest includes relative names, empty folders, and file contents. Compare it with a folder digest from this tool. |
| **Safely eject USB** | Select USB storage with an assigned drive letter and eject it. Windows attempts to eject the physical device and all its volumes. If a process vetoes the request, the app shows its name and PID when Windows supplies them. |
| **Recover deleted files** | Select two different local volumes, choose a scan mode, and optionally enter a filename, path relative to the source, or wildcard such as `*.jpg`. Follow the prompts in Windows File Recovery, then review the recovery folders on the destination volume. |
| **Analyze and defragment** | Choose a fixed volume and analyze it or start optimization. Windows selects the operation for the storage type, such as defragmentation on a hard disk or retrim on an SSD. |

## Recover deleted files

Install Microsoft Windows File Recovery for the account running WinOptimizer.
Select **Regular** for recently deleted files on NTFS. Use **Extensive** for
older deletions or other supported file systems. Choose another drive to save
the results. Recoverability depends on whether the original data still exists
on the source. [Microsoft Windows File Recovery instructions](https://support.microsoft.com/en-us/windows/experience/backup-recovery/windows-file-recovery)
