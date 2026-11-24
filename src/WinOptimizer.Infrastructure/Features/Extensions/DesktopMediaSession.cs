using Windows.Media.Control;
using Windows.Storage.Streams;

namespace WinOptimizer.Infrastructure.Features.Extensions;

public enum DesktopMediaCommand { Previous, PlayPause, Next }

public readonly record struct DesktopMediaSeekRange(TimeSpan Minimum, TimeSpan Maximum);

public sealed record DesktopMediaState(
    string? Title, string? Artist, IRandomAccessStreamReference? Thumbnail,
    bool IsPlaying, bool CanPlayPause, bool CanGoPrevious, bool CanGoNext,
    TimeSpan Position, TimeSpan Duration)
{
    public DesktopMediaSeekRange? SeekRange { get; init; }
    public long TrackRevision { get; init; }
    internal TimeSpan TimelineStart { get; init; }
}

public interface IDesktopMediaSession : IDisposable
{
    Task<DesktopMediaState?> ReadAsync();
    Task<bool> ControlAsync(DesktopMediaCommand command);
    Task<bool> SeekAsync(DesktopMediaState displayed, TimeSpan position);
}

/// <summary>Reads and controls the active Windows media session, caching metadata until it changes.</summary>
public sealed class DesktopMediaSession : IDesktopMediaSession
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private GlobalSystemMediaTransportControlsSessionMediaProperties? _media;
    private long _trackRevision;
    private long _loadedRevision = -1;
    private bool _disposed;

    public async Task<DesktopMediaState?> ReadAsync()
    {
        if (_disposed) return null;
        _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        if (_disposed) return null;

        var session = _manager.GetCurrentSession();
        if (session != _session)
        {
            Detach();
            _session = session;
            if (session is not null) session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            Interlocked.Increment(ref _trackRevision);
        }
        if (session is null) return null;
        var revision = Interlocked.Read(ref _trackRevision);
        if (revision != _loadedRevision)
        {
            var media = await session.TryGetMediaPropertiesAsync();
            if (_disposed || session != _manager.GetCurrentSession()) return null;
            _media = media;
            _loadedRevision = revision;
        }
        if (_disposed || session != _manager.GetCurrentSession()) return null;

        var playback = session.GetPlaybackInfo();
        var controls = playback.Controls;
        var playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        var timeline = session.GetTimelineProperties();
        var duration = timeline.EndTime - timeline.StartTime;
        var position = timeline.Position - timeline.StartTime;
        if (playing && playback.PlaybackRate is double rate && double.IsFinite(rate))
        {
            var elapsed = DateTimeOffset.UtcNow - timeline.LastUpdatedTime;
            var seconds = Math.Clamp(position.TotalSeconds + Math.Max(0, elapsed.TotalSeconds) * rate,
                0, Math.Max(0, duration.TotalSeconds));
            position = TimeSpan.FromSeconds(seconds);
        }
        return new(_media?.Title, _media?.Artist, _media?.Thumbnail, playing,
            controls.IsPlayPauseToggleEnabled || (playing ? controls.IsPauseEnabled : controls.IsPlayEnabled),
            controls.IsPreviousEnabled, controls.IsNextEnabled, position, duration)
        {
            TrackRevision = revision,
            TimelineStart = timeline.StartTime,
            SeekRange = SeekRangeFor(timeline, controls.IsPlaybackPositionEnabled)
        };
    }

    /// <summary>Sends a command only while Windows still points to the displayed session.</summary>
    public async Task<bool> ControlAsync(DesktopMediaCommand command)
    {
        if (_disposed || _session is not { } session || session != _manager?.GetCurrentSession()) return false;
        if (command == DesktopMediaCommand.Previous) return await session.TrySkipPreviousAsync();
        if (command == DesktopMediaCommand.Next) return await session.TrySkipNextAsync();

        var state = session.GetPlaybackInfo();
        if (state.Controls.IsPlayPauseToggleEnabled) return await session.TryTogglePlayPauseAsync();
        return state.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
            ? await session.TryPauseAsync() : await session.TryPlayAsync();
    }

    /// <summary>Seeks within the current player's advertised limits and rejects a stale track.</summary>
    public async Task<bool> SeekAsync(DesktopMediaState displayed, TimeSpan position)
    {
        if (_disposed || displayed.SeekRange is not { } shown || displayed.TrackRevision != Interlocked.Read(ref _trackRevision) ||
            _session is not { } session || session != _manager?.GetCurrentSession()) return false;
        var timeline = session.GetTimelineProperties();
        var current = SeekRangeFor(timeline, session.GetPlaybackInfo().Controls.IsPlaybackPositionEnabled);
        if (current is not { } allowed || displayed.TrackRevision != Interlocked.Read(ref _trackRevision)) return false;
        var requested = displayed.TimelineStart + TimeSpan.FromTicks(Math.Clamp(position.Ticks, shown.Minimum.Ticks, shown.Maximum.Ticks));
        var lower = timeline.StartTime + allowed.Minimum;
        var upper = timeline.StartTime + allowed.Maximum;
        return await session.TryChangePlaybackPositionAsync(Math.Clamp(requested.Ticks, lower.Ticks, upper.Ticks));
    }

    private static DesktopMediaSeekRange? SeekRangeFor(GlobalSystemMediaTransportControlsSessionTimelineProperties timeline, bool enabled)
    {
        if (!enabled) return null;
        var first = timeline.MinSeekTime > timeline.StartTime ? timeline.MinSeekTime : timeline.StartTime;
        var last = timeline.MaxSeekTime < timeline.EndTime ? timeline.MaxSeekTime : timeline.EndTime;
        return last > first ? new DesktopMediaSeekRange(first - timeline.StartTime, last - timeline.StartTime) : null;
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) =>
        Interlocked.Increment(ref _trackRevision);

    private void Detach()
    {
        if (_session is not null) _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
        _session = null;
        _media = null;
    }

    public void Dispose()
    {
        _disposed = true;
        Detach();
    }
}
