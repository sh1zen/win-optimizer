using System.Diagnostics.Eventing.Reader;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using WinOptimizer.Core.Features.Disks;

namespace WinOptimizer.Infrastructure.Features.Disks;

/// <summary>Maps mounted USB storage to PnP devices and reports the processes Windows records for a veto.</summary>
internal static class UsbDiskEjection
{
    private const string StorageNamespace = @"root\Microsoft\Windows\Storage";
    private const string VetoEvents = "*[System[Provider[@Name='Microsoft-Windows-Kernel-PnP'] and (EventID=225)]]";

    public static IReadOnlyList<UsbDisk> List(CancellationToken ct)
    {
        var usbIndices = Query(StorageNamespace, "SELECT Number FROM MSFT_Disk WHERE BusType = 7",
            row => (uint)row["Number"], ct).ToHashSet();
        if (usbIndices.Count == 0) return [];
        var volumes = Query(StorageNamespace, "SELECT DiskNumber, DriveLetter FROM MSFT_Partition",
            row => (Number: (uint)row["DiskNumber"], Letter: Convert.ToString(row["DriveLetter"])), ct)
            .Where(volume => volume.Letter is { Length: 1 } letter && char.IsAsciiLetter(letter[0]))
            .ToLookup(volume => volume.Number, volume => $"{char.ToUpperInvariant(volume.Letter![0])}:");
        var disks = Query(@"root\CIMV2", "SELECT Index, Model, PNPDeviceID FROM Win32_DiskDrive",
            row => (Index: (uint)row["Index"], Model: (string)row["Model"], PnpId: (string)row["PNPDeviceID"]), ct);
        return disks.Where(disk => usbIndices.Contains(disk.Index) && volumes.Contains(disk.Index))
            .Select(disk => (Id: UsbAncestor(disk.PnpId), disk.Model, Drives: volumes[disk.Index]))
            .Where(disk => disk.Id != null)
            .GroupBy(disk => disk.Id!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new UsbDisk(group.Key, group.First().Model, string.Join(", ", group.SelectMany(disk => disk.Drives).Distinct().Order())))
            .OrderBy(disk => disk.Drives).ToArray();
    }

    public static async Task<UsbEjectResult> EjectAsync(UsbDisk disk, CancellationToken ct)
    {
        if (!List(ct).Contains(disk) || CM_Locate_DevNode(out var device, disk.InstanceId, 0) != 0)
            throw new IOException("The selected USB device or its volumes changed. Refresh the list and try again.");
        ct.ThrowIfCancellationRequested();
        var started = DateTime.UtcNow;
        var name = new StringBuilder(260);
        uint code = CM_Request_Device_Eject(device, out var veto, name, name.Capacity, 0);
        if (code == 0) return new(true, [], null, 0, 0);

        IReadOnlyList<DiskProcess> blockers = [];
        if (code == 0x17 || veto != 0)
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                blockers = RecentBlockers(disk.InstanceId, started);
                if (blockers.Count > 0 || attempt == 4) break;
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
        }
        return new(false, blockers, name.Length == 0 ? null : name.ToString(), veto, code);
    }

    internal static DiskProcess? ReadBlocker(string xml, string instanceId)
    {
        var data = XElement.Parse(xml).Descendants().Where(element => element.Name.LocalName == "Data").ToArray();
        string? Field(string key) => data.FirstOrDefault(element => (string?)element.Attribute("Name") == key)?.Value;
        if (!string.Equals(Field("DeviceInstance"), instanceId, StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(Field("ProcessId"), out var pid) || pid <= 0 ||
            Field("ProcessName") is not { Length: > 0 } name)
            return null;
        return new(pid, Path.GetFileName(name));
    }

    private static IReadOnlyList<DiskProcess> RecentBlockers(string instanceId, DateTime started)
    {
        var blockers = new HashSet<DiskProcess>();
        try
        {
            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, VetoEvents) { ReverseDirection = true });
            while (reader.ReadEvent() is { } entry)
            {
                using (entry)
                {
                    if (entry.TimeCreated is not { } created || created.ToUniversalTime() < started) break;
                    if (ReadBlocker(entry.ToXml(), instanceId) is { } blocker) blockers.Add(blocker);
                }
            }
        }
        catch (EventLogException) { /* The native eject veto remains available if the System log cannot be read. */ }
        catch (UnauthorizedAccessException) { /* The native eject veto remains available without log access. */ }
        return blockers.OrderBy(blocker => blocker.Id).ToArray();
    }

    private static string? UsbAncestor(string? instance)
    {
        if (string.IsNullOrEmpty(instance) || CM_Locate_DevNode(out var device, instance, 0) != 0) return null;
        var id = new StringBuilder(200);
        while (CM_Get_Device_ID(device, id, id.Capacity, 0) == 0)
        {
            var current = id.ToString();
            var hardwareId = current.Split('\\', 3).ElementAtOrDefault(1);
            if (current.StartsWith(@"USB\VID_", StringComparison.OrdinalIgnoreCase) &&
                hardwareId?.Contains("&MI_", StringComparison.OrdinalIgnoreCase) == false)
                return current;
            if (CM_Get_Parent(out device, device, 0) != 0) break;
            id.Clear();
        }
        return null;
    }

    private static IEnumerable<T> Query<T>(string scope, string query, Func<ManagementObject, T> project, CancellationToken ct)
    {
        using var search = new ManagementObjectSearcher(scope, query);
        using var rows = search.Get();
        foreach (ManagementObject row in rows)
        {
            using (row)
            {
                ct.ThrowIfCancellationRequested();
                yield return project(row);
            }
        }
    }

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNode(out uint device, string instanceId, uint flags);
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint parent, uint device, uint flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID(uint device, StringBuilder id, int length, uint flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Request_Device_EjectW", CharSet = CharSet.Unicode)]
    private static extern uint CM_Request_Device_Eject(uint device, out int vetoType, StringBuilder vetoName, int length, uint flags);
}
