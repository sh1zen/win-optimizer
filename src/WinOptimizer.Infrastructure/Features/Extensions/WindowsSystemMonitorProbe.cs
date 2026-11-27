using System.ComponentModel;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using WinOptimizer.Core.Features.Extensions;

namespace WinOptimizer.Infrastructure.Features.Extensions;

/// <summary>Reads native counters and built-in hardware sensors, supplementing missing types from local WMI providers.</summary>
public sealed class WindowsSystemMonitorProbe(HardwareMonitorSensors? sensors, bool useExternalSensors = true) : ISystemMonitorProbe
{
    private readonly SensorQueries _queries = new(provider => new WmiSensorQuery(provider));
    private readonly Func<bool>? _includeSensors;
    private readonly bool _includeNetwork = true;

    internal WindowsSystemMonitorProbe() : this(null, useExternalSensors: false) => _includeNetwork = false;

    internal WindowsSystemMonitorProbe(HardwareMonitorSensors sensors, Func<bool> includeSensors)
        : this(sensors, useExternalSensors: false) => _includeSensors = includeSensors;

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

        var network = _includeNetwork ? ReadNetwork(cancellationToken, notices) : FrozenDictionary<string, (long, long)>.Empty;

        // Timestamp the fast counters before optional sensor queries, whose latency is unrelated to their rates.
        var timestamp = DateTimeOffset.Now;
        var elapsed = TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
        var includeSensors = _includeSensors?.Invoke() != false;
        if (!includeSensors) sensors?.Dispose();
        var (temperatures, fans, metrics) = (includeSensors ? sensors?.Read(cancellationToken, notices) : null) ?? ([], [], []);
        if (useExternalSensors && (temperatures.Count == 0 || fans.Count == 0))
        {
            var (providerTemperatures, providerFans) = ReadSensors(
                provider => _queries.Read(provider, cancellationToken, notices), cancellationToken, notices,
                needTemperatures: temperatures.Count == 0, needFans: fans.Count == 0);
            if (temperatures.Count == 0) temperatures = providerTemperatures;
            if (fans.Count == 0) fans = providerFans;
        }
        if (useExternalSensors && temperatures.Count == 0) notices.Add("Temperatures: no supported sensor is reporting a value. Check hardware sensor setup in SysMonitor settings.");
        if (useExternalSensors && fans.Count == 0) notices.Add("Fans: no supported RPM sensor is reporting a value. Firmware may not expose fan speeds; check hardware sensor setup in SysMonitor settings.");
        return new(timestamp, elapsed, idle, kernel, user, totalMemory, availableMemory,
            network, temperatures, fans, notices) { HardwareMetrics = metrics };
    }

    /// <summary>Reads active adapters for consumers that display transfer rates.</summary>
    private static IReadOnlyDictionary<string, (long Received, long Sent)> ReadNetwork(CancellationToken token, List<string> notices)
    {
        Dictionary<string, (long Received, long Sent)> network = [];
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                token.ThrowIfCancellationRequested();
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
        return network;
    }

    /// <summary>Uses an already-running hardware WMI provider; missing providers leave sensors unavailable.</summary>
    internal static (IReadOnlyList<SystemMonitorSensor> Temperatures, IReadOnlyList<SystemMonitorSensor> Fans) ReadSensors(
        Func<string, (IReadOnlyList<SystemMonitorSensor> Temperatures, IReadOnlyList<SystemMonitorSensor> Fans)> readProvider,
        CancellationToken cancellationToken, List<string> notices, bool needTemperatures = true, bool needFans = true)
    {
        IReadOnlyList<SystemMonitorSensor> temperatures = [], fans = [];
        foreach (var provider in new[] { "LibreHardwareMonitor", "OpenHardwareMonitor" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var (providerTemperatures, providerFans) = readProvider(provider);
                if (needTemperatures && temperatures.Count == 0) temperatures = providerTemperatures;
                if (needFans && fans.Count == 0) fans = providerFans;
                if ((!needTemperatures || temperatures.Count > 0) && (!needFans || fans.Count > 0)) break;
            }
            catch (ManagementException ex) when (ex.ErrorCode is ManagementStatus.InvalidNamespace or ManagementStatus.InvalidClass)
            { /* An optional provider is absent; try the other supported local provider. */ }
            catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
            { notices.Add($"{provider}: {ex.Message}"); }
        }
        return (temperatures, fans);
    }

    internal interface ISensorQuery : IDisposable
    {
        (IReadOnlyList<SystemMonitorSensor> Temperatures, IReadOnlyList<SystemMonitorSensor> Fans) Read(
            CancellationToken token, List<string> notices);
    }

    /// <summary>Retains working WMI connections while reading fresh values and reconnecting if a connection goes stale.</summary>
    internal sealed class SensorQueries(Func<string, ISensorQuery> open) : IDisposable
    {
        private readonly Dictionary<string, ISensorQuery> _working = new();

        public (IReadOnlyList<SystemMonitorSensor> Temperatures, IReadOnlyList<SystemMonitorSensor> Fans) Read(
            string provider, CancellationToken token, List<string> notices)
        {
            lock (_working)
            {
                token.ThrowIfCancellationRequested();
                if (_working.TryGetValue(provider, out var existing))
                {
                    var firstNotice = notices.Count;
                    try { return existing.Read(token, notices); }
                    catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
                    {
                        _working.Remove(provider);
                        existing.Dispose();
                        notices.RemoveRange(firstNotice, notices.Count - firstNotice);
                    }
                }

                token.ThrowIfCancellationRequested();
                var query = open(provider);
                try
                {
                    var result = query.Read(token, notices);
                    _working.Add(provider, query);
                    return result;
                }
                catch
                {
                    query.Dispose();
                    throw;
                }
            }
        }

        public void Dispose()
        {
            lock (_working)
            {
                foreach (var query in _working.Values) query.Dispose();
                _working.Clear();
            }
        }
    }

    /// <summary>Runs fresh bounded queries over one provider connection and releases each result set.</summary>
    private sealed class WmiSensorQuery(string provider) : ISensorQuery
    {
        private readonly ManagementObjectSearcher _searcher = new(new ManagementScope($@"root\{provider}"),
            new ObjectQuery("SELECT Name, SensorType, Value FROM Sensor WHERE SensorType='Temperature' OR SensorType='Fan'"),
            new System.Management.EnumerationOptions { ReturnImmediately = true, Rewindable = false, Timeout = TimeSpan.FromSeconds(2) });

        public (IReadOnlyList<SystemMonitorSensor> Temperatures, IReadOnlyList<SystemMonitorSensor> Fans) Read(
            CancellationToken cancellationToken, List<string> notices)
        {
            using var entries = _searcher.Get();
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

        public void Dispose() => _searcher.Dispose();
    }

    /// <summary>Releases the built-in hardware session after the current sample finishes.</summary>
    public void Close()
    {
        _queries.Dispose();
        sensors?.Dispose();
    }

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
