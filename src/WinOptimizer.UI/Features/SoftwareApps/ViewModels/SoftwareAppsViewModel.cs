using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Extensions;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.ViewModels;
using WinOptimizer.UI.Features.SoftwareApps.Models;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.UI.Features.Common.Interfaces;

namespace WinOptimizer.UI.Features.SoftwareApps.ViewModels;

public partial class SoftwareAppsViewModel : BaseViewModel
{
    private readonly ILocalizationService _localizationService;
    private readonly ILogService _logService;
    private readonly IUserPreferencesService _userPreferencesService;
    private readonly IConfigReviewModeService _configReviewModeService;
    private readonly IConfigReviewBadgeService _configReviewBadgeService;
    private readonly IApplicationModeService _applicationModeService;
    private readonly IUninstallResidueReview _uninstallResidueReview;
    private readonly IDispatcherService? _dispatcher;
    private bool _isSubscribed;

    public SoftwareAppsViewModel(
        WindowsAppsViewModel windowsAppsViewModel,
        ExternalAppsViewModel externalAppsViewModel,
        ILocalizationService localizationService,
        ILogService logService,
        IUserPreferencesService userPreferencesService,
        IConfigReviewModeService configReviewModeService,
        IConfigReviewBadgeService configReviewBadgeService,
        IApplicationModeService applicationModeService,
        IUninstallResidueReview uninstallResidueReview,
        LeftoverAppsViewModel leftoverAppsViewModel,
        IDispatcherService? dispatcher = null)
    {
        WindowsAppsViewModel = windowsAppsViewModel;
        ExternalAppsViewModel = externalAppsViewModel;
        LeftoverAppsViewModel = leftoverAppsViewModel;
        _localizationService = localizationService;
        _logService = logService;
        _userPreferencesService = userPreferencesService;
        _configReviewModeService = configReviewModeService;
        _configReviewBadgeService = configReviewBadgeService;
        _applicationModeService = applicationModeService;
        _uninstallResidueReview = uninstallResidueReview;
        _dispatcher = dispatcher;

        // Initialize partial property defaults (SearchText first since
        // tab-change handlers forward it to child ViewModels)
        SearchText = string.Empty;
        IsWindowsAppsTabSelected = true;
    }

    public WindowsAppsViewModel WindowsAppsViewModel { get; }
    public ExternalAppsViewModel ExternalAppsViewModel { get; }
    public LeftoverAppsViewModel LeftoverAppsViewModel { get; }

    [ObservableProperty]
    public partial bool IsWindowsAppsTabSelected { get; set; }

    [ObservableProperty]
    public partial bool IsExternalAppsTabSelected { get; set; }
    [ObservableProperty]
    public partial bool IsLeftoverTabSelected { get; set; }
    public bool IsLeftoverTabEnabled => !IsBuilderMode && !IsInReviewMode;
    public bool IsInventoryTabSelected => !IsLeftoverTabSelected;

    [ObservableProperty]
    public partial string SearchText { get; set; }

    [ObservableProperty]
    public partial SoftwareAppsViewMode ViewMode { get; set; } = SoftwareAppsViewMode.Card;

    public bool IsCardView => ViewMode == SoftwareAppsViewMode.Card;
    public bool IsTableView => ViewMode == SoftwareAppsViewMode.Table;
    public bool IsCompactView => ViewMode == SoftwareAppsViewMode.Compact;

    [ObservableProperty]
    public partial AppSortMode SortMode { get; set; } = AppSortMode.GroupAsc;

    public bool IsSortGroupAsc => SortMode == AppSortMode.GroupAsc;
    public bool IsSortNameAsc => SortMode == AppSortMode.NameAsc;
    public bool IsSortInstallationDate => SortMode == AppSortMode.InstallationDate;
    public bool IsSortSize => SortMode == AppSortMode.Size;
    public bool IsSortLastUsageTime => SortMode == AppSortMode.LastUsageTime;

    [ObservableProperty]
    public partial bool IsInReviewMode { get; set; }

