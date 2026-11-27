# Get started with win-optimizer

## Install win-optimizer

Check the [system requirements](../README.md#system-requirements). Then use the
[Windows x64 installer](../README.md#download-win-optimizer).

| Installation type | Windows integration |
| --- | --- |
| Regular | Setup registers the app with Windows. You can use shortcuts, file associations, and the Windows uninstall entry. |
| Portable | Setup puts the app in a folder that you can move. Use Setup to update the files in that folder. |

To change the installation type, select a different folder or remove the
existing installation first. During updates and uninstallation, Setup manages
only the files it installed.

## Use the app

The startup screen shows initialization progress. The app then restores your
language, theme, window position, sidebar, and app-list layout.

**Dashboard** opens at startup. It shows your PC and Windows details, graphics
driver versions, CPU core count and maximum clock, live CPU and memory usage,
and capacity and file system details for the Windows drive. Select **Copy** to
copy the device summary, or choose a quick action to open the corresponding tool.

Use **Search the program** in the title bar to find pages, tools and settings
by name or description in any available language. Results appear in the interface
language over the current page. Select a result or press **Enter** from the search
field to open the first result. Press **Esc** or select **Close** to return to the page.

1. Select a section in the sidebar.
2. Select a tool on the overview page.
3. Examine the current values and available options.
4. Select the changes or scan results.
5. Confirm the operation.
6. Follow progress on the page or open the task icon in the title bar.

Use the search suggestions in **Optimize** and **Customize** to find settings.
Select **ⓘ** or press **Ctrl+D** to examine registry values, scripts, and
dependencies for a setting. Select **Close**, or press **Esc** or **Ctrl+D** to
close the details.

Use the **Back** and **Forward** arrows or **Alt+Left** and **Alt+Right** to move
between pages. The same controls work in **Settings**. The app inventory has
its own progress indicator.

Use [Test mode](CONFIGURATION-RECOVERY.md#test-mode) to prepare supported settings
before you apply them. Use [logs and history](CONFIGURATION-RECOVERY.md#logs-and-history)
to examine the results. The task icon opens progress, terminal output, and
cancellation when the operation supports it.
When a cancelled scan stops, a notification appears and the app returns from
the scan review to its starting page.

If you close the app during an operation, the app asks for confirmation.
Confirmed exits and update restarts wait for operation cleanup and pending saves.
If you cancel the exit, monitoring continues.

## Measure PC performance

Open **Dashboard → Bench**. Review the CPU, RAM, graphics, and Windows details.
Select **Run** on an individual test or **Run all tests**. Select the fixed drive
for disk writes and the graphics adapter for the GPU test. Use **Cancel** to stop
a run and keep its finished results.

| Test | Measurement |
| --- | --- |
| CPU clock | Average CPU frequency reported by Windows during sampling, in GHz |
| CPU throughput | Synthetic double-precision arithmetic throughput, in GFLOPS |
| Memory copy | Sequential in-memory copy throughput, in GB/s |
| Disk write | Sequential write-through throughput, in MB/s |
| GPU throughput | Synthetic single-precision compute throughput on the selected GPU, in GFLOPS |

The disk test needs at least 64 MiB of free space and write access to the
selected drive. It creates a 64 MiB test file at the root of the drive and
Windows removes it when the file closes. Test results reflect conditions during
the run, including the power plan and other running programs.

The GPU test requires a graphics card and Windows driver that support Direct3D
feature level 11_0 or higher. It uses Direct3D components supplied with Windows
and measures a compute shader using GPU timestamps.

## Change app preferences

**Settings** opens **General**, where you can change the language and appearance,
check for updates, and find version and support information. Use the category
list or search to find other preferences. In a narrow window, select
**Choose a settings category** to open the list. The app saves changes automatically.

The main window uses a solid background by default. In **Settings → General**,
turn on **Blurred window background** to use a 98% opaque tint. If Windows acrylic
is unavailable, the window uses a solid background.

To reset a category, select the reset icon in its header. Use **Reset all settings**
to reset all preferences on the page. If the app cannot read the preferences file,
it stops incremental saves to protect the existing settings.

## Start win-optimizer at sign-in

Open **Settings → General → Info** and turn on
**Start win-optimizer as administrator**. This requires your own administrator
account and a protected installation under Program Files. Windows opens
win-optimizer at sign-in without another UAC prompt. Turn off the switch to
remove the sign-in task.

## Update the app

The update icon shows the installed version, the latest verified release, and
the check status. Select **Check for Updates** from that icon, or select
**Current Version** just above the links in **Settings → General → Info**.
Manual checks show the result in a toast inside the app. The update menu stays
open and refreshes its version and status. Update toasts provide the actions to
download an available update and relaunch once it is ready.

The automatic app update and removal-script checks have separate switches under
**Settings → General → Updates**.
The app enables both by default. Automatic checks start after 30 seconds and
run at most once per day across app restarts. App updates use the latest stable
release. The app verifies the installer against the GitHub release's SHA-256
digest before offering to restart. A missing or mismatched digest stops the
update. The removal-script check updates the shared sign-in script and keeps
its saved selections. You can check for app updates manually with either switch
off.

## Remove the app and its data

Installations share the data in `%ProgramData%\win-optimizer`. The app stores
account data in `Users\<SID>` subfolders. See the
[storage paths and permissions](../ARCHITECTURE.md#storage).

The uninstaller keeps the data store by default. A later installation can use
your preferences, backups, and history.

Full cleanup has these effects.

- It removes the shared data store, including cached tools and scripts.
- It removes this installation's usage, compatibility, and shortcut history
  from registered Windows accounts.
- It removes matching startup tasks, empty app folders in Task Scheduler, and
  the app's Prefetch files.

Full cleanup leaves other applications' entries and tasks in place.
Windows changes that you made in the app stay in effect. Restore settings that
use the removal scripts before you remove the shared data. Close the app before
you start full cleanup. Use an administrator account to remove protected data.

If cleanup fails, the uninstaller stops and shows the path of the remaining
data. You can then retry or keep the data.

Silent uninstallation also keeps the data by default. To request full cleanup,
run the following command as an administrator.

```powershell
./unins000.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DATA=remove
```

Use `/DATA=keep` to keep the data. The uninstaller stops if you specify an
invalid data option.
