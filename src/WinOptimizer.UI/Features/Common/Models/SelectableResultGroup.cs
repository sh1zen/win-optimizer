using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Interfaces;

namespace WinOptimizer.UI.Features.Common.Models;

/// <summary>A collapsible category sharing the scan's result items and their selection.</summary>
public sealed class SelectableResultGroup : ObservableObject, IDisposable
{
    private readonly string _labelKey;
    private readonly ILocalizationService _localization;
    private readonly int _cleanableCount;
    private int _selectedCount;
    private bool _isExpanded;

    public IReadOnlyList<ISelectableResultItem> Items { get; }
    public string Header => $"{_localization.GetString(_labelKey)} ({Items.Count:N0})";
    public string Size { get; }
    public FluentIcons.Common.Icon Icon { get; }
    public bool CanClean => _cleanableCount > 0;
    public bool ShowSelection { get; }
    public string EmptyMessage { get; }
    public bool HasEmptyMessage => Items.Count == 0 && EmptyMessage.Length > 0;
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }

    /// <summary>Reports partial selection and applies explicit choices only to actionable items.</summary>
    public bool? IsSelected
    {
        get => _selectedCount == 0 ? false : _selectedCount == _cleanableCount ? true : null;
        set
        {
            if (value is not bool selected) return;
            foreach (var item in Items)
                if (item.CanClean) item.IsSelected = selected;
        }
    }

    public SelectableResultGroup(string labelKey, IReadOnlyList<ISelectableResultItem> items,
        string size, FluentIcons.Common.Icon icon, ILocalizationService localization, string emptyMessage = "")
    {
        Items = items;
        _labelKey = labelKey;
        _localization = localization;
        Icon = icon;
        ShowSelection = items.Any(item => item.ShowSelection);
        EmptyMessage = emptyMessage;
        foreach (var item in items)
        {
            if (item.CanClean) _cleanableCount++;
            if (item.IsSelected) _selectedCount++;
            item.PropertyChanged += OnItemSelectionChanged;
        }
        Size = size;
    }

    /// <summary>Updates the group checkbox in constant time after an individual selection changes.</summary>
    private void OnItemSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ISelectableResultItem.IsSelected) || sender is not ISelectableResultItem item) return;
        _selectedCount += item.IsSelected ? 1 : -1;
        OnPropertyChanged(nameof(IsSelected));
    }

    /// <summary>Refreshes the localized header without replacing items or expansion state.</summary>
    public void RefreshText() => OnPropertyChanged(nameof(Header));

    /// <summary>Releases the completed scan's subscriptions when results are replaced.</summary>
    public void Dispose()
    {
        foreach (var item in Items) item.PropertyChanged -= OnItemSelectionChanged;
    }
}
