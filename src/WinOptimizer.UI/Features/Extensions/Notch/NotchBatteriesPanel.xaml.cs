using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Infrastructure.Features.Extensions;

namespace WinOptimizer.UI.Features.Extensions.Notch;

/// <summary>Shows one card for each measured battery and fits up to three rows in the Notch.</summary>
public sealed partial class NotchBatteriesPanel : UserControl
{
    public NotchBatteriesPanel() => InitializeComponent();

    internal bool CanScroll => CardsScroll.ScrollableHeight > 0;
    internal double PreferredHeight => Math.Clamp((BatteryCards.Items.Count + 1) / 2, 1, 3) * 75 - 7;

    internal void Publish(IReadOnlyList<DesktopBattery>? batteries, ILocalizationService localization)
    {
        var cards = NotchBatteryBadge.Create(batteries, localization, compact: false);
        if (BatteryCards.ItemsSource is not NotchBatteryBadge[] previous || !previous.SequenceEqual(cards))
            BatteryCards.ItemsSource = cards;
        EmptyMessage.Text = localization.GetString(batteries is null ? "SysMonitor_Unavailable" : "Notch_NoBatteries");
        EmptyMessage.Visibility = cards.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>Formats the same battery reports for full cards and compact badges.</summary>
internal sealed record NotchBatteryBadge(string Label, string Level, string Glyph, Visibility ChargingVisibility, string Description)
{
    internal static NotchBatteryBadge[] Create(IReadOnlyList<DesktopBattery>? batteries, ILocalizationService localization, bool compact) =>
        batteries is null ? [] : batteries.Where(battery => battery.Percent.HasValue).Select(battery =>
        {
            var name = battery.DeviceName ?? localization.GetString("Notch_PcBattery");
            var label = compact && battery.DeviceName is null ? localization.GetString("SettingGroup_Battery") : name;
            var level = $"{battery.Percent}%";
            var description = battery.IsCharging
                ? $"{name} · {level} · {localization.GetString("Utilities_BatteryCharging")}" : $"{name} · {level}";
            return new NotchBatteryBadge(label, level, battery.DeviceName is null ? "\uEBA7" : "\uE702",
                battery.IsCharging ? Visibility.Visible : Visibility.Collapsed, description);
        }).ToArray();
}