    [ObservableProperty]
    public partial int WindowsAppsSelectedCount { get; set; }

    [ObservableProperty]
    public partial int ExternalAppsSelectedCount { get; set; }

    // Action choice properties for review mode
    [ObservableProperty]
    public partial bool IsWindowsAppsRemoveAction { get; set; }

    [ObservableProperty]
    public partial bool IsExternalAppsRemoveAction { get; set; }

    [ObservableProperty]
    public partial bool CanRemoveItems { get; set; }

    public bool IsWindowsAppsActionChosen => IsWindowsAppsRemoveAction;
    public bool IsExternalAppsActionChosen => IsExternalAppsRemoveAction;

    public bool HasWindowsAppsInConfig => _configReviewBadgeService.IsFeatureInConfig(
        FeatureIds.WindowsApps);
    public bool HasExternalAppsInConfig => _configReviewBadgeService.IsFeatureInConfig(
        FeatureIds.ExternalApps);

    /// <summary>
    /// Whether all SoftwareApps sections have been reviewed.
    /// A section is reviewed when:
    /// - It's not in the config, OR
    /// - No items are selected (user chose to apply nothing), OR
    /// - A supported action has been selected
    /// </summary>
    public bool IsSoftwareAppsReviewed
    {
        get
        {
            if (!IsInReviewMode) return false;
            bool windowsOk = !HasWindowsAppsInConfig || WindowsAppsSelectedCount == 0 || IsWindowsAppsActionChosen;
            bool externalOk = !HasExternalAppsInConfig || ExternalAppsSelectedCount == 0 || IsExternalAppsActionChosen;
            return windowsOk && externalOk;
        }
    }

    partial void OnIsWindowsAppsRemoveActionChanged(bool value)
    {
        OnPropertyChanged(nameof(IsWindowsAppsActionChosen));
        OnPropertyChanged(nameof(IsSoftwareAppsReviewed));
        OnPropertyChanged(nameof(ReviewWindowsAppsBannerText));
        OnPropertyChanged(nameof(CurrentRemoveAction));
        SyncSoftwareAppsReviewedState();
    }

    partial void OnIsExternalAppsRemoveActionChanged(bool value)
    {
        OnPropertyChanged(nameof(IsExternalAppsActionChosen));
        OnPropertyChanged(nameof(IsSoftwareAppsReviewed));
        OnPropertyChanged(nameof(ReviewExternalAppsBannerText));
        OnPropertyChanged(nameof(CurrentRemoveAction));
        SyncSoftwareAppsReviewedState();
    }

    private void SyncSoftwareAppsReviewedState()
    {
        _configReviewBadgeService.IsSoftwareAppsReviewed = IsSoftwareAppsReviewed;
        _configReviewBadgeService.NotifyBadgeStateChanged();
    }

    /// <summary>
    /// Routes to the current tab's remove action checkbox.
    /// </summary>
    public bool CurrentRemoveAction
    {
        get => IsWindowsAppsTabSelected ? IsWindowsAppsRemoveAction : IsExternalAppsRemoveAction;
        set
        {
            if (IsWindowsAppsTabSelected)
                IsWindowsAppsRemoveAction = value;
            else
                IsExternalAppsRemoveAction = value;
            OnPropertyChanged();
        }
    }

    // Localized text properties
    public string PageTitle => _localizationService.GetString("Category_SoftwareApps_Title");
    public string PageDescription => _localizationService.GetString("Category_SoftwareApps_StatusText");
    public string SearchPlaceholder => _localizationService.GetString("Common_Search_Placeholder") ?? "Search apps...";
    public string WindowsAppsTabText => _localizationService.GetString("SoftwareApps_Tab_WindowsApps");
    public string ExternalAppsTabText => _localizationService.GetString("SoftwareApps_Tab_ExternalApps");
    public string LeftoverTabText => _localizationService.GetString("SoftwareApps_Tab_Leftover");
    public string RefreshButtonText => _localizationService.GetString("Button_Refresh");
    public string UninstallBackupsText => _localizationService.GetString("UninstallResidue_Backups");
    public bool CanReviewBackups => !IsBuilderMode && !IsInReviewMode &&
        !_applicationModeService.IsSystemOperationRunning &&
        !WindowsAppsViewModel.IsTaskRunning && !ExternalAppsViewModel.IsTaskRunning && !LeftoverAppsViewModel.IsBusy;

