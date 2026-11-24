using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace WinOptimizer.UI.Helpers;

/// <summary>Fades in the incoming page on the shell frame's compositor visual.</summary>
internal sealed class PageFadeTransition(FrameworkElement host) : IDisposable
{
    private Visual? _visual;
    private ScalarKeyFrameAnimation? _fade;

    /// <summary>Plays the fade after navigation commits, replacing any previous fade.</summary>
    public void Play(bool animate)
    {
        if (!animate || !host.IsLoaded)
        {
            _visual?.StopAnimation(nameof(Visual.Opacity));
            return;
        }

        var visual = _visual ??= ElementCompositionPreview.GetElementVisual(host);
        visual.StartAnimation(nameof(Visual.Opacity), _fade ??= CreateFade(visual.Compositor));
    }

    /// <summary>Creates a reusable fade that returns to full opacity even when interrupted.</summary>
    private static ScalarKeyFrameAnimation CreateFade(Compositor compositor)
    {
        using var easeOut = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.7f), new Vector2(0.3f, 1));
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Duration = TimeSpan.FromMilliseconds(150);
        fade.StopBehavior = AnimationStopBehavior.SetToFinalValue;
        fade.InsertKeyFrame(0, 0.82f);
        fade.InsertKeyFrame(1, 1, easeOut);
        return fade;
    }

    public void Dispose()
    {
        _visual?.StopAnimation(nameof(Visual.Opacity));
        _fade?.Dispose();
        _visual = null;
        _fade = null;
    }
}
