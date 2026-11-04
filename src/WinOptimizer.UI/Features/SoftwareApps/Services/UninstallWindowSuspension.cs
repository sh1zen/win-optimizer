using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace WinOptimizer.UI.Features.SoftwareApps.Services;

/// <summary>Disables the shell while native uninstall UI runs and restores its previous window state.</summary>
internal sealed class UninstallWindowSuspension : IDisposable
{
    private readonly Window _window;
    private readonly HWND _handle;
    private readonly OverlappedPresenter _presenter;
    private readonly bool _wasEnabled;
    private readonly bool _wasMaximized;
    public bool IsDisposed { get; private set; }

    public UninstallWindowSuspension(Window window)
    {
        _window = window;
        _handle = new HWND(WinRT.Interop.WindowNative.GetWindowHandle(window));
        _presenter = window.AppWindow.Presenter as OverlappedPresenter
            ?? throw new InvalidOperationException("Native uninstallation requires a desktop window.");
        _wasEnabled = PInvoke.IsWindowEnabled(_handle);
        _wasMaximized = _presenter.State == OverlappedPresenterState.Maximized;
        PInvoke.EnableWindow(_handle, false);
        try { _presenter.Minimize(); }
        catch { PInvoke.EnableWindow(_handle, _wasEnabled); throw; }
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        PInvoke.EnableWindow(_handle, _wasEnabled);
        _presenter.Restore();
        if (_wasMaximized) _presenter.Maximize();
        _window.Activate();
    }
}
