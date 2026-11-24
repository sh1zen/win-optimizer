namespace WinOptimizer.UI.Features.Extensions.Notch;

internal enum FocusPhase { Work, ShortBreak, LongBreak, Custom }

/// <summary>Owns one focus countdown, using elapsed time so delayed UI ticks cannot slow it down.</summary>
internal sealed class FocusTimer(TimeProvider clock)
{
    private static readonly TimeSpan WorkDuration = TimeSpan.FromMinutes(25);
    private TimeSpan _remaining = WorkDuration;
    private long? _startedAt;

    public FocusPhase Phase { get; private set; }
    public bool IsRunning => _startedAt.HasValue;
    public int CompletedSessions { get; private set; }
    public TimeSpan Remaining { get; private set; } = WorkDuration;
    public int CustomMinutes { get; private set; } = 5;
    public TimeSpan PhaseDuration => Phase switch
    {
        FocusPhase.ShortBreak => TimeSpan.FromMinutes(5),
        FocusPhase.LongBreak => TimeSpan.FromMinutes(15),
        FocusPhase.Custom => TimeSpan.FromMinutes(CustomMinutes),
        _ => WorkDuration
    };

    public void SetCustomDuration(int minutes)
    {
        if (minutes is < 1 or > 180 || CustomMinutes == minutes || IsRunning) return;
        CustomMinutes = minutes;
        if (Phase == FocusPhase.Custom) Reset();
    }

    public void Select(FocusPhase phase)
    {
        Update();
        if (Phase == phase) return;
        _startedAt = null;
        Phase = phase;
        Reset();
    }

    public void Reset()
    {
        Update();
        _startedAt = null;
        Remaining = _remaining = PhaseDuration;
    }

    public void Toggle()
    {
        bool wasRunning = IsRunning;
        Update();
        if (wasRunning)
        {
            _remaining = Remaining;
            _startedAt = null;
            return;
        }

        if (Remaining == TimeSpan.Zero) Reset();
        _startedAt = clock.GetTimestamp();
    }

    public void Update()
    {
        if (_startedAt is not long start) return;
        Remaining = _remaining - clock.GetElapsedTime(start);
        if (Remaining > TimeSpan.Zero) return;

        Remaining = TimeSpan.Zero;
        _startedAt = null;
        if (Phase == FocusPhase.Work) CompletedSessions++;
    }

}
