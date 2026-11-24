using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinOptimizer.Infrastructure.Features.Extensions;
using Windows.Graphics;
using WinRT.Interop;

namespace WinOptimizer.UI.Features.Extensions;

/// <summary>Fits an Explorer popup to the source monitor at its logical size.</summary>
internal static class DesktopPopupPlacement
{
    public static void Center(Window window, ExplorerPreviewSelection.FocusedView source, SizeInt32 logicalSize)
    {
        var area = source.IsDesktop && GetCursorPos(out var cursor)
            ? DisplayArea.GetFromPoint(cursor, DisplayAreaFallback.Nearest)
            : DisplayArea.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(source.Window), DisplayAreaFallback.Nearest);
        double scale = GetScale(source.Window);
        var size = new SizeInt32((int)(logicalSize.Width * scale), (int)(logicalSize.Height * scale));
        Fit(window.AppWindow, area, size, center: true);
    }

    /// <summary>Fits the measured client height and native frame within the current monitor's work area.</summary>
    public static void Resize(Window window, Func<double, double> measureClientHeight)
    {
        var appWindow = window.AppWindow;
        var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest);
        var size = appWindow.Size;
        var client = appWindow.ClientSize;
        int width = Math.Min(size.Width, area?.WorkArea.Width ?? size.Width);
        int clientWidth = Math.Max(1, width - (size.Width - client.Width));
        double scale = GetScale(WindowNative.GetWindowHandle(window));
        int height = (int)Math.Ceiling(measureClientHeight(clientWidth / scale) * scale) + size.Height - client.Height;
        Fit(appWindow, area, new SizeInt32(width, height), center: false);
    }

    private static void Fit(AppWindow appWindow, DisplayArea? area, SizeInt32 requested, bool center)
    {
        if (area is null) { appWindow.Resize(requested); return; }
        var work = area.WorkArea;
        int width = Math.Min(work.Width, requested.Width);
        int height = Math.Min(work.Height, requested.Height);
        var position = appWindow.Position;
        var size = appWindow.Size;
        int x = center ? work.X + (work.Width - width) / 2 : Math.Clamp(position.X, work.X, work.X + work.Width - width);
        int y = center ? work.Y + (work.Height - height) / 2 : Math.Clamp(position.Y, work.Y, work.Y + work.Height - height);
        if (position.X != x || position.Y != y || size.Width != width || size.Height != height)
            appWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private static double GetScale(nint window) => Math.Max(96, GetDpiForWindow(window)) / 96.0;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out PointInt32 point);
}
