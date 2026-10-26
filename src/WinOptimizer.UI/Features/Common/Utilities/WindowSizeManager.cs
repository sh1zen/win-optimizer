using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using Windows.Graphics;

namespace WinOptimizer.UI.Features.Common.Utilities;

/// <summary>
/// Manages window size and position persistence for WinUI 3.
/// Saves/restores window bounds and maximized state via user preferences.
/// </summary>
public class WindowSizeManager
{
    private readonly AppWindow _appWindow;
    private readonly IUserPreferencesService _userPreferencesService;
    private readonly ILogService _logService;

    private static readonly SizeInt32 DefaultSize = new(1280, 800);

    // Restored windows need a reachable title-bar region on an active display.
    internal const int MinVisibleWidth = 120;
    internal const int MinVisibleHeight = 40;

    // Tracked "normal" bounds (WinUI 3 has no RestoreBounds equivalent)
    private int _normalX;
    private int _normalY;
    private int _normalWidth;
    private int _normalHeight;
    private bool _isTrackingBounds;
    private bool _isMaximized;

    public WindowSizeManager(AppWindow appWindow, IUserPreferencesService userPreferencesService, ILogService logService)
    {
        _appWindow = appWindow ?? throw new ArgumentNullException(nameof(appWindow));
        _userPreferencesService = userPreferencesService ?? throw new ArgumentNullException(nameof(userPreferencesService));
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
    }

    /// <summary>
    /// Initializes window size and position, restoring from preferences if available.
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            bool loaded = await LoadWindowSettingsAsync();

            if (!loaded)
            {
                _appWindow.Resize(DefaultSize);
                CenterOnScreen();
                RecordNormalBounds();
            }

