using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinOptimizer.Core.Features.Extensions;
using WinOptimizer.UI.Features.Extensions.ViewModels;
using WinOptimizer.UI.Helpers;

namespace WinOptimizer.UI.Features.Extensions;

/// <summary>Movable compact instrument panel with an expanded view and configurable desktop stacking.</summary>
public sealed partial class SystemMonitorWindow : Window, ISystemMonitorWindow
{
    private bool _closingFromHost;
    private readonly SystemMonitorPanel _panel;
    private SizeInt32 _clientSize;
    private bool _resizeQueued;
    private bool _closed;
    private double _widgetScale = 1;
    public SystemMonitorReadingsViewModel ViewModel { get; }
    public event EventHandler? DisableRequested;
    public event EventHandler? ToggleSizeRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ClosedBySystem;

    public SystemMonitorWindow(SystemMonitorReadingsViewModel viewModel)
    {
        ViewModel = viewModel;
        // Construction owns the native window even when XAML or frame setup fails.
        try
        {
            InitializeComponent();
            _panel = new SystemMonitorPanel(viewModel);
            ScaledContent.Child = _panel;
            _panel.DisableRequested += (_, _) => DisableRequested?.Invoke(this, EventArgs.Empty);
            _panel.ToggleSizeRequested += (_, _) => ToggleSizeRequested?.Invoke(this, EventArgs.Empty);
            _panel.SettingsRequested += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
            _panel.ContentSizeChanged += (_, _) => QueueContentResize();
            AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, AppBranding.IconRelativePath));
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(_panel.DragRegion);
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.SetBorderAndTitleBar(false, false);
            }
            RemoveNativeBorder();
            // Keep the desktop widget out of taskbar previews, Task View and Alt+Tab.
            AppWindow.IsShownInSwitchers = false;
            AppWindow.Closing += (_, args) =>
            {
                if (_closingFromHost) return;
                args.Cancel = true;
                DispatcherQueue.TryEnqueue(() => DisableRequested?.Invoke(this, EventArgs.Empty));
            };
            Root.Loaded += (_, _) => ResizeToContent();
            Closed += (_, _) => { _closed = true; if (!_closingFromHost) ClosedBySystem?.Invoke(this, EventArgs.Empty); };
        }
        catch { _closingFromHost = true; Close(); throw; }
    }

    /// <summary>Applies persisted appearance immediately without restarting telemetry.</summary>
    public void ApplyOptions(SystemMonitorOptions options, ElementTheme effectiveTheme)
    {
        _widgetScale = options.ScalePercent / 100d;
        Root.RequestedTheme = options.Theme switch
        {
            SystemMonitorTheme.Light => ElementTheme.Light,
            SystemMonitorTheme.Dark => ElementTheme.Dark,
            _ => effectiveTheme
        };
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = options.AlwaysOnTop;
        ViewModel.ApplyOptions(options);
        ResizeToContent();
    }

    /// <summary>Scales the complete composition and fits its native client size to the display work area.</summary>
    private void ResizeToContent()
    {
        if (_closed) return;
        var dpiScale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
        var scale = dpiScale * _widgetScale;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min(ViewModel.IsCompact ? SystemMonitorPanel.CompactWidth : SystemMonitorPanel.ExpandedWidth, area.Width / scale);
        var content = _panel.MeasureContent(width, area.Height / scale);
        _panel.Width = width;
        _panel.Height = content.Height;
        ScaledContent.Width = width * _widgetScale;
        ScaledContent.Height = content.Height * _widgetScale;
        var size = new SizeInt32((int)Math.Ceiling(width * scale), Math.Min((int)Math.Ceiling(content.Height * scale), area.Height));
        if (size.Width != _clientSize.Width || size.Height != _clientSize.Height)
        { AppWindow.ResizeClient(size); _clientSize = size; }
        _panel.RefreshPresentation();
        KeepInWorkArea(area);
    }

    /// <summary>Coalesces post-template/font layout changes before measuring the final native client size.</summary>
    private void QueueContentResize()
    {
        if (_resizeQueued || _closed) return;
        _resizeQueued = true;
        DispatcherQueue.TryEnqueue(() => { _resizeQueued = false; ResizeToContent(); });
    }

    /// <summary>Starts near the active display's right edge; window dragging uses the native header.</summary>
    public void PlaceInitially()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Move(new PointInt32(area.X + Math.Max(0, area.Width - AppWindow.Size.Width - 20), area.Y + 20));
        KeepInWorkArea(area);
    }

    private void KeepInWorkArea(RectInt32 area) => AppWindow.Move(new PointInt32(
        Math.Clamp(AppWindow.Position.X, area.X, area.X + Math.Max(0, area.Width - AppWindow.Size.Width)),
        Math.Clamp(AppWindow.Position.Y, area.Y, area.Y + Math.Max(0, area.Height - AppWindow.Size.Height))));

    public void Publish(SystemMonitorSnapshot snapshot) { ViewModel.Publish(snapshot); ResizeToContent(); }
    public void Show() { AppWindow.Show(); Activate(); }
    public void ShowError(string error) { ViewModel.Error = error; ResizeToContent(); }
    public void RefreshLabels() { ViewModel.RefreshLabels(); ResizeToContent(); }
    public void CloseFromHost() { _closingFromHost = true; Close(); }

    /// <summary>Removes the remaining native frame and Windows 11's compositor outline.</summary>
    private void RemoveNativeBorder()
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        const int styleIndex = -16;
        const int frameStyles = 0x00C40000; // WS_BORDER, WS_DLGFRAME, WS_THICKFRAME.
        SetWindowLong(handle, styleIndex, GetWindowLong(handle, styleIndex) & ~frameStyles);
        if (!SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 0x0037)) // FRAMECHANGED, NOMOVE, NOSIZE, NOZORDER, NOACTIVATE.
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            var noBorder = 0xFFFFFFFEu; // DWMWA_COLOR_NONE.
            Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(handle, 34, ref noBorder, sizeof(uint)));
            var roundedCorners = 2u; // DWMWCP_ROUND.
            Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(handle, 33, ref roundedCorners, sizeof(uint)));
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref uint value, int size);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
}
