using CommunityToolkit.Mvvm.ComponentModel;
using FluentIcons.Common;
using WinOptimizer.Core.Features.Common.Interfaces;

namespace WinOptimizer.UI.Features.Settings.ViewModels;

public enum SettingsCategory { Appearance, Privacy, Junk, Registry, Shortcuts, Disk, Backups, Drivers, ResetAll, LogsAndHistory, Info }

/// <summary>A settings destination whose searchable labels come from its owning controls.</summary>
public sealed class SettingsCategoryItem(SettingsCategory category, string titleKey, string descriptionKey,
    string groupKey, Icon icon, ILocalizationService localization, Func<IEnumerable<string>> searchLabels) : ObservableObject
{
    public SettingsCategory Category { get; } = category;
    public string GroupKey { get; } = groupKey;
    public Icon Icon { get; } = icon;
    public string Title => localization.GetString(titleKey);
    public string Description => localization.GetString(descriptionKey);
    private string _searchDescription = "";
    public string DisplayDescription => _searchDescription.Length > 0 ? _searchDescription : Description;

    /// <summary>Searches category text and individual setting names, including discovered app names.</summary>
    public bool Matches(string query)
    {
        var labels = query.Length == 0 ? [] : searchLabels().Where(label =>
            label.Contains(query, StringComparison.CurrentCultureIgnoreCase)).Distinct().ToArray();
        _searchDescription = string.Join(" · ", labels.Take(3));
        OnPropertyChanged(string.Empty);
        return query.Length == 0 || Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            Description.Contains(query, StringComparison.CurrentCultureIgnoreCase) || labels.Length > 0;
    }
}

/// <summary>A localized group of settings destinations displayed together in the category list.</summary>
public sealed record SettingsCategoryGroup(string Title, IReadOnlyList<SettingsCategoryItem> Items);