    public string ViewModeTableTooltip => _localizationService.GetString("ViewMode_Table");
    public string ViewModeCardTooltip => _localizationService.GetString("ViewMode_Card");
    public string ViewModeCompactTooltip => _localizationService.GetString("ViewMode_Compact");

    public string SortButtonText => _localizationService.GetString("SoftwareApps_Sort_Button") ?? "Sort";
    public string SortGroupAscText => _localizationService.GetString("SoftwareApps_Sort_GroupsAZ") ?? "Groups A-Z";
    public string SortNameAscText => _localizationService.GetString("SoftwareApps_Sort_NameAZ") ?? "Name A-Z";
    public string SortInstallationDateText => _localizationService.GetString("SoftwareApps_Sort_InstallationDate") ?? "Installation date";

    public string SortSizeText => _localizationService.GetString("SoftwareApps_Sort_Size");
    public string SortLastUsageTimeText => _localizationService.GetString("SoftwareApps_Sort_LastUsageTime");

    // Table-view column header texts. Applied to the DataGrid columns in code-behind
    // (SoftwareAppsPage.LocalizeColumnHeaders) because CommunityToolkit DataGrid columns
    // live outside the page's compiled-binding tree, and re-applied on language change.
    public string ColumnHeaderName => _localizationService.GetString("SoftwareApps_Column_Name") ?? "Name";
    public string ColumnHeaderDescription => _localizationService.GetString("SoftwareApps_Column_Description") ?? "Description";

    public string ReviewWindowsAppsBannerText
    {
        get
        {
            if (IsWindowsAppsRemoveAction)
                return _localizationService.GetString("Review_Mode_Action_Remove");
            return _localizationService.GetString("Review_Mode_Select_Action");
        }
    }

    public string ReviewExternalAppsBannerText
    {
        get
        {
            if (IsExternalAppsRemoveAction)
                return _localizationService.GetString("Review_Mode_Action_Remove");
            return _localizationService.GetString("Review_Mode_Select_Action");
        }
    }

    public string RemoveButtonText => _localizationService.GetString(IsLeftoverTabSelected ? "UninstallResidue_Delete" : "SoftwareApps_Button_UninstallSelected");

    public bool IsLoading => IsLeftoverTabSelected ? LeftoverAppsViewModel.IsBusy : IsWindowsAppsTabSelected
        ? WindowsAppsViewModel.IsLoading
        : ExternalAppsViewModel.IsLoading;

    partial void OnViewModeChanged(SoftwareAppsViewMode value)
    {
        _userPreferencesService.SetPreferenceAsync("SoftwareAppsViewMode", value.ToString()).FireAndForget(_logService);
        OnPropertyChanged(nameof(IsCardView));
        OnPropertyChanged(nameof(IsTableView));
        OnPropertyChanged(nameof(IsCompactView));
    }

    partial void OnSortModeChanged(AppSortMode value)
    {
        _userPreferencesService.SetPreferenceAsync("SoftwareAppsSortMode", value.ToString()).FireAndForget(_logService);
        WindowsAppsViewModel.SortMode = value;
        ExternalAppsViewModel.SortMode = value;
        OnPropertyChanged(nameof(IsSortGroupAsc));
        OnPropertyChanged(nameof(IsSortNameAsc));
        OnPropertyChanged(nameof(IsSortInstallationDate));
        OnPropertyChanged(nameof(IsSortSize));
        OnPropertyChanged(nameof(IsSortLastUsageTime));
    }

