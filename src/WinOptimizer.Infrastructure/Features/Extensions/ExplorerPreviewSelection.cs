using System.Runtime.InteropServices;
using System.Text;

namespace WinOptimizer.Infrastructure.Features.Extensions;

/// <summary>Reads the selected filesystem item from the focused Explorer or desktop view.</summary>
public static class ExplorerPreviewSelection
{
    private static readonly Guid ShellWindowsClass = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid TopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid OleWindow = typeof(IOleWindow).GUID;

    public readonly record struct FocusedView(nint Window, nint FileView, nint Tab, bool IsDesktop);

    public static nint ForegroundWindow => GetForegroundWindow();

    /// <summary>Captures just the native window hierarchy before any cross-process Shell call.</summary>
    public static FocusedView? Capture()
    {
        var window = GetForegroundWindow();
        if (window == 0) return null;

        var thread = GetWindowThreadProcessId(window, out _);
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (thread == 0 || !GetGUIThreadInfo(thread, ref info) || (info.Flags & 0x1e) != 0)
            return null;
        return FindFileView(window, info.Focus, GetParent, ClassName);
    }

    internal static FocusedView? FindFileView(nint window, nint focus, Func<nint, nint> getParent, Func<nint, string> getClass)
    {
        var windowClass = getClass(window);
        bool desktop = windowClass is "Progman" or "WorkerW";
        if (!desktop && windowClass is not ("CabinetWClass" or "ExploreWClass")) return null;
        var focusClass = getClass(focus);
        if (focusClass is not ("DirectUIHWND" or "SysListView32" or "SHELLDLL_DefView")) return null;

        nint fileView = 0;
        nint tab = 0;
        var parent = focus;
        for (var depth = 0; depth < 24 && parent != 0 && parent != window; depth++, parent = getParent(parent))
        {
            var name = parent == focus ? focusClass : getClass(parent);
            if (name == "SHELLDLL_DefView") fileView = parent;
            if (name == "ShellTabWindowClass") tab = parent;
        }
        return fileView != 0 && parent == window ? new FocusedView(window, fileView, tab, desktop) : null;
    }

    public static string? ReadSingleItem(nint window) =>
        Capture() is { } view && view.Window == window ? ReadSingleItem(view) : null;

    /// <summary>Reads only the captured view, then checks that it still owns focus.</summary>
    public static string? ReadSingleItem(FocusedView view)
    {
        if (Capture() != view) return null;
        IShellWindows? collection = null;
        try
        {
            collection = (IShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(ShellWindowsClass, throwOnError: true)!)!;
            var path = view.IsDesktop ? ReadDesktop(collection, view) : ReadExplorer(collection, view);
            return Capture() == view ? path : null;
        }
        finally { Release(collection); }
    }

    private static string? ReadDesktop(IShellWindows collection, FocusedView view)
    {
        object? browser = null;
        try
        {
            const int desktopClass = 8, needDispatch = 1;
            object desktop = 0, root = 0;
            browser = collection.FindWindowSW(ref desktop, ref root, desktopClass, out int shellWindow, needDispatch);
            if (browser is not IShellBrowserDispatch desktopBrowser || GetWindowThreadProcessId(shellWindow, out uint shellId) == 0 ||
                GetWindowThreadProcessId(view.Window, out uint foregroundId) == 0 || shellId != foregroundId)
                return null;
            return SelectedPath(desktopBrowser);
        }
        finally { Release(browser); }
    }

