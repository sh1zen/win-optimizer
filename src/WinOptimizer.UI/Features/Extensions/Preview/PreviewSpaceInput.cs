using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using WinOptimizer.Infrastructure.Features.Extensions;

namespace WinOptimizer.UI.Features.Extensions.Preview;

/// <summary>Observes single Space transitions through Windows Raw Input without intercepting keystrokes.</summary>
internal sealed class PreviewSpaceInput : IDisposable
{
    private readonly nint _window;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action<ExplorerPreviewSelection.FocusedView> _pressed;
    private readonly Procedure _procedure;
    private bool _spaceDown;
    private bool _active;
    private bool _queued;

    public PreviewSpaceInput(nint window, DispatcherQueue dispatcher, Action<ExplorerPreviewSelection.FocusedView> pressed)
    {
        _window = window;
        _dispatcher = dispatcher;
        _pressed = pressed;
        _procedure = WindowProc;
        if (!SetWindowSubclass(window, _procedure, 1, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var keyboard = new RawDevice { UsagePage = 1, Usage = 6, Flags = 0x100, Target = window };
        if (!RegisterRawInputDevices(in keyboard, 1, (uint)Marshal.SizeOf<RawDevice>()))
        {
            var error = Marshal.GetLastWin32Error();
            RemoveWindowSubclass(window, _procedure, 1);
            throw new Win32Exception(error);
        }
        _active = true;
    }

    private nint WindowProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == 0x00ff && _active)
        {
            uint size = (uint)Marshal.SizeOf<RawInput>();
            if (GetRawInputData(lParam, 0x10000003, out var input, ref size, (uint)Marshal.SizeOf<RawHeader>()) == size &&
                input.Header.Type == 1 && input.VirtualKey == 0x20)
            {
                var released = (input.Flags & 1) != 0;
                if (!released && !_spaceDown && !_queued && !ModifierPressed() && ExplorerPreviewSelection.Capture() is { } view)
                {
                    _queued = _dispatcher.TryEnqueue(DispatcherQueuePriority.High, () =>
                    {
                        _queued = false;
                        if (_active) _pressed(view);
                    });
                }
                _spaceDown = !released;
            }
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    private static bool ModifierPressed() =>
        GetAsyncKeyState(0x10) < 0 || GetAsyncKeyState(0x11) < 0 || GetAsyncKeyState(0x12) < 0 ||
        GetAsyncKeyState(0x5b) < 0 || GetAsyncKeyState(0x5c) < 0;

    public void Dispose()
    {
        if (!_active) return;
        _active = false;
        var keyboard = new RawDevice { UsagePage = 1, Usage = 6, Flags = 1 };
        RegisterRawInputDevices(in keyboard, 1, (uint)Marshal.SizeOf<RawDevice>());
        RemoveWindowSubclass(_window, _procedure, 1);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawDevice
    {
        public ushort UsagePage, Usage;
        public uint Flags;
        public nint Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawHeader
    {
        public uint Type, Size;
        public nint Device;
        public nuint WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInput
    {
        public RawHeader Header;
        public ushort MakeCode, Flags, Reserved, VirtualKey;
        public uint Message, ExtraInformation;
    }

    private delegate nint Procedure(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint window, Procedure procedure, nuint id, nuint data);
    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint window, Procedure procedure, nuint id);
    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(in RawDevice devices, uint count, uint size);
    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(nint handle, uint command, out RawInput data, ref uint size, uint headerSize);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
}
