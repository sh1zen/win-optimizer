using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.WinUI.Collections;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Extensions;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Common.Utils;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.ViewModels;
using WinOptimizer.UI.Features.SoftwareApps;
using WinOptimizer.UI.Features.SoftwareApps.Models;

namespace WinOptimizer.UI.Features.SoftwareApps.ViewModels;

/// <summary>
/// ViewModel for the Windows Apps tab in the SoftwareApps feature.
/// </summary>
public partial class WindowsAppsViewModel : BaseViewModel, IWindowsAppsItemsProvider
{
    private readonly IWindowsAppsService _windowsAppsService;
    private readonly IAppUninstallWorkflow _uninstallWorkflow;
    private readonly ILogService _logService;
    private readonly IDialogService _dialogService;
    private readonly ILocalizationService _localizationService;
    private readonly IDispatcherService _dispatcherService;
    private readonly IThemeService _themeService;
    private readonly IAppIconResolver _iconResolver;

    public WindowsAppsViewModel(
        IWindowsAppsService windowsAppsService,
        IAppUninstallWorkflow uninstallWorkflow,
        ILogService logService,
        IDialogService dialogService,
        ILocalizationService localizationService,
        IDispatcherService dispatcherService,
        IThemeService themeService,
        IAppIconResolver iconResolver)
    {
        _windowsAppsService = windowsAppsService;
        _uninstallWorkflow = uninstallWorkflow;
        _logService = logService;
        _dialogService = dialogService;
        _localizationService = localizationService;
        _dispatcherService = dispatcherService;
        _themeService = themeService;
        _iconResolver = iconResolver;

        _localizationService.LanguageChanged += OnLanguageChanged;
        _windowsAppsService.WinGetReady += OnWinGetInstalled;

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

    public IEnumerable<AppItemViewModel> WindowsAppsFiltered => AppSortHelper.Order(
        Items.Where(a =>
            a.Definition.AppxPackageName?.Length > 0 &&
            string.IsNullOrEmpty(a.Definition.CapabilityName) &&
            string.IsNullOrEmpty(a.Definition.OptionalFeatureName) &&
            FilterItem(a)),
        SortMode);

    public IEnumerable<AppItemViewModel> CapabilitiesFiltered => AppSortHelper.Order(
        Items.Where(a =>
            !string.IsNullOrEmpty(a.Definition.CapabilityName) &&
            FilterItem(a)),
        SortMode);

    public IEnumerable<AppItemViewModel> OptionalFeaturesFiltered => AppSortHelper.Order(
        Items.Where(a =>
            !string.IsNullOrEmpty(a.Definition.OptionalFeatureName) &&
            FilterItem(a)),
        SortMode);

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

    /// <summary>Includes device-retained packages without broadening the current-account installation state.</summary>
    [ObservableProperty]
    public partial bool ShowHiddenInstalledApps { get; set; }

    partial void OnShowHiddenInstalledAppsChanged(bool value)
    {
        if (!value)
        {
            foreach (var item in Items.Where(item => !item.IsInstalled))
                item.IsSelected = false;
        }
        ItemsView.RefreshFilter();
        NotifyCardViewProperties();
        NotifySelectionStateChanged();
        if (value && IsInitialized)
            RefreshInstallationStatusAsync().FireAndForget(_logService);
    }

    public string ShowHiddenInstalledAppsLabel => _localizationService.GetString("WindowsApps_ShowHiddenInstalled");
    public string ShowHiddenInstalledAppsTooltip => _localizationService.GetString("WindowsApps_ShowHiddenInstalled_Tooltip");

    partial void OnSortModeChanged(AppSortMode value)
    {
        AppSortHelper.ApplySortDescriptions(ItemsView, value);
        NotifyCardViewProperties();
    }

    public string SectionAppsHeader => _localizationService.GetString("WindowsApps_Section_Apps") ?? "Windows Apps";
    public string SectionCapabilitiesHeader => _localizationService.GetString("WindowsApps_Section_Capabilities") ?? "Windows Capabilities";
    public string SectionOptionalFeaturesHeader => _localizationService.GetString("WindowsApps_Section_OptionalFeatures") ?? "Windows Optional Features";

    public string SelectAllLabel => _localizationService.GetString("Common_SelectAll") ?? "Select All";
    public string SelectAllInstalledLabel => _localizationService.GetString("Common_SelectAll_Installed") ?? "Select All Installed";
    public string SelectAllNotInstalledLabel => _localizationService.GetString("Common_SelectAll_NotInstalled") ?? "Select All Not Installed";

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
        NotifyCardViewProperties();
        OnPropertyChanged(nameof(IsAllSelected));
    }

