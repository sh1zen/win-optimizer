using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using WinOptimizer.Core.Features.Common.Extensions;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Extensions;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.Infrastructure.Features.Extensions;

namespace WinOptimizer.UI.Features.Extensions.Notch;

internal sealed record NotchReadingSnapshot(
    SystemMonitorSnapshot? System = null, IReadOnlyList<DesktopBattery>? Batteries = null,
    DesktopWeatherReading? Weather = null, bool WeatherAttempted = false, DesktopMicrophoneReading? Microphone = null,
    DesktopPower? Power = null, DesktopVolume? Volume = null, int? Brightness = null,
    IReadOnlyList<DesktopBluetoothDevice>? BluetoothDevices = null, IReadOnlyList<DesktopLocalDevice>? LocalDevices = null,
    int? FrameRate = null, bool FrameRateUnavailable = false)
{
    internal (ulong Used, ulong Total)? Memory => System is { TotalMemoryBytes: > 0 and ulong total, AvailableMemoryBytes: ulong available }
        ? (total - Math.Min(available, total), total) : null;
    internal double? MemoryPercent => Memory is { } memory ? 100d * memory.Used / memory.Total : null;
    internal double? GpuPercent => System?.HardwareMetrics.Where(metric => metric.Kind == SystemMonitorMetricKind.GpuUsage)
        .Select(metric => (double?)metric.Value).Max();
}

internal sealed record NotchStatusSources(Func<DesktopPower?> ReadPower, Func<DesktopVolume?> ReadVolume,
    Func<int?> ReadBrightness, Func<CancellationToken, Task<IReadOnlyList<DesktopBluetoothDevice>>> ReadBluetoothDevices,
    Func<CancellationToken, Task<IReadOnlyList<DesktopLocalDevice>>> ReadLocalDevices,
    Func<CancellationToken, IAsyncEnumerable<int?>> WatchFrameRate,
    Func<string, CancellationToken, Task<int?>>? ReadConnectedBattery = null);

