# Leftover scan

Software & Apps → **Leftover** reviews stale desktop-app registrations and
unreferenced installer caches and installation-folder remnants. Selecting the tab starts its first scan;
**Refresh** scans again. Results include path, type, size and an eligibility reason.
Every listed result is eligible for selection. Notices identify incomplete coverage.
The selection summary includes selected items hidden by search. Its size totals
cover files and folders; registry entries contribute to the count.

## Scope and evidence

- Uninstall registrations: machine and all registered Windows accounts, both views.
  MSI entries require verified product absence in the complete native inventory.
  Other entries require a missing uninstaller owned by the installation directory
  and a missing application executable when one is recorded. A missing installation
  directory permits registration cleanup; an existing directory requires the
  installation-folder checks below. Optional icons and install-location fields are
  not prerequisites: explicit application/uninstaller paths can establish their common
  installation boundary. Missing cache uninstallers alone do not prove app removal.
  Missing metadata and access errors are not evidence of absence.
  Invalid registry value types are reported and keep the registration protected;
  readable ownership references continue to protect their files and folders.
- Windows Installer: only top-level `.msi`/`.msp` files in `%WINDIR%\Installer`.
  Internal subdirectories and executable resources remain outside this scan.
- Package Cache directories under standard application roots and each registered
  account's Local AppData: `.msi`, `.msp` and
  `.exe` files. Registered bundle directories, uninstall/repair paths and MSI source
  directories protect their contents.
- Registered installation remnants: inspect explicit paths beneath Program Files, Program Files (x86),
  ProgramData and every registered account's Local, Roaming and LocalLow AppData,
  including individual installations under Local\Programs. Account folders use their
  registered bindings, including offline profiles. Exact obsolete installation evidence
  is required; age, a matching folder name and absence of executable files do not establish ownership.
  Trees must have no executable/library/script/installer payload, no active
  installation or repair reference, and no modifications or file access within 30 days.
  Complete ownership coverage is required. Shared roots, links, overlapping candidates,
  unreadable trees and folders exceeding 10,000 entries or 16 levels are excluded.
  Folders containing data require this installation evidence, and an active registration
  of the same product in the applicable scope protects them. Shared Programs containers and Windows-managed/cache roots
  remain protected. Nested files and empty directories are captured with each selection.
- Empty installation folders: direct children of Program Files, Program Files (x86)
  and each account's Local\Programs can qualify without an uninstall record. They
  must contain no files or subfolders, be at least 30 days old, and have no known
  active reference to their path. Hidden, system, linked and reserved Windows/shared
  directories are excluded. A current installation at a different path protects its
  own directory; it does not claim an empty previous architecture's directory.
  Empty ProgramData and general AppData folders do not qualify under this rule.

Native MSI queries include all product contexts, all patch states and alternative
repair sources. Advertised products keep their source references; installed-only
cache/location properties and patch enumeration are queried for installed products.
If source enumeration reports bad configuration before returning any source, the
scan checks the exact product registration in both registry views. An existing
registration without a `SourceList` key has no registered alternative sources.
A missing registration, unreadable registry or existing damaged source list remains
incomplete. Patch-source errors also remain incomplete. This check only reads metadata.
Native failures identify the product, property and context when available.
They use the read-only [Windows Installer inventory APIs](https://learn.microsoft.com/en-us/windows/win32/msi/inventory-products-and-patches-)
and [source-list enumeration](https://learn.microsoft.com/en-us/windows/win32/api/msi/nf-msi-msisourcelistenumsourcesw).
They do not trigger repairs. An incomplete native inventory excludes Windows Installer
cache files. Unreadable account hives protect bundle caches and data-containing
application folders; verified empty installation leaves are inspected independently.
Native Windows Installer product/patch coverage remains authoritative for its managed cache.
Active MSI execution, installation or
servicing/update restart markers, or modification/access within 30 days also exclude
those results. MSI activity uses the [execution mutex](https://learn.microsoft.com/en-us/windows/win32/msi/-msiexecute-mutex).
Queued file renames/deletions protect their source and destination paths, descendant
cache files and overlapping application folders; unrelated leftovers remain eligible.
Both pending-file-operation values are read, including native and numbered path
prefixes. Empty queues are ignored; malformed or unreadable queues protect cleanup
and report the coverage error. Links and junctions are not followed.
Ownership references resolve existing path aliases before comparison. Network repair
sources that cannot be reconciled locally leave installer files protected.
Windows app execution aliases retain their local launcher paths as ownership references
without running the launcher. Resolution failures identify the path and Windows error code.
Uninstall/repair command arguments protect explicitly referenced files and containing
directories. Active command references also protect installation folders.
Exact command targets protect their files or named directory, without protecting
unrelated siblings elsewhere in the same cache.

## Recognition guidance

Folder age, similar names and absent executables are supporting signals. Ownership
evidence, current installations and shared dependencies determine eligibility.
Settings and user-created content are not automatically disposable app data.
Install-time traces and captured pre-uninstall identities provide stronger evidence
for targeted residue review than a standalone filesystem scan.

These distinctions follow [BCU's orphan-folder scanner](https://github.com/BCUninstaller/Bulk-Crap-Uninstaller/blob/master/source/UninstallTools/Junk/ProgramFilesOrphans.cs),
its [confidence evidence](https://github.com/BCUninstaller/Bulk-Crap-Uninstaller/blob/master/source/UninstallTools/Junk/Confidence/ConfidenceRecords.cs),
and [Revo's review workflow](https://www.revouninstaller.com/online-manual/uninstaller/).
Older version folders can be intentional: [Squirrel preserves current and previous releases](https://github.com/Squirrel/Squirrel.Windows/blob/develop/src/Squirrel/UpdateManager.ApplyReleases.cs).
Windows Installer cache ownership uses [native all-user product enumeration](https://learn.microsoft.com/en-us/windows/win32/api/msi/nf-msi-msienumproductsexw)
and patch/source references; [Microsoft documents why required cached packages must be retained](https://learn.microsoft.com/en-us/troubleshoot/windows-client/application-management/missing-windows-installer-cache).

## Cleanup and recovery

**Delete selected** requires normal mode and confirmation. Cleanup accepts only IDs
issued by the current scan, verifies fresh ownership and unchanged evidence, and
creates an authenticated backup before any deletion. Folder selections include
their captured files, empty directories and any stale registrations. Files are checked against
their backup hashes; registry cleanup removes captured values and empty keys.
An additional inventory check after backup protects newly required installers.

Use **Uninstall cleanup backups** to restore a cleanup. Existing different contents
are preserved. A partial failure or cancellation can leave a partially cleaned
registration; its removed resources remain available in the same recovery backup.
