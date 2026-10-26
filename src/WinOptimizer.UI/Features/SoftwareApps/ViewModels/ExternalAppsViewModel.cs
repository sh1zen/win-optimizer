using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.WinUI.Collections;
using WinOptimizer.Core.Features.Common.Extensions;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Utils;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.ViewModels;
using WinOptimizer.UI.Features.SoftwareApps;
using WinOptimizer.UI.Features.SoftwareApps.Models;

namespace WinOptimizer.UI.Features.SoftwareApps.ViewModels;

public record AppCategory(string GroupName, string DisplayName, string IconGlyph, IReadOnlyList<AppItemViewModel> Apps);

/// <summary>
/// ViewModel for the External Apps tab in the SoftwareApps feature.
/// </summary>
public partial class ExternalAppsViewModel : BaseViewModel, IExternalAppsItemsProvider
{
    private readonly IExternalAppsService _externalAppsService;
    private readonly IAppUninstallWorkflow _uninstallWorkflow;
    private readonly ILogService _logService;
    private readonly IDialogService _dialogService;
    private readonly ILocalizationService _localizationService;
    private readonly IDispatcherService _dispatcherService;
    private readonly IThemeService _themeService;
    private readonly IAppIconResolver _iconResolver;

    public ExternalAppsViewModel(
        IExternalAppsService externalAppsService,
        IAppUninstallWorkflow uninstallWorkflow,
        ILogService logService,
        IDialogService dialogService,
        ILocalizationService localizationService,
        IDispatcherService dispatcherService,
        IThemeService themeService,
        IAppIconResolver iconResolver)
    {
        _externalAppsService = externalAppsService;
        _uninstallWorkflow = uninstallWorkflow;
        _logService = logService;
        _dialogService = dialogService;
        _localizationService = localizationService;
        _dispatcherService = dispatcherService;
        _themeService = themeService;
        _iconResolver = iconResolver;

        _localizationService.LanguageChanged += OnLanguageChanged;
        _externalAppsService.WinGetReady += OnWinGetInstalled;

        Items = new ObservableCollection<AppItemViewModel>();
        ItemsView = new AdvancedCollectionView(Items, true);
        ItemsView.Filter = FilterItem;
        AppSortHelper.ApplySortDescriptions(ItemsView, SortMode);

        // Initialize partial property defaults (after Items/ItemsView,
        // since OnSearchTextChanged uses ItemsView)
        StatusText = "Ready";
        SearchText = string.Empty;
    }

    public ObservableCollection<AppItemViewModel> Items { get; }
    public AdvancedCollectionView ItemsView { get; }

    private IReadOnlyList<AppCategory> _categories = new List<AppCategory>();
    public IReadOnlyList<AppCategory> Categories
    {
        get => _categories;
        private set => SetProperty(ref _categories, value);
    }

    private static readonly Dictionary<string, string> CategoryGlyphs = new()
    {
        ["Browsers"] = "\uE774",
        ["Compression"] = "\uE8B7",
        ["Customization Utilities"] = "\uE790",
        ["Development Apps"] = "\uE943",
        ["Document Viewers"] = "\uE8A5",
        ["File & Disk Management"] = "\uEDA2",
        ["Gaming"] = "\uE7FC",
        ["Imaging"] = "\uE722",
        ["Messaging, Email & Calendar"] = "\uE715",
        ["Multimedia (Audio & Video)"] = "\uE8B2",
        ["Online Storage & Backup"] = "\uE753",
        ["Optical Disc Tools"] = "\uE958",
        ["Other Utilities"] = "\uE74C",
        ["Privacy & Security"] = "\uE72E",
        ["Remote Access"] = "\uE8AF",
        ["Runtimes & Dependencies"] = "\uE756",
    };

