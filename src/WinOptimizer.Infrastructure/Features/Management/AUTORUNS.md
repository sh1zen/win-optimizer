# Autorun management

Open **Control → Autorun management** to load ordinary startup sources.
Refresh replaces its dated snapshot. Search sits on the left, with category and
state filters on the right of the same row. Search covers names, commands, locations,
publishers, signers and accounts; filters work locally. The advanced Microsoft
filter shares the row on wide windows and wraps below it on narrow windows.
Selecting an entry exposes its complete registration and command for copying.
Entries are grouped under **User accounts**, **All users**, **System** and
**Unspecified account**, with counts for the current filters. Reported machine-wide
registrations are shared by all users; built-in system and service accounts have
their own group. Entries without account information use an explicit registry hive
when available, otherwise they stay unspecified.

## Ordinary startup controls

| Source | Coverage | State changes |
| --- | --- | --- |
| Run / RunOnce | Current user and machine, native and machine 32-bit registry views | Preserve the original command and registry type in application-data JSON, then remove the active value. Enable restores it. |
| Startup folders | Current user and all users, including saved disabled files | Preserve the file and its source metadata in application data; enable moves it back. |
| Windows packaged apps | Declared startup tasks of current-account AppX packages | Open **Windows startup settings**. Other applications cannot enable arbitrary packaged startup tasks through their own StartupTask API. |
| Scheduled tasks | Accessible tasks with boot or logon triggers, including hidden tasks | Change the native task's Enabled property. Tasks under `\Microsoft\` are inspection only. Other triggers on the same task share its enabled state. |

Windows StartupApproved state is read as evidence, using Explorer's native registry
view; these undocumented values are never written. Windows-controlled disabled or
unrecognized states are delegated to Windows settings. Packaged task registration
without readable current state is shown as **State unavailable**, never inferred
from the manifest's requested default. Publisher/author metadata in the ordinary
view is not signature verification.

Select one entry and choose Enable or Disable, then review its name, location and
command in the confirmation. Current processes are not stopped; changes affect
future starts. Writes require normal application mode. Machine-wide resources and
tasks may require administrator permissions. Under elevation with another user's
credentials, ordinary controls report the account mismatch instead of changing
that administrator's startup profile.

Each write checks the scanned source again. Changed registry values/types,
changed task definitions/state, changed file content, redirected filesystem paths,
and conflicting saved/live entries are rejected. Saved data is verified before
removing its active registry value; files are moved without overwriting targets.
Partial failures are reported and retained data remains visible on Refresh.
Uninstalling win-optimizer does not automatically restore disabled registrations.
Keep the saved application-data records and payloads until their entries have been re-enabled.
Machine entries use `%ProgramData%\win-optimizer\State\Autoruns`; personal entries
use `Users\<SID>\State\Autoruns` under that root.

## Advanced loading points

Enable **Advanced mode**, read the Microsoft license link and explicitly check the
license/download consent to start the advanced scan. Later mode changes automatically
load the selected sources using that session's consent; **Scan all loading points**
refreshes advanced results. Advanced scanning waits for consent. On first request,
the application fetches
`https://download.sysinternals.com/files/Autoruns.zip`, extracts only
`autorunsc64.exe`, verifies a valid Microsoft Authenticode signature and caches it
under `%ProgramData%\win-optimizer\Tools\autoruns`. The executable is verified
again and held open against replacement for each run. Remove that cached file to
fetch the latest Microsoft version on the next requested scan.

The command is `autorunsc64.exe -accepteula -nobanner -a * -c -s -t`, adding the
literal `*` profile argument when elevated. All known categories are selected,
including logon, Explorer, Internet Explorer, scheduled tasks, services, drivers,
Winlogon, AppInit, image hijacks, boot execute, known DLLs, WMI, Winsock/network
providers, codecs, printer monitors and LSA providers. The result includes Microsoft
entries and is not restricted to unsigned files. Administrator permissions are
needed for protected sources and other accessible user profiles. Access errors
and account coverage remain available by expanding the scan count.

This is an inventory of configured loading points, including disabled registrations;
it is not proof that each image executed, a live process list, or a guarantee about
unknown/unsupported persistence mechanisms. Advanced entries are inspection only;
use their owning Windows tools for system changes. The optional Microsoft filter
hides entries only when Autorunsc reports a verified Microsoft signer. It starts off.

No VirusTotal lookup or file upload is requested. Signature verification can contact
certificate services; first acquisition needs Internet access. The third-party
binary is not bundled, and no additional package dependency is added. Scan results
stay in memory and are not written to application logs or source files. Sysinternals
can maintain its own local licensing/state information.

## Execution and checks

Core owns the entries and dated inventory. `AutorunService` serializes scan/write
snapshots, authorizes known editable entries and reserves application mode for
writes. `AutorunPowerShellBackend` uses `ManagementPowerShellRunner` to materialize embedded scripts into exclusive
temporary files. An encoded loader hashes the bytes read from disk before executing
that same buffer, and request JSON is passed separately as base64 data. Temporary
scripts are deleted after the complete command. Native units finish before
cancellation is observed; no UI cancellation or forced termination is offered.
Shutdown drains in-flight startup writes through the existing utility hook.
`Sysinternals-Cache.ps1` owns the shared allowlisted acquisition, Microsoft signature
validation and executable locks for Autorunsc and the process monitor's Handle tool.

`AutorunCsvReader` validates the Autorunsc schema and handles quoted multiline CSV.
Unexpected output fails explicitly. Unknown state stays unknown.

Infrastructure, storage and native UI checks are listed in the
[test guide](../../../../tests/README.md). They use fake registry/OS boundaries,
isolated files and synthetic inventory. UI coverage includes consent loading,
search/filter alignment, both modes, EN/IT, themes and three widths;
previews go under `build/autoruns-preview`.

## Microsoft references

- [Autoruns categories and command-line options](https://learn.microsoft.com/en-us/sysinternals/downloads/autoruns/)
- [Sysinternals license](https://learn.microsoft.com/en-us/sysinternals/license-terms)
- [Run and RunOnce keys](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys)
- [Configure startup applications in Windows](https://support.microsoft.com/en-US/Windows/Experience/Startup-Boot/configure-startup-applications-in-windows)
- [StartupTask API and user control](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.startuptask)