            SubscribeToWindowChanges();
        }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Error, $"Error initializing window size: {ex.Message}");
            _appWindow.Resize(DefaultSize);
            CenterOnScreen();
            RecordNormalBounds();
            SubscribeToWindowChanges();
        }
    }

    /// <summary>
    /// Saves current window settings to preferences.
    /// Called by ApplicationCloseService.BeforeShutdown which properly awaits before exiting.
    /// </summary>
    public async Task SaveWindowSettingsAsync()
    {
        try
        {
            var presenter = _appWindow.Presenter as OverlappedPresenter;
            var state = presenter?.State ?? OverlappedPresenterState.Restored;

            // Minimized native coordinates are unusable; retain the latest normal
            // bounds and the presentation state from before minimization.
            if (state == OverlappedPresenterState.Restored) RecordNormalBounds();
            if (state != OverlappedPresenterState.Minimized)
                _isMaximized = state == OverlappedPresenterState.Maximized;

            var saved = await _userPreferencesService.UpdatePreferencesAsync(new Dictionary<string, object>
            {
                [UserPreferenceKeys.WindowMaximized] = _isMaximized,
                [UserPreferenceKeys.WindowWidth] = (double)_normalWidth,
                [UserPreferenceKeys.WindowHeight] = (double)_normalHeight,
                [UserPreferenceKeys.WindowLeft] = (double)_normalX,
                [UserPreferenceKeys.WindowTop] = (double)_normalY
            });
            if (!saved.Success) throw new InvalidOperationException(saved.ErrorMessage ?? "Window placement could not be saved.");
        }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Error, $"Failed to save window settings: {ex.Message}");
        }
    }

    private async Task<bool> LoadWindowSettingsAsync()
    {
        try
        {
            var width = await _userPreferencesService.GetPreferenceAsync<double>(UserPreferenceKeys.WindowWidth, 0);
            var height = await _userPreferencesService.GetPreferenceAsync<double>(UserPreferenceKeys.WindowHeight, 0);
            var left = await _userPreferencesService.GetPreferenceAsync<double>(UserPreferenceKeys.WindowLeft, double.NaN);
            var top = await _userPreferencesService.GetPreferenceAsync<double>(UserPreferenceKeys.WindowTop, double.NaN);
            var isMaximized = await _userPreferencesService.GetPreferenceAsync<bool>(UserPreferenceKeys.WindowMaximized, false);

            // Only reject clearly invalid values (no saved prefs yet)
            if (!double.IsFinite(width) || !double.IsFinite(height) ||
                width < 1 || height < 1 || width > int.MaxValue || height > int.MaxValue)
                return false;

            int w = (int)width;
            int h = (int)height;

            // Restore position if valid and still visible on an active monitor.
            // If the saved rect is off-screen (e.g. secondary monitor was disconnected
            // since last close), fall back to centering on the primary display.
            if (double.IsFinite(left) && double.IsFinite(top) &&
                left >= int.MinValue && left <= int.MaxValue && top >= int.MinValue && top <= int.MaxValue)
            {
                var savedRect = new RectInt32((int)left, (int)top, w, h);

                if (IsSavedPositionVisibleOnAnyDisplay(savedRect))
                {
                    _appWindow.MoveAndResize(savedRect);
                }
                else
                {
                    _logService.Log(
                        LogLevel.Info,
                        $"Saved window position ({savedRect.X}, {savedRect.Y}) is not visible on any active display; re-centering on primary.");
                    _appWindow.Resize(new SizeInt32(w, h));
                    CenterOnScreen();
                }
            }
            else
            {
                _appWindow.Resize(new SizeInt32(w, h));
                CenterOnScreen();
            }

            // Retain the restored dimensions before maximizing changes the native bounds.
            RecordNormalBounds();
            _isMaximized = isMaximized;
            if (isMaximized)
            {
                var presenter = _appWindow.Presenter as OverlappedPresenter;
                if (presenter is not null)
                {
                    // Maximize also shows the native window. Preserve caller-owned
                    // visibility until explicit activation, including widget startup.
                    var wasVisible = _appWindow.IsVisible;
                    try { presenter.Maximize(); }
                    finally { if (!wasVisible) _appWindow.Hide(); }
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Error, $"Failed to load window settings ({ex.GetType().FullName}): {ex.Message}");
            if (ex.InnerException != null)
                _logService.Log(LogLevel.Error, $"Inner exception: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}");
            _logService.Log(LogLevel.Error, $"Stack trace: {ex.StackTrace}");
            return false;
        }
    }

    // Enumerates displays and runs the visibility check. Isolated so a failure
    // inside the WinUI DisplayArea APIs (which can throw in obscure COM-interop
    // scenarios during early startup) can't take down the whole load path —
    // if we can't enumerate displays, assume the saved position might be stale
    // and recenter, which matches the safe behavior for issue #585.
    private bool IsSavedPositionVisibleOnAnyDisplay(RectInt32 savedRect)
    {
        try
        {
            var displays = DisplayArea.FindAll();
            var workAreas = new List<RectInt32>(displays.Count);
            for (int i = 0; i < displays.Count; i++)
            {
                workAreas.Add(displays[i].WorkArea);
            }

            return IsWindowRectVisible(savedRect, workAreas);
        }
        catch (Exception ex)
        {
            _logService.Log(
                LogLevel.Warning,
                $"Could not enumerate displays to validate window position ({ex.GetType().FullName}: {ex.Message}); treating saved position as invalid and re-centering.");
            return false;
        }
    }

    private void CenterOnScreen()
    {
        try
        {
            var displayArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest);
            var workArea = displayArea.WorkArea;

            _appWindow.Move(GetCenteredPosition(_appWindow.Size, workArea));
        }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Error, $"Error centering window: {ex.Message}");
        }
    }

    /// <summary>Centers each fitting dimension and anchors oversized dimensions inside the work area's origin.</summary>
    internal static PointInt32 GetCenteredPosition(SizeInt32 size, RectInt32 workArea) => new(
        workArea.X + Math.Max(0, (workArea.Width - size.Width) / 2),
        workArea.Y + Math.Max(0, (workArea.Height - size.Height) / 2));

    private void RecordNormalBounds()
    {
        _normalX = _appWindow.Position.X;
        _normalY = _appWindow.Position.Y;
        _normalWidth = _appWindow.Size.Width;
        _normalHeight = _appWindow.Size.Height;
        _isTrackingBounds = true;
    }

    private void SubscribeToWindowChanges()
    {
        _appWindow.Changed += AppWindow_Changed;
    }

    internal static bool IsWindowRectVisible(RectInt32 windowRect, IEnumerable<RectInt32> workAreas)
    {
        if (workAreas is null)
            return false;

        foreach (var work in workAreas)
        {
            int overlapLeft = Math.Max(windowRect.X, work.X);
            int overlapTop = Math.Max(windowRect.Y, work.Y);
            int overlapRight = Math.Min(windowRect.X + windowRect.Width, work.X + work.Width);
            int overlapBottom = Math.Min(windowRect.Y + Math.Min(windowRect.Height, MinVisibleHeight), work.Y + work.Height);

            int overlapW = overlapRight - overlapLeft;
            int overlapH = overlapBottom - overlapTop;

            if (overlapW >= MinVisibleWidth && overlapH >= MinVisibleHeight)
                return true;
        }

        return false;
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!_isTrackingBounds)
            return;

        var presenter = sender.Presenter as OverlappedPresenter;
        if (presenter?.State == OverlappedPresenterState.Minimized) return;
        _isMaximized = presenter?.State == OverlappedPresenterState.Maximized;

        // Only record bounds when the window is in normal (restored) state.
        if ((args.DidPositionChange || args.DidSizeChange) &&
            presenter?.State == OverlappedPresenterState.Restored)
        {
            RecordNormalBounds();
        }
    }
}
