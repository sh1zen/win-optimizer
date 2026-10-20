using System.ComponentModel;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.PawnIo;
using WinOptimizer.Core.Features.Extensions;

namespace WinOptimizer.Infrastructure.Features.Extensions;

/// <summary>Owns one explicitly opened hardware session and releases it when sampling stops.</summary>
public sealed class HardwareMonitorSensors : IDisposable
{
    private readonly Func<IHardwareMonitorSession> _createSession;
    private IHardwareMonitorSession? _session;

    public HardwareMonitorSensors() : this(() => new LibreHardwareMonitorSession()) { }

    internal HardwareMonitorSensors(Func<IHardwareMonitorSession> createSession) => _createSession = createSession;

    /// <summary>Updates parents and subhardware once, then reads supported finite quantities in their domain units.</summary>
    public (IReadOnlyList<SystemMonitorSensor> Temperatures, IReadOnlyList<SystemMonitorSensor> Fans,
        IReadOnlyList<SystemMonitorMetric> Metrics) Read(
        CancellationToken cancellationToken, List<string> notices)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<SystemMonitorSensor> temperatures = [], fans = [];
        List<SystemMonitorMetric> metrics = [];
        try
        {
            _session ??= _createSession();
            var hardwareDevices = _session.GetHardware();
            if (!_session.HasLowLevelAccess)
                notices.Add("Hardware sensors: install PawnIO from SysMonitor settings for low-level CPU and motherboard access, then restart win-optimizer. GPU and external-provider readings can still be available.");
            foreach (var hardware in hardwareDevices) ReadHardware(hardware);
        }
        catch (Exception ex) when (IsHardwareFailure(ex))
        { notices.Add($"Hardware sensors: {ex.Message}"); }
        return (temperatures, fans, metrics);

        void ReadHardware(IHardware hardware)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                hardware.Update();
                foreach (var sensor in hardware.Sensors)
                {
                    if (sensor.Value is not { } value || !float.IsFinite(value)) continue;
                    // Keep the short sensor name first while retaining hardware identity in labels and tooltips.
                    var name = $"{sensor.Name} · {(hardware.HardwareType == HardwareType.Cpu ? "CPU · " : "")}{hardware.Name}";
                    if (sensor.SensorType == SensorType.Temperature && value is >= -50 and <= 200)
                        temperatures.Add(new(name, value));
                    else if (sensor.SensorType == SensorType.Fan && value is >= 0 and <= 100000)
                        fans.Add(new(name, value));
                    else if (sensor.SensorType == SensorType.Clock && value >= 0)
                        metrics.Add(new(SystemMonitorMetricKind.ClockSpeed, name, value));
                    else if (sensor.SensorType == SensorType.Power && value >= 0)
                        metrics.Add(new(SystemMonitorMetricKind.Power, name, value));
                    else if (sensor.SensorType == SensorType.Voltage)
                        metrics.Add(new(SystemMonitorMetricKind.Voltage, name, value));
                    else if (hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                    {
                        if (sensor.SensorType == SensorType.Load && value is >= 0 and <= 100)
                            metrics.Add(new(SystemMonitorMetricKind.GpuUsage, name, value));
                        else if (sensor.SensorType is SensorType.Data or SensorType.SmallData && value >= 0)
                            metrics.Add(new(SystemMonitorMetricKind.GpuMemory, name,
                                sensor.SensorType == SensorType.SmallData ? value / 1024d : value));
                    }
                    else if (hardware.HardwareType == HardwareType.Storage)
                    {
                        if (sensor.SensorType == SensorType.Load && value is >= 0 and <= 100)
                            metrics.Add(new(SystemMonitorMetricKind.StorageActivity, name, value));
                        else if (sensor.SensorType == SensorType.Throughput && value >= 0)
                            metrics.Add(new(SystemMonitorMetricKind.StorageThroughput, name, value));
                    }
                }
            }
            catch (Exception ex) when (IsHardwareFailure(ex))
            { notices.Add($"{hardware.Name}: {ex.Message}"); }
            foreach (var child in hardware.SubHardware) ReadHardware(child);
        }
    }

    private static bool IsHardwareFailure(Exception error) => error is
        IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception or COMException or DllNotFoundException;

    /// <summary>Called after the serialized worker stops; a later read opens a fresh session.</summary>
    public void Dispose()
    {
        var session = _session;
        _session = null;
        session?.Dispose();
    }
}

/// <summary>Isolates the library's native session and driver discovery from deterministic sensor tests.</summary>
internal interface IHardwareMonitorSession : IDisposable
{
    IEnumerable<IHardware> GetHardware();
    bool HasLowLevelAccess { get; }
}

/// <summary>Opens the library explicitly on first sampling and owns its native resource lifetime.</summary>
internal sealed class LibreHardwareMonitorSession : IHardwareMonitorSession
{
    private Computer? _computer;
    public bool HasLowLevelAccess => PawnIo.IsInstalled;

    public IEnumerable<IHardware> GetHardware()
    {
        if (_computer is null)
        {
            var computer = new Computer
            {
                IsCpuEnabled = true, IsGpuEnabled = true, IsMotherboardEnabled = true,
                IsControllerEnabled = true, IsStorageEnabled = true
            };
            try { computer.Open(); _computer = computer; }
            catch { computer.Close(); throw; }
        }
        return _computer.Hardware;
    }

    public void Dispose()
    {
        var computer = _computer;
        _computer = null;
        computer?.Close();
    }
}
