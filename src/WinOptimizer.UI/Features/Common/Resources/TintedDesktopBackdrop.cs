using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace WinOptimizer.UI.Features.Common.Resources;

/// <summary>Applies themed desktop acrylic behind a XAML window and owns its native controller.</summary>
internal sealed partial class TintedDesktopBackdrop(float tintOpacity, Func<bool, Color> tintForTheme) : SystemBackdrop
{
    private DesktopAcrylicController? _controller;
    private float _tintOpacity = tintOpacity;

    internal float TintOpacity
    {
        get => _tintOpacity;
        set
        {
            _tintOpacity = value;
            if (_controller is { } controller) controller.TintOpacity = value;
        }
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        var configuration = GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot);
        _controller = new DesktopAcrylicController { TintOpacity = TintOpacity, LuminosityOpacity = 0 };
        ApplyTheme(configuration, xamlRoot);
        _controller.SetSystemBackdropConfiguration(configuration);
        _controller.AddSystemBackdropTarget(connectedTarget);
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnDefaultSystemBackdropConfigurationChanged(target, xamlRoot);
        ApplyTheme(GetDefaultSystemBackdropConfiguration(target, xamlRoot), xamlRoot);
    }

    private void ApplyTheme(SystemBackdropConfiguration configuration, XamlRoot xamlRoot)
    {
        if (_controller is not { } controller) return;
        bool dark = configuration.Theme == SystemBackdropTheme.Dark ||
            configuration.Theme == SystemBackdropTheme.Default && xamlRoot.Content is FrameworkElement { ActualTheme: ElementTheme.Dark };
        var tint = tintForTheme(dark);
        controller.TintColor = tint;
        controller.FallbackColor = configuration.IsHighContrast
            ? new UISettings().GetColorValue(UIColorType.Background) : tint;
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        _controller?.Dispose();
        _controller = null;
        base.OnTargetDisconnected(disconnectedTarget);
    }
}
