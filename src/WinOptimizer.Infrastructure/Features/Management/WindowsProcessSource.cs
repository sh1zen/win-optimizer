using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WinOptimizer.Core.Features.Management;
using WinOptimizer.Core.Features.Utilities;

namespace WinOptimizer.Infrastructure.Features.Management;

/// <summary>Reads Windows process counters without opening processes for termination or memory writes.</summary>
internal sealed class WindowsProcessSource(WindowsProcessPorts ports) : IProcessMonitorSource
{
    public NativeProcessSample Read()
    {
        var parents = ReadParents();
        var readings = new List<ProcessReading>();
        var processors = GetActiveProcessorCount(ushort.MaxValue);
        if (processors == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                var diagnostics = new List<string>();
                T? Measure<T>(Func<T> read) where T : struct
                {
                    try { return read(); }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                    { diagnostics.Add(ex.Message); return null; }
                }
                var pid = process.Id;
                string name;
                try { name = process.ProcessName; }
                catch (InvalidOperationException) { continue; }
                catch (Win32Exception ex) { name = "PID " + pid; diagnostics.Add(ex.Message); }
                using var handle = OpenProcess(0x1000, false, pid);
                long start = 0; long? cpu = null; ulong? read = null; ulong? write = null; string path = "";
                if (!handle.IsInvalid)
                {
                    if (GetProcessTimes(handle, out var created, out _, out var kernel, out var user))
                    { start = DateTime.FromFileTimeUtc(created.Ticks).Ticks; cpu = kernel.Ticks + user.Ticks; }
                    else diagnostics.Add(new Win32Exception(Marshal.GetLastWin32Error()).Message);
                    if (GetProcessIoCounters(handle, out var io)) { read = io.ReadBytes; write = io.WriteBytes; }
                    else diagnostics.Add(new Win32Exception(Marshal.GetLastWin32Error()).Message);
                    var buffer = new StringBuilder(32768); var length = buffer.Capacity;
                    if (QueryFullProcessImageName(handle, 0, buffer, ref length)) path = buffer.ToString();
                }
                else diagnostics.Add(new Win32Exception(Marshal.GetLastWin32Error()).Message);
                readings.Add(new(new(pid, start), parents.GetValueOrDefault(pid), name, path, cpu,
                    Measure(() => process.WorkingSet64), Measure(() => process.PrivateMemorySize64), read, write,
                    Measure(() => process.Threads.Count), Measure(() => process.HandleCount), string.Join(" · ", diagnostics.Distinct())));
            }
        }
        return new(checked((int)processors), readings);
    }

    public void AssertCurrent(ProcessIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!identity.CanInspect) throw new UtilityOperationException("The process lifetime is unavailable or protected. Select an accessible process.");
        using var handle = OpenProcess(0x1000, false, identity.Pid);
        if (handle.IsInvalid || !GetProcessTimes(handle, out var created, out var exited, out _, out _) ||
            exited.Ticks != 0 || DateTime.FromFileTimeUtc(created.Ticks).Ticks != identity.StartedAtUtcTicks)
            throw new UtilityOperationException("The selected process has exited, changed identity, or is no longer accessible. Refresh the process list.");
    }

    /// <summary>Loads command-line/account metadata and ports only for the explicitly selected lifetime.</summary>
    public ProcessInspection Inspect(ProcessIdentity identity)
    {
        var warnings = new List<string>();
        string command = "", owner = "", description = "", publisher = "";
        try
        {
            using var search = new ManagementObjectSearcher("SELECT CommandLine,ExecutablePath FROM Win32_Process WHERE ProcessId = " + identity.Pid);
            search.Options.Timeout = TimeSpan.FromSeconds(5);
            using var results = search.Get();
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    command = item["CommandLine"]?.ToString() ?? "";
                    var path = item["ExecutablePath"]?.ToString();
                    if (!string.IsNullOrEmpty(path))
                    {
                        try { var info = FileVersionInfo.GetVersionInfo(path); description = info.FileDescription ?? ""; publisher = info.CompanyName ?? ""; }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception) { warnings.Add(ex.Message); }
                    }
                    using var account = item.InvokeMethod("GetOwner", null, null);
                    if (account != null && Convert.ToUInt32(account["ReturnValue"]) == 0)
                        owner = account["Domain"] + "\\" + account["User"];
                    else warnings.Add("Windows did not provide the process owner.");
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or InvalidOperationException)
        { warnings.Add("Process metadata: " + ex.Message); }
        var endpoints = ports.Read(identity.Pid, warnings);
        return new(identity, DateTimeOffset.Now, command, owner, description, publisher, endpoints, warnings);
    }

    public ProcessPortSnapshot ReadPorts(ProcessIdentity identity)
    {
        var warnings = new List<string>();
        var endpoints = ports.Read(identity.Pid, warnings);
        return new(identity, DateTimeOffset.Now, endpoints, warnings);
    }

    private static Dictionary<int, int> ReadParents()
    {
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Exe = "" };
        var parents = new Dictionary<int, int>();
        if (!Process32First(snapshot, ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error());
        do { parents[checked((int)entry.Pid)] = checked((int)entry.ParentPid); } while (Process32Next(snapshot, ref entry));
        var error = Marshal.GetLastWin32Error();
        if (error != 18) throw new Win32Exception(error); // ERROR_NO_MORE_FILES is the documented end of enumeration.
        return parents;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, Pid; public UIntPtr Heap; public uint Module, Threads, ParentPid; public int Priority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Exe;
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileTime
    { public uint Low, High; public readonly long Ticks => ((long)High << 32) | Low; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out FileTime created, out FileTime exited, out FileTime kernel, out FileTime user);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(SafeProcessHandle process, out IoCounters counters);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetActiveProcessorCount(ushort group);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
}
