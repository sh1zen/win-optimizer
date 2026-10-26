using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.WinUI.Collections;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.UI.Features.Common.ViewModels;
using WinOptimizer.UI.Features.Common.Interfaces;

namespace WinOptimizer.UI.Features.SoftwareApps.ViewModels;

/// <summary>Presents scan-issued leftover details and prevents selection of protected entries.</summary>
public partial class LeftoverItemViewModel(LeftoverItem item) : ObservableObject
{
    public LeftoverItem Item { get; } = item;
    public string Name => Item.Name;
    public string Kind => Item.Kind == LeftoverKind.Registration ? "REG" : Item.Kind == LeftoverKind.Folder ? "DIR" : Item.Kind.ToString().ToUpperInvariant();
    public string Location => Item.Location;
    public string Reason => Item.Reason;
    public bool CanRemove => Item.CanRemove;
    public string SizeText => Item.Kind == LeftoverKind.Registration ? "—" : $"{Item.Bytes / 1048576d:N1} MiB";
    [ObservableProperty] public partial bool IsSelected { get; set; }
    partial void OnIsSelectedChanged(bool value) { if (value && !CanRemove) IsSelected = false; }
}

/// <summary>Runs explicit leftover scans and confirmed cleanup while preserving notices, backups and cancellation outcomes.</summary>
public partial class LeftoverAppsViewModel(ILeftoverService service, ILocalizationService text, IDialogService dialogs,
    IApplicationModeService mode, ITaskProgressService tasks, IDispatcherService dispatcher, ILogService log) : BaseViewModel
{
    public ObservableCollection<LeftoverItemViewModel> Items { get; } = [];
    private AdvancedCollectionView? _itemsView;
    public AdvancedCollectionView ItemsView => _itemsView ??= CreateView();
    private AdvancedCollectionView CreateView() => new(Items, true)
    { Filter = item => item is LeftoverItemViewModel row && (string.IsNullOrWhiteSpace(SearchText) ||
        new[] { row.Name, row.Location, row.Kind, row.Reason }.Any(value => value.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase))) };

    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool HasScanned { get; set; }
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial string NoticesText { get; set; } = "";
    public bool HasSelectedItems => Items.Any(item => item.CanRemove && item.IsSelected);
    public int SelectedCount => Items.Count(item => item.CanRemove && item.IsSelected);
    public long SelectedBytes => Items.Where(item => item.CanRemove && item.IsSelected).Sum(item => item.Item.Bytes);
    /// <summary>Formats the selected file/folder total with binary units and the current number culture.</summary>
    public string SelectedSize
    {
        get
        {
            var bytes = SelectedBytes;
            return bytes switch
            {
                >= 1073741824 => $"{bytes / 1073741824d:N1} GiB",
                >= 1048576 => $"{bytes / 1048576d:N1} MiB",
                >= 1024 => $"{bytes / 1024d:N1} KiB",
                _ => $"{bytes:N0} B"
            };
        }
    }
    public string SelectionText => $"{text.GetString("Accessibility_Selected")}: {SelectedCount:N0} · {SelectedSize}";
    public bool HasNotices => !string.IsNullOrWhiteSpace(NoticesText);
    public bool CanSelect => !IsBusy;
    public bool IsAllSelected => ItemsView.Cast<LeftoverItemViewModel>().Any(item => item.CanRemove) &&
        ItemsView.Cast<LeftoverItemViewModel>().Where(item => item.CanRemove).All(item => item.IsSelected);
    public string Description => text.GetString("Leftover_Description");
    public string SelectAllText => text.GetString("Common_SelectAll");
    public string EmptyText => text.GetString(HasScanned ? "Cleaner_NoResultsDescription" : "Leftover_ScanFirst");
    public bool IsEmpty => !IsBusy && ItemsView.Count == 0;
    public event EventHandler? SelectedItemsChanged;

    partial void OnSearchTextChanged(string value) { _itemsView?.RefreshFilter(); NotifySelection(); }
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(CanSelect)); }
    partial void OnNoticesTextChanged(string value) => OnPropertyChanged(nameof(HasNotices));
    partial void OnHasScannedChanged(bool value) => OnPropertyChanged(nameof(EmptyText));

    [RelayCommand]
    public void ToggleSelectAll()
    {
        var selected = !IsAllSelected;
        foreach (var item in ItemsView.Cast<LeftoverItemViewModel>().Where(item => item.CanRemove)) item.IsSelected = selected;
        NotifySelection();
    }

    /// <summary>Scanning starts only on user demand; cached-page navigation does not launch background cleanup work.</summary>
    public async Task ScanAsync()
    {
        if (IsBusy || tasks.IsTaskRunning) return;
        IsBusy = true;
        var cancellation = tasks.StartTask(text.GetString("SoftwareApps_Tab_Leftover"), true);
        try { await ReadScanAsync(cancellation.Token); }
        catch (OperationCanceledException) { ClearItems(); StatusText = text.GetString("UninstallResidue_Cancelled"); }
        catch (Exception ex) { ClearItems(); log.LogError("Leftover scan failed", ex); StatusText = ex.Message; }
        finally { IsBusy = false; tasks.CompleteTask(); NotifySelection(); }
    }

    private async Task ReadScanAsync(CancellationToken ct)
    {
        var scan = await service.ScanAsync(ct);
        await dispatcher.RunOnUIThreadAsync(() =>
        {
            ClearItems();
            foreach (var item in scan.Items.Where(item => item.CanRemove))
            {
                var row = new LeftoverItemViewModel(item);
                row.PropertyChanged += ItemChanged;
                Items.Add(row);
            }
            HasScanned = true;
            NoticesText = string.Join(Environment.NewLine, scan.Notices);
            StatusText = text.GetString("Leftover_Count", Items.Count, Items.Count(item => item.CanRemove));
            OnPropertyChanged(nameof(IsEmpty));
            return Task.CompletedTask;
        });
    }

    public async Task RemoveSelectedAsync()
    {
        if (IsBusy || tasks.IsTaskRunning || mode.CurrentMode != WinOptimizerMode.Normal) return;
        var selected = Items.Where(item => item.IsSelected && item.CanRemove).ToArray();
        if (selected.Length == 0) return;
        var confirmation = await dialogs.ShowConfirmationAsync(new ConfirmationRequest
        {
            Title = text.GetString("SoftwareApps_Tab_Leftover"),
            Message = text.GetString("UninstallResidue_ConfirmDelete", selected.Length),
            Items = selected.Select(item => item.Name).ToList(),
        });
        if (!confirmation.Confirmed || IsBusy || mode.CurrentMode != WinOptimizerMode.Normal) return;
        IsBusy = true;
        var cancellation = tasks.StartTask(text.GetString("SoftwareApps_Tab_Leftover"), true);
        try
        {
            var result = await service.RemoveAsync(selected.Select(item => item.Item.Id).ToArray(), cancellation.Token);
            var outcome = text.GetString("UninstallResidue_Removed", result.Removed);
            await ReadScanAsync(cancellation.Token);
            StatusText = outcome;
            if (result.Errors.Count > 0) await dialogs.ShowWarningAsync(string.Join(Environment.NewLine, result.Errors), text.GetString("SoftwareApps_Tab_Leftover"));
        }
        catch (OperationCanceledException) { ClearItems(); StatusText = text.GetString("UninstallResidue_Cancelled"); }
        catch (Exception ex) { ClearItems(); log.LogError("Leftover cleanup failed", ex); StatusText = ex.Message; }
        finally { IsBusy = false; tasks.CompleteTask(); NotifySelection(); }
    }

    private void ItemChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(LeftoverItemViewModel.IsSelected)) NotifySelection(); }
    private void NotifySelection()
    {
        OnPropertyChanged(nameof(HasSelectedItems)); OnPropertyChanged(nameof(IsAllSelected)); OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(SelectedCount)); OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(SelectedSize)); OnPropertyChanged(nameof(SelectionText));
        SelectedItemsChanged?.Invoke(this, EventArgs.Empty);
    }
    private void ClearItems()
    {
        foreach (var item in Items) item.PropertyChanged -= ItemChanged;
        Items.Clear();
        HasScanned = false;
        NoticesText = "";
    }
    public void NotifyLanguageChanged()
    {
        foreach (var property in new[] { nameof(Description), nameof(SelectAllText), nameof(EmptyText), nameof(SelectedSize), nameof(SelectionText) }) OnPropertyChanged(property);
    }
    protected override void Dispose(bool disposing) { if (disposing) ClearItems(); base.Dispose(disposing); }
}
