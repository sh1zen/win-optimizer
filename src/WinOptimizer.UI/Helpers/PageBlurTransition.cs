using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;
using WinOptimizer.Core.Features.Common.Interfaces;

namespace WinOptimizer.UI.Helpers;

/// <summary>Sharpens the incoming page in place using a temporary compositor overlay.</summary>
internal sealed class PageBlurTransition(FrameworkElement host, ILogService? log) : IDisposable
{
    private const string BlurAmountProperty = "Blur.BlurAmount";
    private const float InitialBlur = 10;
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(220);

    private SpriteVisual? _visual;
    private CompositionEffectBrush? _effect;
    private CompositionBackdropBrush? _backdrop;
    private CompositionScopedBatch? _batch;
    private TypedEventHandler<object, CompositionBatchCompletedEventArgs>? _completed;

    /// <summary>Starts a fresh transition after navigation has committed.</summary>
    public void Play(bool animate)
    {
        Stop();
        if (!animate || !host.IsLoaded) return;

        try
        {
            var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
            var effect = _effect ?? CreateOverlay(compositor);

            using var animation = compositor.CreateScalarKeyFrameAnimation();
            animation.Duration = Duration;
            animation.InsertKeyFrame(0, InitialBlur);
            animation.InsertKeyFrame(1, 0, compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.7f), new Vector2(0.3f, 1)));

            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            _batch = batch;
            _completed = (_, _) =>
            {
                if (_batch == batch) Stop();
            };
            batch.Completed += _completed;
            try
            {
                effect.Properties.StartAnimation(BlurAmountProperty, animation);
                ElementCompositionPreview.SetElementChildVisual(host, _visual);
            }
            finally { batch.End(); }
        }
        catch (COMException ex)
        {
            Dispose();
            log?.LogDebug($"Page blur effect unavailable: {ex.Message}");
        }
    }

    /// <summary>Creates the effect lazily and reuses its visual across page changes.</summary>
    private CompositionEffectBrush CreateOverlay(Compositor compositor)
    {
        using var blur = new GaussianBlurEffect
        {
            Name = "Blur",
            BlurAmount = InitialBlur,
            BorderMode = EffectBorderMode.Hard,
            Source = new CompositionEffectSourceParameter("Page")
        };
        using var factory = compositor.CreateEffectFactory(blur, [BlurAmountProperty]);
        _backdrop = compositor.CreateBackdropBrush();
        _effect = factory.CreateBrush();
        _effect.SetSourceParameter("Page", _backdrop);
        _visual = compositor.CreateSpriteVisual();
        _visual.RelativeSizeAdjustment = Vector2.One;
        _visual.Brush = _effect;
        return _effect;
    }

    /// <summary>Detaches finished or superseded work before the next render.</summary>
    private void Stop()
    {
        if (_batch is not { } batch) return;
        _batch = null;
        batch.Completed -= _completed;
        _completed = null;
        ElementCompositionPreview.SetElementChildVisual(host, null);
        _effect?.Properties.StopAnimation(BlurAmountProperty);
        batch.Dispose();
    }

    public void Dispose()
    {
        Stop();
        _visual?.Dispose();
        _effect?.Dispose();
        _backdrop?.Dispose();
        _visual = null;
        _effect = null;
        _backdrop = null;
    }
}
