using Windows.Foundation;

namespace WinOptimizer.UI.Features.Extensions.Notch;

/// <summary>Defines the saved positions of the compact readouts, including the shared clock and timer.</summary>
internal static class NotchCompactOrder
{
    private static readonly NotchModule[] Default =
    [
        NotchModule.Cpu, NotchModule.Gpu, NotchModule.Memory, NotchModule.Traffic, NotchModule.Batteries,
        NotchModule.AudioVisualizer, NotchModule.FrameRate, NotchModule.Clock
    ];

    /// <summary>Keeps saved positions for supported readouts and appends missing defaults.</summary>
    internal static NotchModule[] Normalize(NotchModule[] saved) =>
        saved.Where(Default.Contains).Concat(Default).Distinct().ToArray();

    internal static bool IsPinned(NotchModule slot, NotchModule pinned) =>
        (pinned & (slot == NotchModule.Clock ? NotchModule.Clock | NotchModule.Focus : slot)) != 0;

    /// <summary>Moves a slot past its target in the direction it traveled.</summary>
    internal static NotchModule[]? Move(IReadOnlyList<NotchModule> order, NotchModule source, NotchModule target)
    {
        if (source == target) return null;
        var next = order.ToList();
        int from = next.IndexOf(source), to = next.IndexOf(target);
        if (from < 0 || to < 0) return null;
        next.RemoveAt(from);
        next.Insert(to, source);
        return next.ToArray();
    }
}

/// <summary>Tracks one press against the positions the readouts had when it started.</summary>
internal sealed class NotchCompactDrag(NotchModule source, Point origin, (NotchModule Slot, double Center)[] items)
{
    private const double ClickTolerance = 6;
    private readonly double _sourceCenter = items.First(item => item.Slot == source).Center;
    public NotchModule Source { get; } = source;
    public NotchModule Target { get; private set; } = source;
    public bool CanClick { get; private set; } = true;
    public bool IsDragging { get; private set; }

    internal bool Hold()
    {
        if (!CanClick) return false;
        CanClick = false;
        IsDragging = true;
        return true;
    }

    /// <summary>Changes the drop target whenever the dragged center crosses another readout.</summary>
    internal bool Move(Point position)
    {
        double delta = position.X - origin.X;
        if (!IsDragging)
        {
            CanClick &= Math.Abs(delta) <= ClickTolerance && Math.Abs(position.Y - origin.Y) <= ClickTolerance;
            return false;
        }

        var center = _sourceCenter + delta;
        var target = Source;
        foreach (var item in items)
        {
            if (delta > 0 && item.Center > _sourceCenter && item.Center <= center) target = item.Slot;
            if (delta < 0 && item.Center < _sourceCenter && item.Center >= center)
            {
                target = item.Slot;
                break;
            }
        }
        if (target == Target) return false;
        Target = target;
        return true;
    }
}
