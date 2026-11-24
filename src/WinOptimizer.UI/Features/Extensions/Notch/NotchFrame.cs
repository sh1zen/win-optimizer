using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace WinOptimizer.UI.Features.Extensions.Notch;

/// <summary>Positions and shapes the widget at the top of the selected display work area.</summary>
internal sealed class NotchFrame(nint handle)
{
    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;
    private const uint NoSendChanging = 0x0400;
    private SizeInt32 _shape;
    private int _cornerRadius;
    private int _flare;
    private bool _rounded;
    private NotchPlacement _placement = NotchPlacement.Default;
    private DisplayArea? _display;

    internal void ApplyPlacement(NotchPlacement placement)
    {
        _placement = placement;
        RefreshDisplay();
    }

    internal void RefreshDisplay() =>
        _display = _placement.DisplayDevice is { } preferred
            ? DisplayDevices().FirstOrDefault(item => string.Equals(item.Device, preferred, StringComparison.OrdinalIgnoreCase)).Area
                ?? DisplayArea.Primary
            : DisplayArea.Primary;

    /// <summary>Uses a monitor interface ID when available, including across display renumbering.</summary>
    internal static IEnumerable<(DisplayArea Area, string Device)> DisplayDevices()
    {
        var displays = DisplayArea.FindAll();
        // WinUI's display vector supports indexed access even when its WinRT iterator is unavailable.
        for (int index = 0; index < displays.Count; index++)
        {
            var area = displays[index];
            var monitor = Microsoft.UI.Win32Interop.GetMonitorFromDisplayId(area.DisplayId);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor == 0 || !GetMonitorInfo(monitor, ref info) || string.IsNullOrEmpty(info.Device)) continue;
            var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            const uint monitorInterfaceName = 1;
            var id = EnumDisplayDevices(info.Device, 0, ref device, monitorInterfaceName) && !string.IsNullOrEmpty(device.Id)
                ? device.Id : info.Device;
            yield return (area, id);
        }
    }

    /// <summary>Lets the shaped client surface define the complete outline.</summary>
    public void ConfigureChrome()
    {
        const int styleIndex = -16;
        const int frameStyles = 0x00C40000; // WS_CAPTION and WS_THICKFRAME.
        int style = GetWindowLong(handle, styleIndex);
        if ((style & frameStyles) != 0)
        {
            if (SetWindowLong(handle, styleIndex, style & ~frameStyles) == 0 && Marshal.GetLastWin32Error() != 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            const uint noResize = 0x0001, noMove = 0x0002, frameChanged = 0x0020;
            if (!SetWindowPos(handle, 0, 0, 0, 0, 0, noResize | noMove | NoZOrder | NoActivate | frameChanged))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            const int cornerPreference = 33, borderColor = 34;
            uint useRegionCorners = 1, noBorder = 0xFFFFFFFE;
            Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(handle, cornerPreference, ref useRegionCorners, sizeof(uint)));
            Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(handle, borderColor, ref noBorder, sizeof(uint)));
        }
    }

    public void Update(double width, double height, double radius, double flare)
    {
        var display = _display ??= DisplayArea.Primary;
        var scale = GetDpiForWindow(handle) / 96d;
        bool floating = _placement.Floating;
        var bounds = Place(display.WorkArea, scale, width, height, floating);
        var shoulder = floating ? 0 : Math.Min((int)Math.Round(flare * scale), Math.Min(bounds.Width / 4, bounds.Height / 3));
        var limit = floating ? Math.Min(bounds.Width, bounds.Height) / 2 :
            Math.Min(bounds.Width / 2 - shoulder, bounds.Height - shoulder);
        var cornerRadius = Math.Min((int)Math.Round(radius * scale), limit);
        if (_shape.Width != bounds.Width || _shape.Height != bounds.Height || _cornerRadius != cornerRadius ||
            _flare != shoulder || _rounded != floating)
        {
            nint region;
            if (floating)
                region = CreateRoundRectRgn(0, 0, bounds.Width, bounds.Height, 2 * cornerRadius, 2 * cornerRadius);
            else
            {
                var outline = CreateOutline(bounds.Width, bounds.Height, shoulder, cornerRadius);
                region = CreatePolygonRgn(outline, outline.Length, 1);
            }
            if (region == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (SetWindowRgn(handle, region, true) == 0)
            {
                var error = new Win32Exception(Marshal.GetLastWin32Error());
                DeleteObject(region);
                throw error;
            }
            _shape = new(bounds.Width, bounds.Height);
            _cornerRadius = cornerRadius;
            _flare = shoulder;
            _rounded = floating;
        }
        var screen = display.OuterBounds;
        // Keep the compact overlay below the minimum size of a standard overlapped window.
        if (!SetWindowPos(handle, 0, screen.X + bounds.X, screen.Y + bounds.Y, bounds.Width, bounds.Height,
            NoZOrder | NoActivate | NoSendChanging))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    /// <summary>Joins the screen edge through inward shoulders and rounds the bottom corners.</summary>
    private static PointInt32[] CreateOutline(int width, int height, int shoulder, int radius)
    {
        const int segments = 8;
        var points = new PointInt32[4 * (segments + 1)];
        int index = 0;
        AddArc(width, shoulder, shoulder, -Math.PI / 2, -Math.PI);
        AddArc(width - shoulder - radius, height - radius, radius, 0, Math.PI / 2);
        AddArc(shoulder + radius, height - radius, radius, Math.PI / 2, Math.PI);
        AddArc(0, shoulder, shoulder, 0, -Math.PI / 2);
        return points;

        void AddArc(int x, int y, int size, double start, double end)
        {
            for (int i = 0; i <= segments; i++)
            {
                double angle = start + (end - start) * i / segments;
                points[index++] = new(x + (int)Math.Round(size * Math.Cos(angle)), y + (int)Math.Round(size * Math.Sin(angle)));
            }
        }
    }

    internal static RectInt32 Place(RectInt32 workArea, double scale, double width, double height, bool floating = false)
    {
        int gap = floating ? Math.Min((int)Math.Round(12 * scale), workArea.Height - 1) : 0;
        int pixelWidth = Math.Clamp((int)Math.Round(width * scale), 1, workArea.Width);
        int pixelHeight = Math.Clamp((int)Math.Round(height * scale), 1, workArea.Height - gap);
        return new(workArea.X + (workArea.Width - pixelWidth) / 2, workArea.Y + gap, pixelWidth, pixelHeight);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public RectInt32 Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string? Device;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string? Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? Description;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? Key;
    }

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string adapter, uint index, ref DisplayDevice device, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreatePolygonRgn([In] PointInt32[] points, int count, int fillMode);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(nint window, int index, int style);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref uint value, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(nint window, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint handle);
}
