using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using WinOptimizer.Core.Features.Extensions;

namespace WinOptimizer.Infrastructure.Features.Extensions;

/// <summary>Reads native counters and built-in hardware sensors, supplementing missing types from local WMI providers.</summary>
public sealed class WindowsSystemMonitorProbe(HardwareMonitorSensors sensors) : ISystemMonitorProbe
{
    public SystemMonitorCounters Read(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<string> notices = [];
        ulong? idle = null, kernel = null, user = null, totalMemory = null, availableMemory = null;
        if (GetSystemTimes(out var idleCounter, out var kernelCounter, out var userCounter))
        { idle = idleCounter; kernel = kernelCounter; user = userCounter; }
        else notices.Add($"CPU: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");

        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (GlobalMemoryStatusEx(ref memory))
        { totalMemory = memory.TotalPhysical; availableMemory = memory.AvailablePhysical; }
        else notices.Add($"RAM: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");

        Dictionary<string, (long Received, long Sent)> network = [];
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (adapter.OperationalStatus != OperationalStatus.Up ||
                    adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                try
                {
                    var statistics = adapter.GetIPStatistics();
                    network[adapter.Id] = (statistics.BytesReceived, statistics.BytesSent);
                }
                catch (NetworkInformationException ex) { notices.Add($"{adapter.Name}: {ex.Message}"); }
            }
        }
        catch (NetworkInformationException ex) { notices.Add($"Network: {ex.Message}"); }

        // Timestamp the fast counters before optional sensor queries, whose latency is unrelated to their rates.
        var timestamp = DateTimeOffset.Now;
        var elapsed = TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
        var (temperatures, fans, metrics) = sensors.Read(cancellationToken, notices);
        if (temperatures.Count == 0 || fans.Count == 0)
        {
            var (providerTemperatures, providerFans) = ReadSensors(
                provider => ReadProvider(provider, cancellationToken, notices), cancellationToken, notices);
            if (temperatures.Count == 0) temperatures = providerTemperatures;
            if (fans.Count == 0) fans = providerFans;
        }
        if (temperatures.Count == 0) notices.Add("Temperatures: no supported sensor is reporting a value. Check hardware sensor setup in SysMonitor settings.");
        if (fans.Count == 0) notices.Add("Fans: no supported RPM sensor is reporting a value. Firmware may not expose fan speeds; check hardware sensor setup in SysMonitor settings.");
        return new(timestamp, elapsed, idle, kernel, user, totalMemory, availableMemory,
            network, temperatures, fans, notices) { HardwareMetrics = metrics };
    }

    /// <summary>Uses an already-running hardware WMI provider; missing providers leave sensors unavailable.</summary>
    internal static (IReadOnlyList<SystemMonitorSensor> Temperatures, IReadOnlyList<SystemMonitorSensor> Fans) ReadSensors(
        Func<string, (IReadOnlyList<SystemMonitorSensor> Temperatures, IReadOnlyList<SystemMonitorSensor> Fans)> readProvider,
        CancellationToken cancellationToken, List<string> notices)
    {
        IReadOnlyList<SystemMonitorSensor> temperatures = [], fans = [];
        foreach (var provider in new[] { "LibreHardwareMonitor", "OpenHardwareMonitor" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var (providerTemperatures, providerFans) = readProvider(provider);
                if (temperatures.Count == 0) temperatures = providerTemperatures;
                if (fans.Count == 0) fans = providerFans;
                if (temperatures.Count > 0 && fans.Count > 0) break;
            }
            catch (ManagementException ex) when (ex.ErrorCode is ManagementStatus.InvalidNamespace or ManagementStatus.InvalidClass)
            { /* An optional provider is absent; try the other supported local provider. */ }
            catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
            { notices.Add($"{provider}: {ex.Message}"); }
        }
        return (temperatures, fans);
    }

    /// <summary>Queries one local provider with bounded waits and releases every native WMI object.</summary>
    private static (IReadOnlyList<SystemMonitorSensor>, IReadOnlyList<SystemMonitorSensor>) ReadProvider(
        string provider, CancellationToken cancellationToken, List<string> notices)
    {
        using var searcher = new ManagementObjectSearcher(new ManagementScope($@"root\{provider}"),
            new ObjectQuery("SELECT Name, SensorType, Value FROM Sensor WHERE SensorType='Temperature' OR SensorType='Fan'"),
            new System.Management.EnumerationOptions { ReturnImmediately = true, Rewindable = false, Timeout = TimeSpan.FromSeconds(2) });
        using var entries = searcher.Get();
        List<SystemMonitorSensor> temperatures = [], fans = [];
        foreach (ManagementObject entry in entries)
        {
            using (entry)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry["Value"] is not { } raw || entry["Name"] is not string name) continue;
                if (!double.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                { notices.Add($"{provider}: {name}: invalid sensor value."); continue; }
                if (entry["SensorType"] is "Temperature" && value is >= -50 and <= 200)
                    temperatures.Add(new(name, value));
                else if (entry["SensorType"] is "Fan" && value is >= 0 and <= 100000)
                    fans.Add(new(name, value));
            }
        }
        return (temperatures, fans);
    }

    /// <summary>Releases the built-in hardware session after the current sample finishes.</summary>
    public void Close() => sensors.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