    private void NotifyCardViewProperties()
    {
        OnPropertyChanged(nameof(WindowsAppsFiltered));
        OnPropertyChanged(nameof(CapabilitiesFiltered));
        OnPropertyChanged(nameof(OptionalFeaturesFiltered));
    }

    private bool FilterItem(object obj)
    {
        if (obj is AppItemViewModel app)
        {
            return (app.IsInstalled || (ShowHiddenInstalledApps && app.IsRetainedOnDevice)) &&
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
    /// Loads definitions, installation status and icons once. Deferred initialization,
    /// UI navigation and first-run backup share the same task. The dispatcher keeps
    /// collection updates and continuations on the UI thread regardless of the caller.
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
        StatusText = _localizationService.GetString("Progress_LoadingWindowsApps");

        try
        {
            foreach (var item in Items)
                item.Dispose();
            Items.Clear();

            var allItems = await _windowsAppsService.GetAppsAsync();
            var apps = allItems.Where(x => x.AppxPackageName?.Length > 0 || (x.WinGetPackageId != null && x.WinGetPackageId.Any()));
            var capabilities = allItems.Where(x => !string.IsNullOrEmpty(x.CapabilityName));
            var features = allItems.Where(x => !string.IsNullOrEmpty(x.OptionalFeatureName));

            LoadAppsIntoItems(apps.Concat(capabilities).Concat(features));
        }
        catch (Exception ex)
        {
            _logService.LogError("[WindowsAppsViewModel] Error loading app definitions", ex);
            StatusText = $"Error loading apps: {ex.Message}";
        }

        // Status discovery reads package identities; icon resolution writes only
        // icon paths. Run both against the loaded definitions, retaining the UI
        // context for publication and waiting for both before declaring readiness.
        var iconTask = ResolveIconsAsync();
        try
        {
            StatusText = _localizationService.GetString("Progress_CheckingInstallStatus");
            await CheckInstallationStatusAsync();
        }
        catch (Exception ex)
        {
            _logService.LogWarning(
                $"[WindowsAppsViewModel] Install status check failed, items loaded without status ({ex.GetType().FullName}, HRESULT=0x{ex.HResult:X8}): {ex.Message}");
        }

        await iconTask;

        try
        {
            NotifySelectionStateChanged();
            IsInitialized = true;
            StatusText = $"Loaded {Items.Count} items";
            NotifyCardViewProperties();
        }
        catch (Exception ex)
        {
            _logService.LogWarning(
                $"[WindowsAppsViewModel] Error finalizing ({ex.GetType().FullName}, HRESULT=0x{ex.HResult:X8}): {ex.Message}");
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

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppItemViewModel.IsSelected))
        {
            NotifySelectionStateChanged();
        }
        else if (e.PropertyName is nameof(AppItemViewModel.IsInstalled) or nameof(AppItemViewModel.IsRetainedOnDevice))
        {
            if (sender is AppItemViewModel app && !app.IsInstalled &&
                !(ShowHiddenInstalledApps && app.IsRetainedOnDevice))
                app.IsSelected = false;
            ItemsView.RefreshFilter();
            NotifyCardViewProperties();
            NotifySelectionStateChanged();
        }
    }

