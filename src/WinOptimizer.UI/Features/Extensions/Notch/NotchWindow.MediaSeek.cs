using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using WinOptimizer.Infrastructure.Features.Extensions;

namespace WinOptimizer.UI.Features.Extensions.Notch;

public sealed partial class NotchWindow
{
    private readonly DispatcherQueueTimer _seekTimer;
    private DesktopMediaState? _mediaSnapshot;
    private DesktopMediaState? _seekAnchor;
    private uint? _seekPointer;
    private bool _renderingTimeline;

    /// <summary>Refreshes timeline bounds and preserves a scrub preview for the same track.</summary>
    private void RenderMediaTimeline(DesktopMediaState? state)
    {
        var bounds = state?.SeekRange;
        if (_seekAnchor is { } pending && (bounds is null || pending.TrackRevision != state?.TrackRevision))
            CancelMediaSeek();

        _renderingTimeline = true;
        try
        {
            TrackSeek.Visibility = VisibilityFor(bounds.HasValue);
            Progress.Visibility = VisibilityFor(!bounds.HasValue);
            if (state is not { Duration.TotalSeconds: > 0 })
            {
                PositionText.Text = DurationText.Text = "";
                Progress.Value = 0;
                return;
            }

            DurationText.Text = FormatTime(state.Duration);
            if (bounds is { } range)
            {
                TrackSeek.Maximum = range.Maximum.TotalSeconds;
                TrackSeek.Minimum = range.Minimum.TotalSeconds;
                if (_seekAnchor is null)
                    TrackSeek.Value = Math.Clamp(state.Position.TotalSeconds, TrackSeek.Minimum, TrackSeek.Maximum);
                PositionText.Text = FormatTime(_seekAnchor is null ? state.Position : TimeSpan.FromSeconds(TrackSeek.Value));
            }
            else
            {
                PositionText.Text = FormatTime(state.Position);
                Progress.Value = Math.Clamp(state.Position.TotalSeconds / state.Duration.TotalSeconds * 100, 0, 100);
            }
        }
        finally { _renderingTimeline = false; }
    }

    private void OnMediaSeekPressed(object sender, PointerRoutedEventArgs args)
    {
        if (_seekPointer.HasValue || !ShowsMedia || _mediaSnapshot?.SeekRange is null) return;
        _seekPointer = args.Pointer.PointerId;
        _seekAnchor ??= _mediaSnapshot;
        _seekTimer.Stop();
    }

    private void OnMediaSeekChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (_renderingTimeline || _closed || !ShowsMedia || _mediaSnapshot?.SeekRange is null ||
            (_seekPointer.HasValue && _seekAnchor is null)) return;
        _seekAnchor ??= _mediaSnapshot;
        PositionText.Text = FormatTime(TimeSpan.FromSeconds(args.NewValue));
        if (!_seekPointer.HasValue) QueueMediaSeek();
    }

    private void OnMediaSeekFinished(object sender, PointerRoutedEventArgs args)
    {
        if (_seekPointer != args.Pointer.PointerId) return;
        _seekPointer = null;
        if (_seekAnchor is not null) QueueMediaSeek();
    }

    private void OnMediaSeekCanceled(object sender, PointerRoutedEventArgs args)
    {
        if (_seekPointer != args.Pointer.PointerId) return;
        _seekPointer = null;
        CancelMediaSeek();
        RenderMediaTimeline(_mediaSnapshot);
    }

    private void QueueMediaSeek()
    {
        _seekTimer.Stop();
        _seekTimer.Start();
    }

    /// <summary>Commits one input adjustment after pointer release or a burst of keyboard input.</summary>
    private async void CommitMediaSeek(DispatcherQueueTimer sender, object args)
    {
        if (_closed || !ShowsMedia || _seekPointer.HasValue || _seekAnchor is not { } anchor) return;
        if (_reading || _commandPending)
        {
            QueueMediaSeek();
            return;
        }

        var target = TrackSeek.Value;
        CancelMediaSeek();
        if (anchor.SeekRange is not { } range ||
            Math.Abs(target - Math.Clamp(anchor.Position.TotalSeconds, range.Minimum.TotalSeconds, range.Maximum.TotalSeconds)) < 0.01)
            return;
        await ExecuteMediaCommandAsync(() => _media.SeekAsync(anchor, TimeSpan.FromSeconds(target)));
    }

    private void CancelMediaSeek()
    {
        _seekTimer.Stop();
        _seekAnchor = null;
    }
}
