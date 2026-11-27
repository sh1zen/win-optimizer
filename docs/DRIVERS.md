# Scan, download, and remove Windows drivers

## Check for driver updates

![Drivers scan results with sample unused packages and tabs for disconnected devices and driver candidates](images/drivers.jpg)

1. Open **Drivers**.
2. Select **Scan**. Follow device detection, source checks, and results preparation
   in the progress card. When prompted, review the NVIDIA license and choose
   whether to let the scan download and verify its packages. The card shows
   download progress, package extraction, and INF verification. The scan waits
   for source checks and package verification to finish. Select **View details**
   for live output or **Cancel scan** to stop at any time.
3. Select **View scan details** when the coverage banner appears to see what
   could be checked. Open **Sources** for individual catalog results and the
   reasons packages could not be verified.
4. Open **Driver candidates** to compare installed and available versions,
   release dates, certification, and package sizes.

**Driver candidates** shows newer drivers whose signed INF matches a connected
device and the running version of Windows. The scan compares the driver date
and version from the device's applicable INF install section with the installed
driver and selects the best verified update for each matching device and release channel.

The app enables **Only show WHQL-tested driver updates** by default in
**Settings → Drivers**. Use **Filter settings** to edit scan preferences from
the results page, then select **Scan again** to apply them.

Select **Download selected**
to save packages under `%ProgramData%\win-optimizer\Downloads\Drivers`.
Select **Open official support** if the publisher manages the download.
Examine any publisher license before you accept it.

Open the task icon in the title bar and select **Cancel** to stop a download. Scans
and downloads read driver information without changing device bindings. See
[official driver sources, package verification, and downloads](../src/WinOptimizer.Infrastructure/Features/Drivers/README.md).

## Remove unused packages or disconnected devices

1. Select unused packages or open **Disconnected devices** and use **Search
   devices** to find entries by name or instance ID. The table checkbox selects
   the results currently shown. Individual selections stay selected across
   searches and categories.
2. Select **Uninstall selected**.
3. Confirm removal.
4. Examine the results for failures and restart requirements.

Removal requires administrator permissions. The app checks the current
inventory again and protects bound, changed, inbox, and boot-critical packages.
Select a disconnected device only if you intend to remove its registration.
Windows can detect the device again when you connect it. To remove its
driver package, select the package separately.

Cancellation stops the queue between Windows removal operations. The results
show the operations that completed. See
[driver package recovery](CONFIGURATION-RECOVERY.md#driver-package-and-cleanup-recovery).