    /// <summary>Applies a menu choice even when a column header has overridden the same selected mode.</summary>
    public void ApplySortMode(AppSortMode mode)
    {
        if (SortMode != mode)
            SortMode = mode;
        else
        {
            AppSortHelper.ApplySortDescriptions(WindowsAppsViewModel.ItemsView, mode);
            AppSortHelper.ApplySortDescriptions(ExternalAppsViewModel.ItemsView, mode);
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        if (IsLeftoverTabSelected) LeftoverAppsViewModel.SearchText = value;
        else if (IsWindowsAppsTabSelected)
        {
            WindowsAppsViewModel.SearchText = value;
        }
        else
        {
            ExternalAppsViewModel.SearchText = value;
        }
    }

    partial void OnIsWindowsAppsTabSelectedChanged(bool value)
    {
        if (value)
        {
            IsLeftoverTabSelected = false;
            IsExternalAppsTabSelected = false;
            WindowsAppsViewModel.SearchText = SearchText;
            ExternalAppsViewModel.SearchText = string.Empty;
        }
        OnPropertyChanged(nameof(RemoveButtonText));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(CurrentRemoveAction));
        UpdateButtonStates();
    }

    partial void OnIsExternalAppsTabSelectedChanged(bool value)
    {
        if (value)
        {
            IsLeftoverTabSelected = false;
            IsWindowsAppsTabSelected = false;
            ExternalAppsViewModel.SearchText = SearchText;
            WindowsAppsViewModel.SearchText = string.Empty;
        }
        OnPropertyChanged(nameof(RemoveButtonText));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(CurrentRemoveAction));
        UpdateButtonStates();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(LeftoverTabText));
        LeftoverAppsViewModel.NotifyLanguageChanged();
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageDescription));
        OnPropertyChanged(nameof(SearchPlaceholder));
        OnPropertyChanged(nameof(WindowsAppsTabText));
        OnPropertyChanged(nameof(ExternalAppsTabText));
        OnPropertyChanged(nameof(ExternalAppsTabLockedTooltip));
        OnPropertyChanged(nameof(BuilderWindowsAppsBannerText));
        OnPropertyChanged(nameof(BuilderExternalAppsBannerText));
        OnPropertyChanged(nameof(RemoveButtonText));
        OnPropertyChanged(nameof(RefreshButtonText));
        OnPropertyChanged(nameof(UninstallBackupsText));
        OnPropertyChanged(nameof(ViewModeTableTooltip));
        OnPropertyChanged(nameof(ViewModeCardTooltip));
        OnPropertyChanged(nameof(ViewModeCompactTooltip));
        OnPropertyChanged(nameof(SortButtonText));
        OnPropertyChanged(nameof(SortGroupAscText));
        OnPropertyChanged(nameof(SortNameAscText));
        OnPropertyChanged(nameof(SortInstallationDateText));
        OnPropertyChanged(nameof(SortSizeText));
        OnPropertyChanged(nameof(SortLastUsageTimeText));
        OnPropertyChanged(nameof(ColumnHeaderName));
        OnPropertyChanged(nameof(ColumnHeaderDescription));
        OnPropertyChanged(nameof(ReviewWindowsAppsBannerText));
        OnPropertyChanged(nameof(ReviewExternalAppsBannerText));
    }

    private void ChildViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender == LeftoverAppsViewModel)
        {
            UpdateButtonStates();
            OnPropertyChanged(nameof(IsLoading));
            return;
        }
        if (e.PropertyName == nameof(WindowsAppsViewModel.HasSelectedItems) ||
            e.PropertyName == nameof(ExternalAppsViewModel.HasSelectedItems) ||
            e.PropertyName == nameof(WindowsAppsViewModel.IsTaskRunning) ||
            e.PropertyName == nameof(ExternalAppsViewModel.IsTaskRunning))
        {
            UpdateButtonStates();
        }
        else if (e.PropertyName == nameof(WindowsAppsViewModel.IsLoading) ||
                 e.PropertyName == nameof(ExternalAppsViewModel.IsLoading))
        {
            OnPropertyChanged(nameof(IsLoading));
        }
    }

    private void ChildViewModel_SelectedItemsChanged(object? sender, EventArgs e)
    {
        UpdateButtonStates();
        UpdateSelectedCounts();
    }

    private void OnReviewModeChanged(object? sender, EventArgs e)
    {
        IsInReviewMode = _configReviewModeService.IsInReviewMode;
        OnPropertyChanged(nameof(IsLeftoverTabEnabled));
        if (IsInReviewMode && IsLeftoverTabSelected) IsWindowsAppsTabSelected = true;

        if (!IsInReviewMode)
        {
            // Reset action choices when exiting review mode
            IsWindowsAppsRemoveAction = false;
            IsExternalAppsRemoveAction = false;
        }

        UpdateButtonStates();
        UpdateSelectedCounts();
    }

    private void UpdateSelectedCounts()
    {
        if (!IsInReviewMode)
        {
            WindowsAppsSelectedCount = 0;
            ExternalAppsSelectedCount = 0;
            return;
        }

        WindowsAppsSelectedCount = WindowsAppsViewModel.Items?.Count(a => a.IsSelected) ?? 0;
        ExternalAppsSelectedCount = ExternalAppsViewModel.Items?.Count(a => a.IsSelected) ?? 0;

        // Re-evaluate reviewed state since it depends on selected counts
        OnPropertyChanged(nameof(IsSoftwareAppsReviewed));
        SyncSoftwareAppsReviewedState();
    }

    // Builder mode authors a config without touching this PC, so live install/remove
    // must stay disabled — app checkboxes are serialized into the saved config instead.
    private bool IsBuilderMode => _applicationModeService.CurrentMode == WinOptimizerMode.Builder;

    /// <summary>True while the app is in Builder mode (drives the Builder info ribbons).</summary>
    public bool IsBuilderModeActive => IsBuilderMode;

    // Autounattend removes Windows apps from the image and can't install external apps
    // (those install post-setup, and that path isn't built yet) — lock the External Apps
    // tab while the Autounattend target is active.
    public bool IsExternalAppsTabLocked =>
        IsBuilderMode && _applicationModeService.CurrentBuilderTarget == BuilderTarget.Autounattend;

    public bool IsExternalAppsTabEnabled => !IsExternalAppsTabLocked;

    public string? ExternalAppsTabLockedTooltip =>
        IsExternalAppsTabLocked
            ? (_localizationService.GetString("SoftwareApps_ExternalTab_AutounattendLocked")
                ?? "External Apps in autounattend is an upcoming feature.")
            : null;

    public string BuilderWindowsAppsBannerText =>
        _applicationModeService.CurrentBuilderTarget == BuilderTarget.Autounattend
            ? (_localizationService.GetString("SoftwareApps_Builder_Banner_Autounattend_WindowsApps")
                ?? "Checked apps will be removed from the Windows image during installation.")
            : (_localizationService.GetString("SoftwareApps_Builder_Banner_WindowsAppsConfig")
                ?? "These selections are saved to the config for removal when you import.");

    public string BuilderExternalAppsBannerText =>
        _localizationService.GetString("SoftwareApps_Builder_Banner_WindowsAppsConfig")
            ?? "These selections are saved to the config for removal when you import.";

    private void OnApplicationModeChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(IsLeftoverTabEnabled));
        if (IsBuilderMode && IsLeftoverTabSelected) IsWindowsAppsTabSelected = true;
        // If the External Apps tab just got locked (Builder + Autounattend) while open,
        // move the user to Windows Apps so they aren't stranded on a disabled tab.
        if (IsExternalAppsTabLocked && IsExternalAppsTabSelected)
        {
            IsWindowsAppsTabSelected = true;
        }

        UpdateButtonStates();
        OnPropertyChanged(nameof(IsBuilderModeActive));
        OnPropertyChanged(nameof(IsExternalAppsTabLocked));
        OnPropertyChanged(nameof(IsExternalAppsTabEnabled));
        OnPropertyChanged(nameof(ExternalAppsTabLockedTooltip));
        OnPropertyChanged(nameof(BuilderWindowsAppsBannerText));
        OnPropertyChanged(nameof(BuilderExternalAppsBannerText));
    }

    private void UpdateButtonStates()
    {
        bool isAnyTaskRunning = WindowsAppsViewModel.IsTaskRunning || ExternalAppsViewModel.IsTaskRunning || LeftoverAppsViewModel.IsBusy;

        CanRemoveItems = !IsInReviewMode && !IsBuilderMode && !isAnyTaskRunning &&
            (!IsLeftoverTabSelected || !_applicationModeService.IsSystemOperationRunning) &&
            (IsLeftoverTabSelected ? LeftoverAppsViewModel.HasSelectedItems : IsWindowsAppsTabSelected ? WindowsAppsViewModel.HasSelectedItems :
                IsExternalAppsTabSelected && ExternalAppsViewModel.HasSelectedItems);

        RemoveSelectedItemsCommand.NotifyCanExecuteChanged();
        ReviewUninstallBackupsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// One-time event subscription / preference load. Idempotent — guarded by
    /// <see cref="_isSubscribed"/>. Called from each Initialize* entry point so
    /// any of them can be the first call without ordering assumptions.
    /// </summary>
    private void EnsureSubscriptions()
    {
        if (_isSubscribed) return;
        _isSubscribed = true;

        var savedViewMode = _userPreferencesService.GetPreference("SoftwareAppsViewMode", "Card");
        ViewMode = savedViewMode switch
        {
            "Compact" => SoftwareAppsViewMode.Compact,
            "Table" => SoftwareAppsViewMode.Table,
            _ => SoftwareAppsViewMode.Card,
        };

        var savedSortMode = _userPreferencesService.GetPreference("SoftwareAppsSortMode", "GroupAsc");
        SortMode = Enum.TryParse<AppSortMode>(savedSortMode, out var mode) && Enum.IsDefined(mode)
            ? mode : AppSortMode.GroupAsc;
        WindowsAppsViewModel.SortMode = SortMode;
        ExternalAppsViewModel.SortMode = SortMode;

        WindowsAppsViewModel.PropertyChanged += ChildViewModel_PropertyChanged;
        ExternalAppsViewModel.PropertyChanged += ChildViewModel_PropertyChanged;
        LeftoverAppsViewModel.PropertyChanged += ChildViewModel_PropertyChanged;
        LeftoverAppsViewModel.SelectedItemsChanged += ChildViewModel_SelectedItemsChanged;
        WindowsAppsViewModel.SelectedItemsChanged += ChildViewModel_SelectedItemsChanged;
        ExternalAppsViewModel.SelectedItemsChanged += ChildViewModel_SelectedItemsChanged;
        _localizationService.LanguageChanged += OnLanguageChanged;
        _configReviewModeService.ReviewModeChanged += OnReviewModeChanged;
        _applicationModeService.ModeChanged += OnApplicationModeChanged;
        _applicationModeService.SystemOperationChanged += OnSystemOperationChanged;

        UpdateButtonStates();
    }

    /// <summary>
    /// Loads the Windows Apps tab through its shared task. Deferred startup calls
    /// this after the first Software & Apps frame, using the tab's loading indicator.
    /// </summary>
    public async Task InitializeWindowsAppsAsync()
    {
        EnsureSubscriptions();
        if (!WindowsAppsViewModel.IsInitialized)
        {
            _logService.LogInformation("[SoftwareAppsViewModel] Loading WindowsAppsViewModel");
            await WindowsAppsViewModel.LoadAppsAndCheckInstallationStatusAsync();
        }
    }

    /// <summary>
    /// Loads only the External Apps tab. Cold-start fires this in the background
    /// (see <see cref="StartupUiCoordinator"/>); the tab's per-tab loading
    /// overlay (bound to <see cref="ExternalAppsViewModel.IsLoading"/>) covers
    /// the case where the user clicks External Apps before resolution completes.
    /// </summary>
    public async Task InitializeExternalAppsAsync()
    {
        EnsureSubscriptions();
        if (!ExternalAppsViewModel.IsInitialized)
        {
            _logService.LogInformation("[SoftwareAppsViewModel] Loading ExternalAppsViewModel");
            await ExternalAppsViewModel.LoadAppsAndCheckInstallationStatusAsync();
        }
    }

    [RelayCommand]
    public async Task InitializeAsync()
    {
        _logService.LogInformation("[SoftwareAppsViewModel] InitializeAsync started");

        try
        {
            await InitializeWindowsAppsAsync();
            await InitializeExternalAppsAsync();
            _logService.LogInformation("[SoftwareAppsViewModel] InitializeAsync completed");
        }
        catch (Exception ex)
        {
            _logService.LogError($"[SoftwareAppsViewModel] Error in InitializeAsync: {ex.Message}", ex);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemoveItems))]
    private async Task RemoveSelectedItemsAsync()
    {
        if (IsLeftoverTabSelected) await LeftoverAppsViewModel.RemoveSelectedAsync();
        else if (IsWindowsAppsTabSelected)
        {
            await WindowsAppsViewModel.RemoveAppsAsync();
        }
        else
        {
            await ExternalAppsViewModel.UninstallAppsAsync();
        }
        UpdateButtonStates();
    }

    [RelayCommand(CanExecute = nameof(CanReviewBackups))]
    private Task ReviewUninstallBackupsAsync() => CanReviewBackups ? _uninstallResidueReview.ShowBackupsAsync() : Task.CompletedTask;

    private void OnSystemOperationChanged(object? sender, EventArgs e)
    {
        if (_dispatcher != null) _dispatcher.RunOnUIThread(UpdateButtonStates);
        else UpdateButtonStates();
    }

    [RelayCommand]
    private async Task RefreshInstallationStatusAsync()
    {
        if (IsLeftoverTabSelected) await LeftoverAppsViewModel.ScanAsync();
        else if (IsWindowsAppsTabSelected)
        {
            await WindowsAppsViewModel.RefreshInstallationStatusAsync();
        }
        else
        {
            await ExternalAppsViewModel.RefreshInstallationStatusAsync();
        }
    }

    [RelayCommand]
    public void SelectWindowsAppsTab()
    {
        IsWindowsAppsTabSelected = true;
    }

    [RelayCommand]
    public void SelectExternalAppsTab()
    {
        IsExternalAppsTabSelected = true;
    }

    partial void OnIsLeftoverTabSelectedChanged(bool value)
    {
        if (value)
        {
            IsWindowsAppsTabSelected = false;
            IsExternalAppsTabSelected = false;
            LeftoverAppsViewModel.SearchText = SearchText;
        }
        OnPropertyChanged(nameof(IsInventoryTabSelected));
        OnPropertyChanged(nameof(RemoveButtonText));
        OnPropertyChanged(nameof(IsLoading));
        UpdateButtonStates();
    }

    [RelayCommand]
    public async Task SelectLeftoverTabAsync()
    {
        if (!IsLeftoverTabEnabled) return;
        EnsureSubscriptions();
        IsLeftoverTabSelected = true;
        if (!LeftoverAppsViewModel.HasScanned) await LeftoverAppsViewModel.ScanAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _localizationService.LanguageChanged -= OnLanguageChanged;
            _configReviewModeService.ReviewModeChanged -= OnReviewModeChanged;
            _applicationModeService.ModeChanged -= OnApplicationModeChanged;
            _applicationModeService.SystemOperationChanged -= OnSystemOperationChanged;
            WindowsAppsViewModel.PropertyChanged -= ChildViewModel_PropertyChanged;
            ExternalAppsViewModel.PropertyChanged -= ChildViewModel_PropertyChanged;
            WindowsAppsViewModel.SelectedItemsChanged -= ChildViewModel_SelectedItemsChanged;
            ExternalAppsViewModel.SelectedItemsChanged -= ChildViewModel_SelectedItemsChanged;
            LeftoverAppsViewModel.PropertyChanged -= ChildViewModel_PropertyChanged;
            LeftoverAppsViewModel.SelectedItemsChanged -= ChildViewModel_SelectedItemsChanged;
        }
        base.Dispose(disposing);
    }
}