/// <summary>Keeps system telemetry independent of view-specific work and drains each session before restarting it.</summary>
internal sealed class NotchReadings(ILogService log, Func<Func<bool>, ISystemMonitorService> createMonitor,
    Func<CancellationToken, Task<IReadOnlyList<DesktopBattery>>> readBatteries,
    Func<DesktopWeatherLocation, CancellationToken, Task<DesktopWeatherReading>>? readWeather = null,
    Func<DesktopMicrophoneReading>? readMicrophone = null, Func<double?>? readOutput = null, NotchStatusSources? statusSources = null,
    Func<CancellationToken, Task<ConnectionDailyUsage>>? readToday = null)
{
    internal const NotchModule SystemModules = NotchModule.Cpu | NotchModule.Memory | NotchModule.Gpu | NotchModule.Traffic;
    internal const NotchModule BackgroundModules = SystemModules | NotchModule.Batteries | NotchModule.StatusAlerts;
    private readonly SamplingSession _system = new(log);
    private readonly SamplingSession _batteries = new(log);
    private readonly SamplingSession _readings = new(log);
    private readonly SamplingSession _output = new(log);
    private readonly SamplingSession _dailyTraffic = new(log);
    private NotchModule _active;
    private bool _systemActive;
    private bool _batteriesActive;
    private bool _includeGraphics;
    private bool _outputActive;
    private bool _dailyTrafficActive;
    private DesktopWeatherLocation? _weatherLocation;
    private IReadOnlyList<DesktopBluetoothDevice>? _connectedBluetoothDevices;

    internal void Apply(NotchModule modules, bool showMediaAudio, DesktopWeatherLocation? location,
        Action<NotchReadingSnapshot> publish, Action<SystemMonitorSnapshot?> publishSystem,
        Action<IReadOnlyList<DesktopBattery>?> publishBatteries, Action<double?> publishPeak,
        Action<ConnectionDailyUsage?> publishToday)
    {
        Volatile.Write(ref _includeGraphics, modules.HasFlag(NotchModule.Gpu));
        bool systemActive = (modules & SystemModules) != 0;
        if (_systemActive != systemActive)
        {
            _systemActive = systemActive;
            if (systemActive) _system.Start(token => ReadSystemAsync(publishSystem, token));
            else
            {
                _ = _system.StopAsync();
                publishSystem(null);
            }
        }

        bool batteriesActive = modules.HasFlag(NotchModule.Batteries);
        if (_batteriesActive != batteriesActive)
        {
            _batteriesActive = batteriesActive;
            if (batteriesActive)
                _batteries.Start(token => PollAsync<IReadOnlyList<DesktopBattery>?>("device batteries", TimeSpan.FromSeconds(30),
                    async ct => await readBatteries(ct), publishBatteries, token));
            else
            {
                _ = _batteries.StopAsync();
                publishBatteries(null);
            }
        }

        bool outputActive = modules.HasFlag(NotchModule.AudioVisualizer) || showMediaAudio;
        if (_outputActive != outputActive)
        {
            _outputActive = outputActive;
            if (outputActive && readOutput is { } read)
                _output.Start(token => PollAsync("audio output level", TimeSpan.FromMilliseconds(140),
                    ct => Task.Run(read, ct), publishPeak, token));
            else _ = _output.StopAsync();
        }

        bool dailyTrafficActive = modules.HasFlag(NotchModule.Traffic);
        if (_dailyTrafficActive != dailyTrafficActive)
        {
            _dailyTrafficActive = dailyTrafficActive;
            if (dailyTrafficActive && readToday is { } read)
                _dailyTraffic.Start(token => PollAsync<ConnectionDailyUsage?>("daily network traffic", TimeSpan.FromMinutes(1),
                    async ct => await read(ct), publishToday, token));
            else
            {
                _ = _dailyTraffic.StopAsync();
                publishToday(null);
            }
        }

        var supported = NotchModule.Weather | NotchModule.AudioVisualizer;
        if (statusSources is not null) supported |= NotchModule.StatusAlerts | NotchModule.FrameRate;
        var active = modules & supported;
        if (location is null) active &= ~NotchModule.Weather;
        location = active.HasFlag(NotchModule.Weather) ? location : null;
        if (active == _active && location == _weatherLocation) return;
        _active = active;
        _weatherLocation = location;
        _ = _readings.StopAsync();
        if (!active.HasFlag(NotchModule.StatusAlerts)) _connectedBluetoothDevices = null;
        publish(new());
        if (active != NotchModule.None)
            _readings.Start(token => RunAsync(active, location, publish, token));
    }

    internal Task StopAsync()
    {
        _active = NotchModule.None;
        _systemActive = false;
        _batteriesActive = false;
        _outputActive = false;
        _dailyTrafficActive = false;
        _weatherLocation = null;
        return Task.WhenAll(_system.StopAsync(), _batteries.StopAsync(), _readings.StopAsync(), _output.StopAsync(), _dailyTraffic.StopAsync());
    }

    /// <summary>Owns one counter baseline until the last system module is disabled.</summary>
    private async Task ReadSystemAsync(Action<SystemMonitorSnapshot?> publish, CancellationToken token)
    {
        var system = createMonitor(() => Volatile.Read(ref _includeGraphics));
        try
        {
            await PollAsync<SystemMonitorSnapshot?>("system counters", TimeSpan.FromSeconds(2),
                async ct => await system.ReadAsync(ct), publish, token);
        }
        finally { await system.StopAsync(); }
    }

    private async Task RunAsync(NotchModule active, DesktopWeatherLocation? location, Action<NotchReadingSnapshot> publish,
        CancellationToken token)
    {
        var snapshot = new NotchReadingSnapshot();
        var weather = location is null || readWeather is null ? Task.CompletedTask :
            PollAsync("weather", TimeSpan.FromMinutes(15), ct => readWeather(location, ct),
                value => publish(snapshot = snapshot with { Weather = value, WeatherAttempted = true }), token);
        var microphone = !active.HasFlag(NotchModule.AudioVisualizer) || readMicrophone is null ? Task.CompletedTask :
            PollAsync<DesktopMicrophoneReading?>("microphone status", TimeSpan.FromSeconds(1),
                async ct => await Task.Run(readMicrophone, ct),
                value => publish(snapshot = snapshot with { Microphone = value }), token);
        var power = Task.CompletedTask;
        var volume = Task.CompletedTask;
        var brightness = Task.CompletedTask;
        var bluetoothDevices = Task.CompletedTask;
        var localDevices = Task.CompletedTask;
        if (active.HasFlag(NotchModule.StatusAlerts) && statusSources is { } status)
        {
            power = PollAsync("power", TimeSpan.FromSeconds(1), ct => Task.Run(status.ReadPower, ct),
                value => publish(snapshot = snapshot with { Power = value }), token);
            volume = PollAsync("volume", TimeSpan.FromMilliseconds(300), ct => Task.Run(status.ReadVolume, ct),
                value => publish(snapshot = snapshot with { Volume = value }), token);
            brightness = PollAsync("brightness", TimeSpan.FromSeconds(1), ct => Task.Run(status.ReadBrightness, ct),
                value => publish(snapshot = snapshot with { Brightness = value }), token);
            bluetoothDevices = PollAsync("connected Bluetooth devices", TimeSpan.FromSeconds(2),
                ct => ReadBluetoothDevicesAsync(status, ct), value => publish(snapshot = snapshot with { BluetoothDevices = value }), token);
            localDevices = PollAsync("connected USB and audio devices", TimeSpan.FromSeconds(2), status.ReadLocalDevices,
                value => publish(snapshot = snapshot with { LocalDevices = value }), token);
        }
        var fps = active.HasFlag(NotchModule.FrameRate) && statusSources is { } fpsSource
            ? WatchFrameRateAsync(fpsSource.WatchFrameRate, (value, unavailable) =>
                publish(snapshot = snapshot with { FrameRate = value, FrameRateUnavailable = unavailable }), token)
            : Task.CompletedTask;
        await Task.WhenAll(weather, microphone, power, volume, brightness, bluetoothDevices, localDevices, fps);
    }

    private async Task<IReadOnlyList<DesktopBluetoothDevice>> ReadBluetoothDevicesAsync(NotchStatusSources status, CancellationToken token)
    {
        var devices = await status.ReadBluetoothDevices(token);
        if (_connectedBluetoothDevices is { } previous && status.ReadConnectedBattery is { } readBattery)
        {
            var known = previous.Select(device => device.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Only newly connected devices need a GATT sample for the connection notice.
            devices = await Task.WhenAll(devices.Select(async device =>
                device.BatteryEndpointId is { } endpoint && !known.Contains(device.Id)
                    ? device with { Percent = await readBattery(endpoint, token) }
                    : device));
        }
        token.ThrowIfCancellationRequested();
        return _connectedBluetoothDevices = devices;
    }

    private async Task WatchFrameRateAsync(Func<CancellationToken, IAsyncEnumerable<int?>> watch,
        Action<int?, bool> publish, CancellationToken token)
    {
        try
        {
            await foreach (var rate in watch(token))
            {
                token.ThrowIfCancellationRequested();
                publish(rate, false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is Win32Exception or COMException or UnauthorizedAccessException
            or InvalidOperationException or IOException or ApplicationException)
        {
            log.LogError("Could not read the Notch graphics trace.", ex);
            if (!token.IsCancellationRequested) publish(null, true);
        }
    }

    private async Task PollAsync<T>(string source, TimeSpan interval, Func<CancellationToken, Task<T>> read,
        Action<T?> publish, CancellationToken token)
    {
        bool unavailable = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                T? value;
                try
                {
                    value = await read(token);
                    unavailable = false;
                }
                catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException
                    or InvalidOperationException or Win32Exception or HttpRequestException or JsonException or TimeoutException or FormatException)
                {
                    if (!unavailable) log.LogError($"Could not read Notch {source}.", ex);
                    unavailable = true;
                    value = default;
                }
                token.ThrowIfCancellationRequested();
                publish(value);
                await Task.Delay(interval, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    /// <summary>Serializes each sampler's start and stop without restarting the other sampler.</summary>
    private sealed class SamplingSession(ILogService log)
    {
        private Task _task = Task.CompletedTask;
        private CancellationTokenSource? _cancellation;

        internal void Start(Func<CancellationToken, Task> sample)
        {
            var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            _task = RunAsync(_task, cancellation, sample);
            _task.FireAndForget(log);
        }

        internal Task StopAsync()
        {
            _cancellation?.Cancel();
            _cancellation = null;
            return _task;
        }

        private async Task RunAsync(Task previous, CancellationTokenSource cancellation, Func<CancellationToken, Task> sample)
        {
            try
            {
                // The previous session reports its own fault. A later visit starts a fresh session.
                await previous.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
                if (!cancellation.IsCancellationRequested) await sample(cancellation.Token);
            }
            finally
            {
                if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
                cancellation.Dispose();
            }
        }
    }
}
