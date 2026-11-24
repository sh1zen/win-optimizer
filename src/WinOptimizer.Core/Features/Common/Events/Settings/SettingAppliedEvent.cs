using System;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Core.Features.Common.Events.Settings;

/// <summary>
/// Publishes a committed setting value or the state observed after a partial write.
/// </summary>
public class SettingAppliedEvent : IDomainEvent
{
    public DateTime Timestamp { get; }
    public Guid EventId { get; }

    /// <summary>
    /// The ID of the setting that was applied.
    /// </summary>
    public string SettingId { get; }

    /// <summary>
    /// Whether the setting was enabled or disabled.
    /// </summary>
    public bool IsEnabled { get; }

    /// <summary>
    /// The value that was applied (for value-based settings like sliders, combo boxes).
    /// </summary>
    public object? Value { get; }

    /// <summary>System readback used when the requested change could only be applied in part.</summary>
    public SettingStateResult? ObservedState { get; }

    public SettingAppliedEvent(string settingId, bool isEnabled, object? value = null, SettingStateResult? observedState = null)
    {
        Timestamp = DateTime.UtcNow;
        EventId = Guid.NewGuid();
        SettingId = settingId ?? throw new ArgumentNullException(nameof(settingId));
        IsEnabled = isEnabled;
        Value = value;
        ObservedState = observedState;
    }
}