    [RelayCommand]
    /// <summary>
    /// Resolves AppX icons for installed entries and notifies their ViewModels.
    /// Failures are logged and swallowed — icon resolution must never block the load flow.
    /// </summary>
    private async Task ResolveIconsAsync()
    {
        try
        {
            var definitions = Items.Select(item => item.Definition).ToList();
            // Windows Apps get theme adaptation — backplate crop + light/dark
            // variant synthesis for monochrome system icons.
            await _iconResolver.ResolveBatchAsync(definitions, applyThemeAdaptation: true);

            foreach (var item in Items)
                item.NotifyIconChanged();
        }
        catch (Exception ex)
        {
            _logService.LogWarning($"[WindowsAppsViewModel] Icon resolution failed: {ex.Message}");
        }
    }

    // Serializes the install-status re-check. It runs from the Refresh button,
    // the WinGetReady event (OnWinGetInstalled) and post-removal
    // (RefreshAfterOperationAsync — including config-apply); none are mutually
    // exclusive. Overlapping callers queue here instead of interleaving access
    // to the shared Items collection and the detection cache.
    private readonly SemaphoreSlim _statusCheckGate = new(1, 1);

    public async Task CheckInstallationStatusAsync()
    {
        if (_windowsAppsService == null) return;

        await _statusCheckGate.WaitAsync();
        try
        {
            var definitions = Items.Select(item => item.Definition).ToList();
            var statusResults = await _windowsAppsService.CheckBatchInstalledAsync(definitions);
            HashSet<string>? devicePackages = null;
            if (ShowHiddenInstalledApps)
            {
                // Failure must remain visible while preserving the independent account status results.
                try
                {
                    devicePackages = await _windowsAppsService.GetDevicePackageNamesAsync();
                }
                catch (Exception ex)
                {
                    _logService.LogError("Error checking device-retained Windows apps", ex);
                    await _dialogService.ShowWarningAsync(ex.Message, ShowHiddenInstalledAppsLabel);
                }
            }

            // NotificationDeferrer.Dispose() fires VectorChanged events the bound
            // DataGrid handles by reading its ItemsSource DependencyProperty —
            // DPs throw WinRT HRESULT off the UI thread.
            _logService.LogDebug(
                $"[WindowsAppsViewModel] CheckInstallationStatusAsync pre-dispatch HasThreadAccess={_dispatcherService.HasThreadAccess}");
            await _dispatcherService.RunOnUIThreadAsync(() =>
            {
                using (ItemsView.DeferRefresh())
                {
                    foreach (var item in Items)
                    {
                        if (statusResults.TryGetValue(item.Definition.Id, out bool isInstalled))
                        {
                            item.IsInstalled = isInstalled;
                        }
                        item.NotifyMetadataChanged();
                        item.IsRetainedOnDevice = devicePackages is not null &&
                            item.Definition.AppxPackageName?.Any(devicePackages.Contains) == true;
                    }
                }
                ItemsView.RefreshSorting();
                NotifyCardViewProperties();
                return Task.CompletedTask;
            });
        }
        catch (Exception ex)
        {
            _logService.LogError(
                $"Error checking installation status ({ex.GetType().FullName}, HRESULT=0x{ex.HResult:X8}): {ex.Message}",
                ex);
            StatusText = $"Error checking status: {ex.Message}";
        }
        finally
        {
            _statusCheckGate.Release();
        }
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
            _windowsAppsService.InvalidateStatusCache();
            await CheckInstallationStatusAsync();
            NotifyCardViewProperties();
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
                _logService.LogInformation("WinGet installed — refreshing Windows Apps installation status");
                _windowsAppsService.InvalidateStatusCache();
                await CheckInstallationStatusAsync();
                NotifyCardViewProperties();
            }
        }).FireAndForget(_logService);
    }

    /// <summary>
    /// Shows removal summary and asks for confirmation.
    /// </summary>
    /// <returns>Tuple of (Confirmed, SaveScripts).</returns>
    public async Task<(bool Confirmed, bool SaveScripts)> ShowRemovalSummaryAndConfirm()
    {
        var selectedItems = Items.Where(a => a.IsSelected).ToList();
        if (!selectedItems.Any()) return (true, true);

        var itemNames = selectedItems.Select(a => a.Name).ToList();
        var checkboxText = _localizationService.GetString("Dialog_SaveRemovalScripts");
        var r = await _dialogService.ShowConfirmationAsync(
            AppOperationConfirmation.Build(itemNames, checkboxText, _localizationService));
        bool confirmed = r.Confirmed;
        bool checkboxChecked = r.CheckboxChecked;
        return (confirmed, checkboxChecked);
    }

    /// <summary>
    /// Removes selected apps with optional confirmation skip (for ConfigurationService compatibility).
    /// </summary>
    public async Task RemoveApps(bool skipConfirmation = false, bool saveRemovalScripts = true)
    {
        var selectedItems = Items.Where(a => a.IsSelected).ToList();
        if (!selectedItems.Any()) return;

        if (!skipConfirmation)
        {
            var itemNames = selectedItems.Select(a => a.Name).ToList();
            var checkboxText = _localizationService.GetString("Dialog_SaveRemovalScripts");
            var r = await _dialogService.ShowConfirmationAsync(
                AppOperationConfirmation.Build(itemNames, checkboxText, _localizationService));
            bool confirmed = r.Confirmed;
            bool checkboxChecked = r.CheckboxChecked;
            if (!confirmed) return;
            saveRemovalScripts = checkboxChecked;
        }

        await RemoveAppsInternalAsync(selectedItems, saveRemovalScripts);
    }

    [RelayCommand]
    public async Task RemoveAppsAsync()
    {
        var selectedItems = Items.Where(a => a.IsSelected).ToList();
        if (!selectedItems.Any())
        {
            await _dialogService.ShowWarningAsync(
                "Please select at least one item for removal.",
                "No Items Selected");
            return;
        }

        var itemNames = selectedItems.Select(a => a.Name).ToList();
        var checkboxText = _localizationService.GetString("Dialog_SaveRemovalScripts");
        var r = await _dialogService.ShowConfirmationAsync(
            AppOperationConfirmation.Build(itemNames, checkboxText, _localizationService));
        bool confirmed = r.Confirmed;
        bool saveScripts = r.CheckboxChecked;
        if (!confirmed) return;

        await RemoveAppsInternalAsync(selectedItems, saveScripts);
    }

    private async Task RemoveAppsInternalAsync(List<AppItemViewModel> selectedItems, bool saveRemovalScripts = true)
    {
        IsTaskRunning = true;
        StatusText = _localizationService.GetString("Progress_Task_RemovingWindowsApps");

        try
        {
            var definitions = selectedItems.Select(a => a.Definition).ToList();
            var result = await _uninstallWorkflow.UninstallAsync(definitions, AppUninstallKind.Windows, saveRemovalScripts);
            var removed = result.Applications.Where(app => app.State == UninstallVerificationState.Removed)
                .Select(app => app.AppId).ToHashSet(StringComparer.Ordinal);
            foreach (var item in selectedItems.Where(item => removed.Contains(item.Id))) item.IsInstalled = false;
            StatusText = _localizationService.GetString("UninstallResidue_Outcome", removed.Count, selectedItems.Count);
        }
        catch (OperationCanceledException)
        {
            StatusText = _localizationService.GetString("UninstallResidue_Cancelled");
        }
        catch (Exception ex)
        {
            _logService.LogError("Error removing apps", ex);
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsTaskRunning = false;
        }

        await RefreshAfterOperationAsync();
    }

    private async Task RefreshAfterOperationAsync()
    {
        _windowsAppsService.InvalidateStatusCache();
        await CheckInstallationStatusAsync();
        NotifyCardViewProperties();
        ClearSelections();
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
        OnPropertyChanged(nameof(SectionAppsHeader));
        OnPropertyChanged(nameof(SectionCapabilitiesHeader));
        OnPropertyChanged(nameof(SectionOptionalFeaturesHeader));
        OnPropertyChanged(nameof(SelectAllLabel));
        OnPropertyChanged(nameof(SelectAllInstalledLabel));
        OnPropertyChanged(nameof(SelectAllNotInstalledLabel));
        OnPropertyChanged(nameof(ShowHiddenInstalledAppsLabel));
        OnPropertyChanged(nameof(ShowHiddenInstalledAppsTooltip));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _localizationService.LanguageChanged -= OnLanguageChanged;
            _windowsAppsService.WinGetReady -= OnWinGetInstalled;
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
