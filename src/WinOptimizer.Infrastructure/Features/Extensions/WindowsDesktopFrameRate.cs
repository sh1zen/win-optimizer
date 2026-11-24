using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace WinOptimizer.Infrastructure.Features.Extensions;

/// <summary>Measures successful DirectX presents from the foreground fullscreen process using a private real-time ETW session.</summary>
public static class WindowsDesktopFrameRate
{
    public static async IAsyncEnumerable<int?> WatchAsync([EnumeratorCancellation] CancellationToken token)
    {
        var capture = await Task.Run(() => new Capture(), token).ConfigureAwait(false);
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                yield return await capture.SampleAsync().ConfigureAwait(false);
        }
        finally { await capture.DisposeAsync().ConfigureAwait(false); }
    }

    private sealed class Capture
    {
        private static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
        private static readonly Guid D3d9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
        private readonly TraceEventSession _session;
        private readonly Task _reader;
        private readonly object _gate = new();
        private readonly byte[] _payload = new byte[12];
        private readonly Dictionary<(int PresentStartId, int ThreadId), ulong> _pending = new();
        private readonly Dictionary<(bool Dxgi, ulong SwapChain), (int Count, double First, double Last)> _frames = new();
        private int _pid = ForegroundGameProcess();

        internal Capture()
        {
            _session = new TraceEventSession("win-optimizer-fps-" + Guid.NewGuid().ToString("N")) { StopOnDispose = true };
            try
            {
                var source = _session.Source;
                source.AllEvents += OnEvent;
                const ulong presentationEvents = 0x8000000000000002;
                _session.EnableProvider(Dxgi, TraceEventLevel.Verbose, presentationEvents);
                _session.EnableProvider(D3d9, TraceEventLevel.Verbose, presentationEvents);
                _reader = Task.Factory.StartNew(() => source.Process(), CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            catch { _session.Dispose(); throw; }
        }

        private void OnEvent(TraceEvent data)
        {
            bool dxgi = data.ProviderGuid == Dxgi;
            if (!dxgi && data.ProviderGuid != D3d9) return;
            var id = (int)data.ID;
            // DXGI has separate start/stop events for regular and multiplane overlay presents.
            int start = dxgi ? (id is 55 or 56 ? 55 : 42) : 1;
            if (id != start && id != start + 1) return;

            lock (_gate)
            {
                if (_pid == 0 || data.ProcessID != _pid) return;
                var thread = (start, data.ThreadID);
                if (id == start)
                {
                    var pointerSize = data.PointerSize;
                    var size = pointerSize + (dxgi ? sizeof(uint) : 0);
                    if (pointerSize is not (4 or 8) || data.EventDataLength < size) return;
                    data.EventData(_payload, 0, 0, size);
                    // DXGI presents flagged TEST check the swap chain without submitting a frame.
                    if (dxgi && (BinaryPrimitives.ReadUInt32LittleEndian(_payload.AsSpan(pointerSize)) & 1) != 0)
                    {
                        _pending.Remove(thread);
                        return;
                    }
                    _pending[thread] = pointerSize == 8
                        ? BinaryPrimitives.ReadUInt64LittleEndian(_payload)
                        : BinaryPrimitives.ReadUInt32LittleEndian(_payload);
                    return;
                }

                if (!_pending.Remove(thread, out var chain) || data.EventDataLength < sizeof(int)) return;
                data.EventData(_payload, 0, 0, sizeof(int));
                if (BinaryPrimitives.ReadInt32LittleEndian(_payload) != 0) return;
                var key = (dxgi, chain);
                double time = data.TimeStampRelativeMSec;
                _frames[key] = _frames.TryGetValue(key, out var previous)
                    ? (previous.Count + 1, previous.First, time)
                    : (1, time, time);
            }
        }

        internal async Task<int?> SampleAsync()
        {
            if (_reader.IsCompleted)
            {
                await _reader.ConfigureAwait(false);
                throw new InvalidOperationException("The Windows graphics trace stopped.");
            }
            var foreground = ForegroundGameProcess();
            lock (_gate)
            {
                int? result = null;
                if (foreground != _pid)
                {
                    _pid = foreground;
                    _pending.Clear();
                }
                else if (_frames.Count != 0)
                {
                    // A process can present to several surfaces. Use its busiest swap chain.
                    var sample = _frames.Values.MaxBy(frame => frame.Count);
                    double seconds = (sample.Last - sample.First) / 1000;
                    if (sample.Count >= 2 && seconds > 0)
                        result = (int)Math.Clamp(Math.Round((sample.Count - 1) / seconds), 1, int.MaxValue);
                }
                _frames.Clear();
                return result;
            }
        }

        internal async ValueTask DisposeAsync()
        {
            await Task.Run(_session.Dispose).ConfigureAwait(false);
            // A read error is reported by SampleAsync, while cancellation still drains the reader.
            await _reader.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static int ForegroundGameProcess()
    {
        var window = GetForegroundWindow();
        if (window == 0 || GetWindowThreadProcessId(window, out var pid) == 0 ||
            pid == 0 || pid == Environment.ProcessId || window == GetDesktopWindow() || window == GetShellWindow())
            return 0;
        var monitor = MonitorFromWindow(window, 0);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info) || (info.Flags & 1) == 0 ||
            !GetClientRect(window, out var client)) return 0;
        var origin = new Point();
        if (!ClientToScreen(window, ref origin)) return 0;
        var bounds = info.Bounds;
        return origin.X <= bounds.Left && origin.Y <= bounds.Top &&
            origin.X + client.Right >= bounds.Right && origin.Y + client.Bottom >= bounds.Bottom ? (int)pid : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect Bounds, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out Rect client);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint window, ref Point point);
}