    private void RebuildCategories()
    {
        var filtered = Items.Where(a => FilterItem(a)).ToList();
        var groups = filtered
            .GroupBy(a => a.GroupName)
            .Where(g => !string.IsNullOrEmpty(g.Key));

        var result = new List<AppCategory>();
        foreach (var group in groups)
        {
            var glyph = CategoryGlyphs.GetValueOrDefault(group.Key, "\uE74C");
            var displayName = group.First().CategoryDisplayName;
            result.Add(new AppCategory(group.Key, displayName, glyph, AppSortHelper.Order(group, SortMode).ToList()));
        }
        Categories = result.OrderBy(category => category.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsInitialized { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; }

    [ObservableProperty]
    public partial AppSortMode SortMode { get; set; } = AppSortMode.GroupAsc;

    partial void OnSortModeChanged(AppSortMode value)
    {
        AppSortHelper.ApplySortDescriptions(ItemsView, value);
        RebuildCategories();
    }

    public bool IsAllSelected =>
        Items.Any(a => FilterItem(a)) && Items.Where(a => FilterItem(a)).All(a => a.IsSelected);
    public bool IsAllSelectedInstalled =>
        Items.Any(a => a.IsInstalled) && Items.Where(a => a.IsInstalled).All(a => a.IsSelected);
    public bool IsAllSelectedNotInstalled =>
        Items.Any(a => !a.IsInstalled) && Items.Where(a => !a.IsInstalled).All(a => a.IsSelected);

    [RelayCommand]
    private void ToggleSelectAll()
    {
        var newValue = !IsAllSelected;
        foreach (var item in Items.Where(a => FilterItem(a)))
            item.IsSelected = newValue;
        NotifySelectionStateChanged();
    }

    [RelayCommand]
    private void ToggleSelectAllInstalled()
    {
        var newValue = !IsAllSelectedInstalled;
        foreach (var item in Items.Where(a => a.IsInstalled))
            item.IsSelected = newValue;
        NotifySelectionStateChanged();
    }

    [RelayCommand]
    private void ToggleSelectAllNotInstalled()
    {
        var newValue = !IsAllSelectedNotInstalled;
        foreach (var item in Items.Where(a => !a.IsInstalled))
            item.IsSelected = newValue;
        NotifySelectionStateChanged();
    }

    private void NotifySelectionStateChanged()
    {
        OnPropertyChanged(nameof(IsAllSelected));
        OnPropertyChanged(nameof(IsAllSelectedInstalled));
        OnPropertyChanged(nameof(IsAllSelectedNotInstalled));
        OnPropertyChanged(nameof(HasSelectedItems));
        SelectedItemsChanged?.Invoke(this, EventArgs.Empty);
    }

    public string SelectAllLabel => _localizationService.GetString("Common_SelectAll") ?? "Select All";
    public string SelectAllInstalledLabel => _localizationService.GetString("Common_SelectAll_Installed") ?? "Select All Installed";
    public string SelectAllNotInstalledLabel => _localizationService.GetString("Common_SelectAll_NotInstalled") ?? "Select All Not Installed";

    [ObservableProperty]
    public partial bool IsTaskRunning { get; set; }

    public event EventHandler? SelectedItemsChanged;

    public bool HasSelectedItems => Items.Any(a => a.IsSelected);

    partial void OnSearchTextChanged(string value)
    {
        using (ItemsView.DeferRefresh())
        {
            ItemsView.RefreshFilter();
        }
        RebuildCategories();
        OnPropertyChanged(nameof(IsAllSelected));
    }

    private bool FilterItem(object obj)
    {
        if (obj is AppItemViewModel app)
        {
            return app.IsInstalled &&
                SearchHelper.MatchesSearchTerm(SearchText, app.Name, app.Description, app.Id);
        }
        return false;
    }

    /// <summary>
    /// Loads items - alias for LoadAppsAndCheckInstallationStatusAsync for ConfigurationService compatibility.
    /// </summary>
    public Task LoadItemsAsync() => LoadAppsAndCheckInstallationStatusAsync();

    private readonly object _loadGate = new();
    private Task? _loadTask;

    /// <summary>
    /// Loads app definitions, checks installation status and resolves icons — exactly once.
    /// Idempotent: startup triggers this from two independent paths — the UI init
    /// (StartupUiCoordinator) and first-run backup-config creation (ConfigExportService,
    /// via StartupOrchestrator phase 2). Both used to sail past an `if (IsInitialized)`
    /// guard — IsInitialized is only set when the load *finishes* — so two full passes
    /// ran concurrently, clobbering the shared Items collection and racing over the
    /// icon-cache .tmp files. Now the first caller starts the load; every other caller
    /// awaits the same Task. The core runs on the UI thread with a SynchronizationContext
    /// so its continuations stay UI-thread-affine regardless of which path triggered it.
    /// </summary>
    [RelayCommand]
    public Task LoadAppsAndCheckInstallationStatusAsync()
    {
        lock (_loadGate)
        {
            return _loadTask ??= _dispatcherService.RunOnUIThreadWithContextAsync(
                LoadAppsAndCheckInstallationStatusCoreAsync);
        }
    }

    private async Task LoadAppsAndCheckInstallationStatusCoreAsync()
    {
        IsLoading = true;
        StatusText = _localizationService.GetString("Progress_LoadingExternalApps");

        try
        {
            foreach (var item in Items)
                item.Dispose();
            Items.Clear();
        }
        catch (Exception ex)
        {
            _logService.LogError("[ExternalAppsViewModel] Error loading app definitions", ex);
            StatusText = $"Error loading apps: {ex.Message}";
        }

        try
        {
            StatusText = _localizationService.GetString("Progress_CheckingInstallStatus");
            await CheckInstallationStatusAsync();
        }
        catch (Exception ex)
        {
            _logService.LogWarning($"[ExternalAppsViewModel] Install status check failed, items loaded without status: {ex.Message}");
        }

        try
        {
            NotifySelectionStateChanged();
            IsInitialized = true;
            StatusText = $"Loaded {Items.Count} items";
            RebuildCategories();
        }
        catch (Exception ex)
        {
            _logService.LogWarning($"[ExternalAppsViewModel] Error finalizing: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void LoadAppsIntoItems(IEnumerable<ItemDefinition> definitions)
    {
        foreach (var definition in definitions)
        {
            var viewModel = new AppItemViewModel(
                definition,
                _localizationService,
                _dispatcherService,
                _themeService);
            viewModel.PropertyChanged += Item_PropertyChanged;
            Items.Add(viewModel);
        }
    }

    private bool _updatingInventory;

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppItemViewModel.IsSelected))
        {
            if (!_updatingInventory) NotifySelectionStateChanged();
        }
        else if (e.PropertyName == nameof(AppItemViewModel.IsInstalled))
        {
            if (sender is AppItemViewModel { IsInstalled: false } app)
                app.IsSelected = false;
            if (_updatingInventory) return;
            ItemsView.RefreshFilter();
            RebuildCategories();
            NotifySelectionStateChanged();
        }
    }

    /// <summary>Publishes one grouped/filter/sort update for a batch of installation changes.</summary>
    private void UpdateInventoryRows(Action update)
    {
        _updatingInventory = true;
        try
        {
            using (ItemsView.DeferRefresh())
            {
                update();
                ItemsView.RefreshFilter();
                ItemsView.RefreshSorting();
            }
        }
        finally { _updatingInventory = false; }
        RebuildCategories();
        NotifySelectionStateChanged();
    }

    /// <summary>
    /// Resolves artwork from installed packages and desktop registrations, then refreshes the bound icons.
    /// Failures are logged while app loading continues with category glyphs.
    /// </summary>
    private async Task ResolveIconsAsync()
    {
        try
        {
            var definitions = Items.Select(item => item.Definition).ToList();
            // Preserve source colors and backplates for local package artwork.
            await _iconResolver.ResolveBatchAsync(definitions, applyThemeAdaptation: false);

            foreach (var item in Items)
                item.NotifyIconChanged();
        }
        catch (Exception ex)
        {
            _logService.LogWarning($"[ExternalAppsViewModel] Icon resolution failed: {ex.Message}");
        }
    }

    // Serializes the install-status re-check. It runs from the Refresh button,
    // the WinGetReady event and cached uninstall reconciliation. Overlapping
    // callers queue here so older scans cannot overwrite verified removals.
    private readonly SemaphoreSlim _statusCheckGate = new(1, 1);

    [RelayCommand]
    public async Task CheckInstallationStatusAsync()
    {
        if (_externalAppsService == null) return;

        await _statusCheckGate.WaitAsync();
        var loadingDefinitions = true;
        try
        {
            var inventory = (await _externalAppsService.GetAppsAsync()).ToList();
            await _dispatcherService.RunOnUIThreadAsync(() =>
            {
                // Keep existing view models and selections while reconciling newly installed or removed programs.
                var ids = inventory.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                using (ItemsView.DeferRefresh())
                {
                    foreach (var item in Items.Where(item => !ids.Contains(item.Id)).ToList())
                    {
                        item.PropertyChanged -= Item_PropertyChanged;
                        Items.Remove(item);
                        item.Dispose();
                    }
                    var existingIds = Items.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    LoadAppsIntoItems(inventory.Where(item => !existingIds.Contains(item.Id)));
                }
                return Task.CompletedTask;
            });
            loadingDefinitions = false;
            var definitions = Items.Select(item => item.Definition).ToList();
            var statusResults = await _externalAppsService.CheckBatchInstalledAsync(definitions);

            await _dispatcherService.RunOnUIThreadAsync(() =>
            {
                UpdateInventoryRows(() =>
                {
                    foreach (var item in Items)
                    {
                        if (statusResults.TryGetValue(item.Definition.Id, out bool isInstalled))
                        {
                            item.IsInstalled = isInstalled;
                        }
                        item.NotifyMetadataChanged();
                    }
                });
                return Task.CompletedTask;
            });
        }
        catch (Exception ex)
        {
            if (loadingDefinitions)
            {
                _logService.LogError("[ExternalAppsViewModel] Error loading app definitions", ex);
                StatusText = $"Error loading apps: {ex.Message}";
            }
            else
            {
                _logService.LogError("Error checking installation status", ex);
                StatusText = $"Error checking status: {ex.Message}";
            }
        }
        finally
        {
            _statusCheckGate.Release();
        }
        // Resolve after every inventory refresh, including newly discovered desktop programs, without blocking app loading.
        ResolveIconsAsync().FireAndForget(_logService);
    }

    [RelayCommand]
    public async Task RefreshInstallationStatusAsync()
    {
        if (!IsInitialized)
        {
            StatusText = _localizationService.GetString("Progress_WaitForInitialLoad");
            return;
        }

        IsLoading = true;
        StatusText = _localizationService.GetString("Progress_RefreshingStatus");

        try
        {
            _externalAppsService.InvalidateStatusCache();
            await CheckInstallationStatusAsync();
            StatusText = $"Refreshed status for {Items.Count} items";
        }
        catch (Exception ex)
        {
            _logService.LogError("Error refreshing status", ex);
            StatusText = $"Error refreshing: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void OnWinGetInstalled(object? sender, EventArgs e)
    {
        _dispatcherService.RunOnUIThreadAsync(async () =>
        {
            if (IsInitialized)
            {
                _logService.LogInformation("WinGet installed — refreshing External Apps installation status");
                _externalAppsService.InvalidateStatusCache();
                await CheckInstallationStatusAsync();
            }
        }).FireAndForget(_logService);
    }

    [RelayCommand]
    public async Task UninstallAppsAsync()
    {
        var selectedItems = Items.Where(a => a.IsSelected).ToList();
        if (!selectedItems.Any())
        {
            await _dialogService.ShowWarningAsync(
                "Please select at least one app for uninstallation.",
                "No Apps Selected");
            return;
        }

        var itemNames = selectedItems.Select(a => a.Name).ToList();
        var r = await _dialogService.ShowConfirmationAsync(
            AppOperationConfirmation.Build(itemNames, null, _localizationService));
        bool confirmed = r.Confirmed;
        if (!confirmed) return;

        IsTaskRunning = true;
        StatusText = _localizationService.GetString("Progress_Task_UninstallingExternalApps");

        try
        {
            var result = await _uninstallWorkflow.UninstallAsync(selectedItems.Select(item => item.Definition).ToArray(), AppUninstallKind.External,
                refreshInventory: result => _dispatcherService.RunOnUIThreadWithContextAsync(() => ApplyUninstallResultAsync(result)));
            var removed = result.Applications.Where(app => app.State == UninstallVerificationState.Removed)
                .Select(app => app.AppId).ToHashSet(StringComparer.Ordinal);
            StatusText = _localizationService.GetString("UninstallResidue_Outcome", removed.Count, selectedItems.Count);
        }
        catch (OperationCanceledException)
        {
            StatusText = _localizationService.GetString("UninstallResidue_Cancelled");
        }
        catch (Exception ex)
        {
            _logService.LogError("Error uninstalling apps", ex);
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            _externalAppsService.InvalidateStatusCache();
            ClearSelections();
            IsTaskRunning = false;
        }
    }

    /// <summary>Reconciles verified removals with the in-memory rows without querying providers or icons.</summary>
    private async Task ApplyUninstallResultAsync(AppUninstallResult result)
    {
        await _statusCheckGate.WaitAsync();
        try
        {
            var removed = result.Applications.Where(app => app.State == UninstallVerificationState.Removed)
                .Select(app => app.AppId).ToHashSet(StringComparer.Ordinal);
            UpdateInventoryRows(() =>
            {
                foreach (var item in Items.Where(item => removed.Contains(item.Id)).ToList())
                {
                    item.IsInstalled = false;
                    item.Definition.Metadata = null;
                    item.NotifyMetadataChanged();
                    if (item.Definition.RegistryUninstallKeyPath is not null)
                    {
                        item.PropertyChanged -= Item_PropertyChanged;
                        Items.Remove(item);
                        item.Dispose();
                    }
                }
            });
        }
        finally { _statusCheckGate.Release(); }
    }

    [RelayCommand]
    public void ClearSelections()
    {
        foreach (var item in Items)
        {
            item.IsSelected = false;
        }
        NotifySelectionStateChanged();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(SelectAllLabel));
        OnPropertyChanged(nameof(SelectAllInstalledLabel));
        OnPropertyChanged(nameof(SelectAllNotInstalledLabel));
        ItemsView.RefreshSorting();
        RebuildCategories();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _localizationService.LanguageChanged -= OnLanguageChanged;
            _externalAppsService.WinGetReady -= OnWinGetInstalled;
            foreach (var item in Items)
            {
                item.PropertyChanged -= Item_PropertyChanged;
                item.Dispose();
            }
            _statusCheckGate.Dispose();
        }
        base.Dispose(disposing);
    }
}
