using System.Text.Json.Serialization;

namespace WinOptimizer.UI.Features.Extensions.Notch;

[JsonConverter(typeof(JsonStringEnumConverter<NotchView>))]
internal enum NotchView { None, Overview, Media, Focus, Calendar, Shortcuts, Weather, Dashboard, Batteries, QuickSettings }

/// <summary>Owns the saved positions and replacement rules for the four Overview tiles.</summary>
internal static class NotchOverviewLayout
{
    internal const int Columns = 2;
    internal const int SlotCount = 4;

    internal static NotchView[] Default() => [NotchView.Media, NotchView.Shortcuts, NotchView.Calendar, NotchView.Weather];

    internal static bool IsTile(NotchView view) => view is not (NotchView.None or NotchView.Overview) && Enum.IsDefined(view);

    /// <summary>Preserves slot positions when restoring a layout with missing or duplicate views.</summary>
    internal static NotchView[] Normalize(NotchView[]? saved)
    {
        if (saved is null) return Default();
        var slots = new NotchView[SlotCount];
        var seen = new HashSet<NotchView>();
        for (var index = 0; index < Math.Min(saved.Length, slots.Length); index++)
            if (IsTile(saved[index]) && seen.Add(saved[index])) slots[index] = saved[index];
        return slots;
    }

    /// <summary>Moves an existing tile by swapping slots or replaces a tile with a newly selected view.</summary>
    internal static NotchView[]? Place(NotchView[] confirmed, NotchView view, int slot)
    {
        if (!IsTile(view) || (uint)slot >= SlotCount || confirmed[slot] == view) return null;
        var next = confirmed.ToArray();
        var previous = Array.IndexOf(next, view);
        if (previous >= 0) next[previous] = next[slot];
        next[slot] = view;
        return next;
    }
}
