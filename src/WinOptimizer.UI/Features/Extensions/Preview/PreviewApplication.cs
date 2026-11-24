using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Extensions;
using WinOptimizer.Infrastructure.Features.Common.Services;
using WinOptimizer.Infrastructure.Features.Extensions;
using WinOptimizer.UI.Features.Common.Resources;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace WinOptimizer.UI.Features.Extensions.Preview;

/// <summary>Listens for desktop selections and reuses a single popup for file previews.</summary>
internal sealed class PreviewApplication : CompanionApplication
{
    private readonly FilePreviewLoader _loader = new();
    private static readonly SizeInt32 CompactSize = new(660, PreviewSurface.InitialHeight);
    private App? _app;
    private PreviewPopup? _popup;
    private PreviewSpaceInput? _input;
    private Guid? _initialSettingsRequest;
    private ExplorerPreviewSelection.FocusedView? _pendingSelection;
    private bool _resolvingSelection;

    private sealed record PreviewPopup(Window Window, PreviewSurface Surface, TintedDesktopBackdrop? Backdrop);

    /// <summary>Routes settings to the resident instance and starts it only when enabling the shortcut.</summary>
    internal void RunSettings(string[] args, Guid connection, string? startLanguage)
    {
        _initialSettingsRequest = connection;
        if (!TryRouteSettings(DesktopExtensionProcess.Preview, args, startLanguage))
            PreviewExtensionSettings.ReceiveAsync(connection).GetAwaiter().GetResult();
    }

    protected override void OnLaunched(App app, string? language)
    {
        _app = app;
        app.Resources.MergedDictionaries.Add(new AppColorResources());
        var localization = new LocalizationService(new FileSystemService());
        if (language is not null) localization.SetLanguage(language);
        var popup = _popup = CreatePreview(localization);
        popup.Window.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (ReferenceEquals(_popup, popup)) popup.Surface.Measure(new Size(CompactSize.Width, CompactSize.Height));
        });
        if (_initialSettingsRequest is { } connection) ApplySettings(connection);
        else SetActive(true);
    }

    protected override void Activate(AppActivationArguments arguments)
    {
        if (arguments.Data is ILaunchActivatedEventArgs launch &&
            DesktopExtensionSettingsChannel.TryParseRedirectedWorker(launch.Arguments, out var connection))
            ApplySettings(connection);
    }

    private async void ApplySettings(Guid connection)
    {
        try
        {
            await PreviewExtensionSettings.ReceiveAsync(connection, () => _input is not null, SetActive, Shutdown,
                opacity => { if (_popup?.Backdrop is { } backdrop) backdrop.TintOpacity = NormalizeOpacity(opacity); });
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
        { Debug.WriteLine(ex); }
    }

    /// <summary>Registers Raw Input on the popup's HWND and clears the current preview when disabled.</summary>
    private void SetActive(bool enabled)
    {
        if (!enabled)
        {
            _pendingSelection = null;
            _input?.Dispose();
            _input = null;
            DismissPreview();
            return;
        }
        var window = _popup?.Window ?? throw new InvalidOperationException("Preview is shutting down.");
        _input ??= new PreviewSpaceInput(WindowNative.GetWindowHandle(window), window.DispatcherQueue, SpacePressed);
    }

    /// <summary>Keeps Shell COM off the window thread and processes the latest shortcut if presses overlap.</summary>
    private async void SpacePressed(ExplorerPreviewSelection.FocusedView view)
    {
        _pendingSelection = view;
        if (_resolvingSelection) return;
        _resolvingSelection = true;
        try
        {
            while (_input is { } listener && _pendingSelection is { } selection)
            {
                _pendingSelection = null;
                try
                {
                    var path = await Task.Run(() => ExplorerPreviewSelection.ReadSingleItem(selection));
                    if (path is not null && _pendingSelection is null && ReferenceEquals(_input, listener) &&
                        ExplorerPreviewSelection.Capture() == selection)
                        Open(path, selection);
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException)
                { Trace.TraceError(ex.ToString()); }
            }
        }
        finally { _resolvingSelection = false; }
    }

    private async void Open(string path, ExplorerPreviewSelection.FocusedView view)
    {
        if (_popup is not { Window: var window, Surface: var surface }) return;
        var ct = surface.Begin(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)));
        DesktopPopupPlacement.Center(window, view, CompactSize);
        try
        {
            var content = _loader.ReadAsync(path, ct);
            ShowPreviewWindow(window);
            var item = await content;
            ct.ThrowIfCancellationRequested();
            await surface.PresentAsync(item, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or COMException
            or ArgumentException or BadImageFormatException or System.Xml.XmlException)
        {
            if (!ct.IsCancellationRequested)
            {
                Trace.TraceWarning(ex.Message);
                surface.FinishLoading();
            }
        }
    }

    /// <summary>Activates the XAML window and shows the resident popup above standard windows.</summary>
    internal static void ShowPreviewWindow(Window window)
    {
        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = true;
        window.Activate();
        window.AppWindow.Show();
    }

    private PreviewPopup CreatePreview(ILocalizationService localization)
    {
        var window = new Window { Title = localization.GetString("QuickPreview_Title") };
        var surface = new PreviewSurface(localization, new ProcessExecutor(), DismissPreview,
            current => DesktopPopupPlacement.Resize(window, current.MeasureClientHeight));
        window.Content = surface;
        TintedDesktopBackdrop? backdrop = null;
        if (DesktopAcrylicController.IsSupported())
        {
            backdrop = CreateBackdrop(PreviewExtensionSettings.ReadBackgroundOpacity());
            window.SystemBackdrop = backdrop;
            surface.RevealBackdrop();
        }
        window.ExtendsContentIntoTitleBar = true;
        window.SetTitleBar(surface.DragRegion);
        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.SetBorderAndTitleBar(true, false);
        window.AppWindow.IsShownInSwitchers = false;
        window.Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated) surface.FocusClose();
        };
        window.AppWindow.Closing += (_, args) =>
        {
            if (!ReferenceEquals(_popup?.Window, window)) return;
            args.Cancel = true;
            DismissPreview();
        };
        return new(window, surface, backdrop);
    }

    /// <summary>Uses the Preview palette and its saved opacity scale for the shared acrylic material.</summary>
    internal static TintedDesktopBackdrop CreateBackdrop(int opacity) => new(NormalizeOpacity(opacity), dark =>
    {
        byte shade = dark ? (byte)32 : (byte)249;
        return Windows.UI.Color.FromArgb(255, shade, shade, shade);
    });

    private static float NormalizeOpacity(int opacity) => (float)opacity / PreviewExtensionSettings.MaximumOpacity;

    private void DismissPreview()
    {
        if (_popup is not { } preview) return;
        preview.Window.AppWindow.Hide();
        preview.Surface.Clear();
    }

    /// <summary>Releases the shortcut and popup before the companion process exits.</summary>
    private void Shutdown()
    {
        SetActive(false);
        var popup = _popup;
        _popup = null;
        popup?.Window.Close();
        _app?.Exit();
    }
}
