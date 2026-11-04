using CommunityToolkit.Mvvm.ComponentModel;
using WinOptimizer.Core.Features.Cleaner.Models;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Models;

namespace WinOptimizer.UI.Features.Cleaner.ViewModels;

/// <summary>Reports one scan-selected category using the same finding and selection objects as cleanup.</summary>
public sealed class CleanerCategoryReport : ObservableObject
{
    private readonly IReadOnlyList<CleanerResultItem> _items;
    private readonly ILocalizationService _text;
    private int _selectedFiles;
    private int _selectedRegistryValues;
    public CleanerArea Area { get; }
    public string Title => _text.GetString($"Cleaner_{Area}");
    public FluentIcons.Common.Icon Icon => CleanerVisuals.IconFor(Area);
    public int FoundCount => _items.Count;
    public IReadOnlyList<SelectableResultGroup> ReviewGroups { get; }
    public int ProtectedCount { get; }
    public bool HasSelectableItems => ReviewGroups.Count > 0;
    public string ProtectedLabel => _text.GetString("Cleaner_ReportProtected");
    public string SelectedSize => CleanerVisuals.FormatSize(_selectedBytes);
    private long _selectedBytes;
    public int SelectedCount { get; private set; }
    public int CompletedCount { get; private set; }
    public int RemovedCount { get; private set; }
    public int FailedCount { get; private set; }
    public int UnprocessedCount => SelectedCount - CompletedCount - FailedCount;
    public long EstimatedFreedFileBytes { get; private set; }
    public long RemovedRegistryBytes { get; private set; }
    public bool HasFileSelection => _selectedFiles > 0;
    public string FreedSize => HasFileSelection ? CleanerVisuals.FormatSize(EstimatedFreedFileBytes) : "—";
    public string RegistrySize => _selectedRegistryValues > 0 ? CleanerVisuals.FormatSize(RemovedRegistryBytes) : "—";
    public string FoundLabel => _text.GetString("Cleaner_Found");
    public string SelectedLabel => _text.GetString("Cleaner_Selected");
    public string CompletedLabel => _text.GetString("Logs_Level_Success");
    public string RemovedLabel => _text.GetString("Cleaner_ReportRemoved");
    public string FailedLabel => _text.GetString("Cleaner_ReportErrors");
    public string UnprocessedLabel => _text.GetString("Cleaner_ReportUnprocessed");
    public string FreedLabel => _text.GetString("Cleaner_ReportFreed");
    public string RegistryLabel => _text.GetString("Cleaner_ReportRegistryData");

    public CleanerCategoryReport(CleanerArea area, IReadOnlyList<CleanerResultItem> items,
        IReadOnlyList<SelectableResultGroup> reviewGroups, ILocalizationService text)
    {
        Area = area; _items = items; ReviewGroups = reviewGroups; _text = text;
        ProtectedCount = items.Count(item => !item.CanClean);
        SnapshotSelection(items.Where(item => item.IsSelected).Select(item => item.Finding.Id).ToHashSet());
    }

    /// <summary>Maintains live selection counters without rescanning the category on each checkbox change.</summary>
    public void SelectionChanged(CleanerResultItem item)
    {
        var change = item.IsSelected ? 1 : -1;
        SelectedCount += change;
        _selectedBytes += change * item.Finding.Bytes;
        if (item.Finding.Target is CleanerFileTarget) _selectedFiles += change;
        if (item.Finding.Target is CleanerRegistryTarget) _selectedRegistryValues += change;
        RefreshText();
    }

    /// <summary>Freezes the exact confirmed selection, including categories with no selected resources.</summary>
    public void SnapshotSelection(IReadOnlySet<Guid> ids)
    {
        SelectedCount = _selectedFiles = _selectedRegistryValues = 0;
        _selectedBytes = 0;
        foreach (var item in _items.Where(item => ids.Contains(item.Finding.Id)))
        {
            SelectedCount++;
            _selectedBytes += item.Finding.Bytes;
            if (item.Finding.Target is CleanerFileTarget) _selectedFiles++;
            if (item.Finding.Target is CleanerRegistryTarget) _selectedRegistryValues++;
        }
        RefreshText();
    }

    /// <summary>Counts only confirmed successes and keeps registry data separate from removed file space.</summary>
    public void SetOutcomes(IEnumerable<CleanerItemResult> outcomes)
    {
        CompletedCount = RemovedCount = FailedCount = 0;
        EstimatedFreedFileBytes = RemovedRegistryBytes = 0;
        foreach (var outcome in outcomes)
        {
            if (outcome.Succeeded) CompletedCount++;
            else FailedCount++;
            if (outcome.Removed) RemovedCount++;
            EstimatedFreedFileBytes += outcome.EstimatedFreedFileBytes;
            RemovedRegistryBytes += outcome.RemovedRegistryBytes;
        }
        RefreshText();
    }

    public void RefreshText() => OnPropertyChanged(string.Empty);
}
