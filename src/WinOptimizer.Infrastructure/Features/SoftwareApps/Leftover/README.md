# Leftover scan

Software & Apps → **Leftover** reviews stale desktop-app registrations and
unreferenced installer caches and installation-folder remnants. Selecting the tab starts its first scan;
**Refresh** scans again. Results include path, type, size and an eligibility reason.
Every listed result is eligible for selection. Notices identify incomplete coverage.
The selection summary includes selected items hidden by search. Its size totals
cover files and folders; registry entries contribute to the count.

## Scope and evidence

- Uninstall registrations: machine and all registered Windows accounts, both views.
  A stale entry needs an explicitly missing application executable, plus a missing
  uninstaller or verified absence of its MSI product. A missing installation
  directory permits registration cleanup; an existing directory requires the
  installation-folder checks below.
  Missing metadata and access errors are not evidence of absence.
- Windows Installer: only top-level `.msi`/`.msp` files in `%WINDIR%\Installer`.
  Internal subdirectories and executable resources remain outside this scan.
- Package Cache directories under standard application roots: `.msi`, `.msp` and
  `.exe` files. Registered bundle directories, uninstall/repair paths and MSI source
  directories protect their contents.
- Installation folders: stale registrations' explicit installation paths beneath
  Program Files, Program Files (x86), ProgramData or the owning account's local/roaming
  AppData. Trees must have no executable/library/script/installer payload, no active
  installation or repair reference, and no modifications or file access within 30 days.
  Complete ownership coverage is required. Shared roots, links, overlapping candidates,
  unreadable trees and folders exceeding 10,000 entries or 16 levels are excluded.
  Folders without stale registration evidence are excluded because an unregistered
  or portable application can still be active.

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
They do not trigger repairs. An unreadable account or incomplete reference inventory
excludes cache files and application folders. Installer activity, a pending restart,
or modification/access within 30 days also excludes those results. Links and junctions are not followed.
Ownership references resolve existing path aliases before comparison. Network repair
sources that cannot be reconciled locally leave installer files protected.

## Cleanup and recovery

**Delete selected** requires normal mode and confirmation. Cleanup accepts only IDs
issued by the current scan, verifies fresh ownership and unchanged evidence, and
creates an authenticated backup before any deletion. Folder selections include
their captured files, empty directories and stale registrations. Files are checked against
their backup hashes; registry cleanup removes captured values and empty keys.
An additional inventory check after backup protects newly required installers.

Use **Uninstall cleanup backups** to restore a cleanup. Existing different contents
are preserved. A partial failure or cancellation can leave a partially cleaned
registration; its removed resources remain available in the same recovery backup.