    private static string? ReadExplorer(IShellWindows collection, FocusedView view)
    {
        int count = Math.Min(collection.Count, 128);
        IShellBrowserDispatch? frameMatch = null;
        bool ambiguous = false;
        try
        {
            for (var index = 0; index < count; index++)
            {
                object? browser = null;
                try
                {
                    browser = collection.Item(index);
                    if (browser is not IShellBrowserDispatch shellBrowser || new nint(shellBrowser.HWND) != view.Window) continue;
                    if (view.Tab != 0)
                    {
                        var browserWindow = BrowserWindow(browser);
                        if (browserWindow == view.Tab) return SelectedPath(shellBrowser);
                        if (browserWindow != 0 && browserWindow != view.Window) continue;
                    }
                    // Older Explorer versions can expose only the frame handle.
                    if (frameMatch is null)
                    {
                        frameMatch = shellBrowser;
                        browser = null;
                    }
                    else ambiguous = true;
                }
                catch (COMException) { /* The Explorer frame or tab closed during enumeration. */ }
                finally { Release(browser); }
            }
            return !ambiguous && frameMatch is not null ? SelectedPath(frameMatch) : null;
        }
        finally { Release(frameMatch); }
    }

    private static string? SelectedPath(IShellBrowserDispatch browser)
    {
        object? document = null;
        object? items = null;
        object? item = null;
        try
        {
            document = browser.Document;
            if (document is not IFolderViewDispatch folderView) return null;
            items = folderView.SelectedItems();
            if (items is not IFolderItems selected || selected.Count != 1) return null;
            item = selected.Item(0);
            var path = (item as IFolderItem)?.Path;
            return path is not null && Path.IsPathFullyQualified(path) ? path : null;
        }
        finally { Release(item); Release(items); Release(document); }
    }

    private static nint BrowserWindow(object browser)
    {
        if (browser is not IComServiceProvider services) return 0;
        if (services.QueryService(in TopLevelBrowser, in OleWindow, out var service) != 0 || service is null) return 0;
        try { return service.GetWindow(out var window) == 0 ? window : 0; }
        finally { Release(service); }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    private static string ClassName(nint window)
    {
        if (window == 0) return "";
        var value = new StringBuilder(64);
        return GetClassName(window, value, value.Capacity) != 0 ? value.ToString() : "";
    }

    // Only the dispatch members read by Preview are declared, using the Windows Shell type-library IDs.
    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IShellWindows
    {
        [DispId(0x60020000)] int Count { get; }
        [DispId(0)] [return: MarshalAs(UnmanagedType.IDispatch)] object? Item([MarshalAs(UnmanagedType.Struct)] object index);
        [DispId(0x60020008)] [return: MarshalAs(UnmanagedType.IDispatch)] object? FindWindowSW(
            [In, MarshalAs(UnmanagedType.Struct)] ref object location, [In, MarshalAs(UnmanagedType.Struct)] ref object root,
            int windowClass, out int window, int options);
    }

    [ComImport, Guid("0002DF05-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IShellBrowserDispatch
    {
        [DispId(-515)] long HWND { get; }
        [DispId(203)] object? Document { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
    }

    [ComImport, Guid("E7A1AF80-4D96-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IFolderViewDispatch
    {
        [DispId(0x60020003)] [return: MarshalAs(UnmanagedType.IDispatch)] object? SelectedItems();
    }

    [ComImport, Guid("744129E0-CBE5-11CE-8350-444553540000"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IFolderItems
    {
        [DispId(0x60020000)] int Count { get; }
        [DispId(0x60020003)] [return: MarshalAs(UnmanagedType.IDispatch)] object? Item([MarshalAs(UnmanagedType.Struct)] object index);
    }

    [ComImport, Guid("FAC32C80-CBE4-11CE-8350-444553540000"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IFolderItem
    {
        [DispId(0x60020004)] string? Path { [return: MarshalAs(UnmanagedType.BStr)] get; }
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IComServiceProvider
    {
        [PreserveSig] int QueryService(in Guid service, in Guid iid, [MarshalAs(UnmanagedType.Interface)] out IOleWindow? result);
    }

    [ComImport, Guid("00000114-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOleWindow
    {
        [PreserveSig] int GetWindow(out nint hwnd);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")]
    private static extern nint GetParent(nint window);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder name, int size);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
}
