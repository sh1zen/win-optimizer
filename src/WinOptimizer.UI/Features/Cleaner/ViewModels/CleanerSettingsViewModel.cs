using WinOptimizer.Application.Preferences;
using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinOptimizer.Core.Features.Cleaner.Interfaces;
using WinOptimizer.Core.Features.Cleaner.Models;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Services;

namespace WinOptimizer.UI.Features.Cleaner.ViewModels;

/// <summary>A localized, selectable category with a stable typed identity.</summary>
public sealed class CleanerChoice<T>(T value, string labelKey, ILocalizationService localization, string? displayName = null,
    string? descriptionKey = null) : ObservableObject
{
    public T Value { get; } = value;
    public string Title => displayName ?? localization.GetString(labelKey);
    public string Description => descriptionKey is null ? "" : localization.GetString(descriptionKey);
    public FluentIcons.Common.Icon Icon { get; init; }
    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    public void RefreshText() { OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(Description)); }
}

public enum CleanerSettingsSection { All, Backups, Registry, Privacy, Junk, Shortcuts, Disk }

/// <summary>Shared Cleaner preferences with ordered automatic persistence and validated scan snapshots.</summary>
public partial class CleanerSettingsViewModel : ObservableObject, IDisposable
{
    private readonly IUserPreferencesService _preferences;
    private readonly ICleanerBackend _backend;
    private readonly ILocalizationService _localization;
    private readonly ITaskProgressService _tasks;
    private readonly IExplorerWindowManager _explorer;
    private readonly ICleanerBackupService _backups;
    private readonly IDialogService _dialogs;
    private readonly IApplicationModeService _applicationModeService;
    private bool _loaded;
    private bool _applyingOptions;
    private Task? _loadTask;
    private readonly PreferenceSaveQueue<CleanerOptions> _saves;
    public bool IsLoaded => _loaded;
    public CleanerOptions Options { get; private set; } = new();
    public ObservableCollection<CleanerChoice<RegistryArea>> RegistryCategories { get; } = [];
    public ObservableCollection<CleanerChoice<string>> Applications { get; } = [];
    public ObservableCollection<CleanerChoice<string>> RegistryAccounts { get; } = [];
    public ObservableCollection<CleanerChoice<string>> PrivacyAccounts { get; } = [];
    public ObservableCollection<CleanerChoice<string>> JunkAccounts { get; } = [];
    public ObservableCollection<CleanerChoice<string>> ShortcutAccounts { get; } = [];
    private readonly HashSet<string> _currentAccountSids = new(StringComparer.Ordinal);
    private Task _applicationRefresh = Task.CompletedTask;
    private int _applicationRevision;
    private bool _disposed;
    public ObservableCollection<string> Drives { get; } = [];
    public ObservableCollection<CleanerBackupInfo> Backups { get; } = [];
    [ObservableProperty] public partial CleanerBackupInfo? SelectedBackup { get; set; }

    [ObservableProperty] public partial bool DeepClean { get; set; }
    [ObservableProperty] public partial bool BackupBeforeCleaning { get; set; } = true;
    [ObservableProperty] public partial bool BackupBeforeRegistryDefrag { get; set; } = true;
    [ObservableProperty] public partial bool RecentFiles { get; set; }
    [ObservableProperty] public partial bool BrowserCache { get; set; }
    [ObservableProperty] public partial bool BrowserHistory { get; set; }
    [ObservableProperty] public partial bool BrowserOfflineCache { get; set; }
    [ObservableProperty] public partial bool WindowsHistory { get; set; }
    [ObservableProperty] public partial bool ApplicationHistory { get; set; }
    [ObservableProperty] public partial bool ApplicationPrivacyCaches { get; set; }
    [ObservableProperty] public partial bool CertificateCache { get; set; }
    [ObservableProperty] public partial bool JumpLists { get; set; }
    [ObservableProperty] public partial bool DefenderLogs { get; set; }
    [ObservableProperty] public partial bool UserTemp { get; set; }
    [ObservableProperty] public partial bool WindowsTemp { get; set; }
    [ObservableProperty] public partial bool WindowsLogs { get; set; }
    [ObservableProperty] public partial bool ShaderCache { get; set; }
    [ObservableProperty] public partial bool ThumbnailCache { get; set; }
    [ObservableProperty] public partial bool ErrorReports { get; set; }
    [ObservableProperty] public partial bool CrashDumps { get; set; }
    [ObservableProperty] public partial bool InternetCache { get; set; }
    [ObservableProperty] public partial bool DesktopShortcuts { get; set; }
    [ObservableProperty] public partial bool StartMenuShortcuts { get; set; }
    [ObservableProperty] public partial bool RecentShortcuts { get; set; }
    [ObservableProperty] public partial double MinimumAgeDays { get; set; } = 7;
    [ObservableProperty] public partial string DiskRoot { get; set; } = "C:\\";
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial string ApplicationStatus { get; private set; } = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsIdle), nameof(CanEdit))] public partial bool IsBusy { get; set; }
    public bool IsIdle => !IsBusy;
    public bool CanEdit => IsLoaded && IsIdle;

    public string DeepLabel => Text("Cleaner_DeepClean");
    public string RegistryAccountsLabel => Text("Cleaner_RegistryAccounts");
    public string RegistryAccountsDescription => Text("Cleaner_RegistryAccountsDescription");
    public string AccountsDescription(CleanerSettingsSection section) => section == CleanerSettingsSection.Registry
        ? RegistryAccountsDescription : Text("Cleaner_AccountsDescription");

    /// <summary>Returns the category's own selection; each checkbox belongs to exactly one persisted scope.</summary>
    public ObservableCollection<CleanerChoice<string>>? AccountChoices(CleanerSettingsSection section) => section switch
    {
        CleanerSettingsSection.Registry => RegistryAccounts,
        CleanerSettingsSection.Privacy => PrivacyAccounts,
        CleanerSettingsSection.Junk => JunkAccounts,
        CleanerSettingsSection.Shortcuts => ShortcutAccounts,
        _ => null
    };

    private IEnumerable<(ObservableCollection<CleanerChoice<string>> Choices, string[]? Selected)> AccountGroups(CleanerOptions options) =>
    [
        (RegistryAccounts, options.RegistryAccountSids), (PrivacyAccounts, options.PrivacyAccountSids),
        (JunkAccounts, options.JunkAccountSids), (ShortcutAccounts, options.ShortcutAccountSids)
    ];
    public string RecentLabel => Text("Cleaner_RecentFiles");
    public string CacheLabel => Text("Cleaner_BrowserCache");
    public string HistoryLabel => Text("Cleaner_BrowserHistory");
    public string OfflineCacheLabel => Text("Cleaner_BrowserOfflineCache");
    public string WindowsHistoryLabel => Text("Cleaner_WindowsHistory");
    public string ApplicationHistoryLabel => Text("Cleaner_ApplicationHistory");
    public string PrivacyCachesLabel => Text("Cleaner_ApplicationPrivacyCaches");
    public string CertificateCacheLabel => Text("Cleaner_CertificateCache");
    public string JumpListsLabel => Text("Cleaner_JumpLists");
    public string DefenderLogsLabel => Text("Cleaner_DefenderLogs");
    public string TempLabel => Text("Cleaner_UserTemp");
    public string WindowsTempLabel => Text("Cleaner_WindowsTemp");
    public string WindowsLogsLabel => Text("Cleaner_WindowsLogs");
    public string CrashDumpsLabel => Text("Cleaner_CrashDumps");
    public string InternetCacheLabel => Text("Cleaner_InternetCache");
    public string AgeLabel => Text("Cleaner_MinimumAge");
    public string AppsLabel => Text("Cleaner_ApplicationCaches");
    public string DesktopLabel => Text("Cleaner_Desktop");
    public string StartMenuLabel => Text("Cleaner_StartMenu");
    public string DiskLabel => Text("Cleaner_Disk");
    public string BackupsLabel => Text("Cleaner_Backups");
    public string BackupLabel => Text("Cleaner_BackupBeforeCleaning");
    public string HiveBackupLabel => Text("Cleaner_BackupBeforeRegistryDefrag");
    public string RestoreLabel => Text("Cleaner_RestoreBackup");
    public string ReloadBackupsLabel => Text("Cleaner_ReloadBackups");
    public string OpenBackupsLabel => Text("Settings_OpenBackupFolder");
    public string ShaderLabel => Text("Cleaner_ShaderCache");
    public string ThumbnailLabel => Text("Cleaner_ThumbnailCache");
    public string ReportsLabel => Text("Cleaner_ErrorReports");
    private string Text(string key) => _localization.GetString(key);

    public CleanerSettingsViewModel(IUserPreferencesService preferences, ICleanerBackend backend,
        ILocalizationService localization, ITaskProgressService tasks, IExplorerWindowManager explorer,
        ICleanerBackupService backups, IDialogService dialogs, IApplicationModeService applicationModeService)
    {
        _preferences = preferences;
        _backend = backend;
        _localization = localization;
        _tasks = tasks;
        _explorer = explorer;
        _backups = backups;
        _applicationModeService = applicationModeService;
        _dialogs = dialogs;
        _saves = new(options => _preferences.SetPreferenceAsync("Cleaner", options), PublishOptions,
            error => Status = error ?? "", "Unable to save Cleaner preferences.");
        foreach (var category in Enum.GetValues<RegistryArea>())
        {
            var choice = new CleanerChoice<RegistryArea>(category, $"Cleaner_Registry_{category}", localization);
            choice.PropertyChanged += OnChoiceChanged;
            RegistryCategories.Add(choice);
        }
        _localization.LanguageChanged += OnLanguageChanged;
    }

    public Task LoadAsync() => _loaded ? Task.CompletedTask :
        _loadTask is { IsCompleted: false } ? _loadTask : _loadTask = LoadCoreAsync(reset: false);

    private async Task<bool> LoadCoreAsync(bool reset)
    {
        if (_loaded && !reset) return true;
        if (IsBusy) { Status = Text("Cleaner_Wait"); return false; }
        IsBusy = true;
        try
        {
            var options = reset ? new CleanerOptions() : (await _preferences.GetPreferenceAsync("Cleaner", new CleanerOptions())).Validate();
            ++_applicationRevision;
            var apps = await _backend.GetApplicationsAsync(options, CancellationToken.None);
            var drives = await _backend.GetDrivesAsync(CancellationToken.None);
            var accounts = await _backend.GetRegistryAccountsAsync(CancellationToken.None);
            if (reset) { _saves.Enqueue(options); if (!await _saves.FlushAsync()) return false; }
            _applyingOptions = true;
            ApplyApplications(apps.Applications, options.ApplicationCaches);
            Drives.Clear();
            foreach (var drive in drives) Drives.Add(drive);
            _currentAccountSids.Clear();
            foreach (var account in accounts.Where(account => account.IsCurrent)) _currentAccountSids.Add(account.Sid);
            foreach (var (choices, selected) in AccountGroups(options))
            {
                foreach (var item in choices) item.PropertyChanged -= OnChoiceChanged;
                choices.Clear();
                foreach (var account in accounts)
                {
                    var choice = new CleanerChoice<string>(account.Sid, "", _localization, account.DisplayName);
                    choice.PropertyChanged += OnChoiceChanged;
                    choices.Add(choice);
                }
                foreach (var sid in (selected ?? []).Except(accounts.Select(account => account.Sid), StringComparer.Ordinal))
                {
                    var choice = new CleanerChoice<string>(sid, "", _localization, sid, "Cleaner_RegistryAccountUnavailable");
                    choice.PropertyChanged += OnChoiceChanged;
                    choices.Add(choice);
                }
            }
            ApplyOptions(options);
            _loaded = true;
            OnPropertyChanged(nameof(CanEdit));
            PublishOptions(options);
            ApplicationStatus = string.Join(Environment.NewLine, apps.Notices);
            Status = "";
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        { Status = ex.Message; return false; }
        finally { _applyingOptions = false; IsBusy = false; }
    }

    /// <summary>Applies a loaded or reset snapshot without treating initialization as user edits.</summary>
    private void ApplyOptions(CleanerOptions options)
    {
        _applyingOptions = true;
        try
        {
            DeepClean = options.DeepClean;
            BackupBeforeCleaning = options.BackupBeforeCleaning;
            BackupBeforeRegistryDefrag = options.BackupBeforeRegistryDefrag;
            RecentFiles = options.RecentFiles;
            BrowserCache = options.BrowserCache;
            BrowserHistory = options.BrowserHistory;
            BrowserOfflineCache = options.BrowserOfflineCache;
            WindowsHistory = options.WindowsHistory;
            ApplicationHistory = options.ApplicationHistory;
            ApplicationPrivacyCaches = options.ApplicationPrivacyCaches;
            CertificateCache = options.CertificateCache;
            JumpLists = options.JumpLists;
            DefenderLogs = options.DefenderLogs;
            UserTemp = options.UserTemp;
            WindowsTemp = options.WindowsTemp;
            WindowsLogs = options.WindowsLogs;
            ShaderCache = options.ShaderCache;
            ThumbnailCache = options.ThumbnailCache;
            ErrorReports = options.ErrorReports;
            CrashDumps = options.CrashDumps;
            InternetCache = options.InternetCache;
            DesktopShortcuts = options.DesktopShortcuts;
            StartMenuShortcuts = options.StartMenuShortcuts;
            RecentShortcuts = options.RecentShortcuts;
            MinimumAgeDays = options.MinimumAgeDays;
            DiskRoot = options.DiskRoot;
            foreach (var item in RegistryCategories) item.IsSelected = options.RegistryAreas.Contains(item.Value);
            foreach (var item in Applications) item.IsSelected = options.ApplicationCaches.Contains(item.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var (choices, selected) in AccountGroups(options))
                foreach (var item in choices) item.IsSelected = selected is null
                    ? _currentAccountSids.Contains(item.Value) : selected.Contains(item.Value, StringComparer.Ordinal);
        }
        finally { _applyingOptions = false; }
    }

    public async Task<bool> ResetAllAsync()
    {
        await _saves.FlushAsync();
        if (_loadTask is { IsCompleted: false }) await _loadTask;
        return await LoadCoreAsync(reset: true);
    }

    private void PublishOptions(CleanerOptions options)
    {
        if (ReferenceEquals(Options, options)) return;
        Options = options;
        OnPropertyChanged(nameof(Options));
    }

    /// <summary>Captures the current edits, optionally substituting defaults for one settings category.</summary>
    private CleanerOptions CaptureOptions(CleanerSettingsSection? reset = null)
    {
        var defaults = new CleanerOptions();
        if (reset == CleanerSettingsSection.All) return defaults.Validate();
        if (!double.IsFinite(MinimumAgeDays) || MinimumAgeDays != Math.Truncate(MinimumAgeDays))
        {
            if (reset != CleanerSettingsSection.Junk) throw new ArgumentException(Text("Cleaner_InvalidAge"));
        }
        var current = new CleanerOptions
        {
            RegistryAreas = RegistryCategories.Where(x => x.IsSelected).Select(x => x.Value).ToArray(),
            RegistryAccountSids = CaptureAccountSelection(RegistryAccounts, Options.RegistryAccountSids),
            PrivacyAccountSids = CaptureAccountSelection(PrivacyAccounts, Options.PrivacyAccountSids),
            JunkAccountSids = CaptureAccountSelection(JunkAccounts, Options.JunkAccountSids),
            ShortcutAccountSids = CaptureAccountSelection(ShortcutAccounts, Options.ShortcutAccountSids),
            DeepClean = DeepClean, RecentFiles = RecentFiles, BrowserCache = BrowserCache, BrowserHistory = BrowserHistory,
            BrowserOfflineCache = BrowserOfflineCache, WindowsHistory = WindowsHistory, ApplicationHistory = ApplicationHistory,
            ApplicationPrivacyCaches = ApplicationPrivacyCaches, CertificateCache = CertificateCache, JumpLists = JumpLists, DefenderLogs = DefenderLogs,
            BackupBeforeCleaning = BackupBeforeCleaning, BackupBeforeRegistryDefrag = BackupBeforeRegistryDefrag,
            ShaderCache = ShaderCache, ThumbnailCache = ThumbnailCache, ErrorReports = ErrorReports,
            WindowsLogs = WindowsLogs, CrashDumps = CrashDumps, InternetCache = InternetCache,
            UserTemp = UserTemp, WindowsTemp = WindowsTemp, DesktopShortcuts = DesktopShortcuts,
            StartMenuShortcuts = StartMenuShortcuts, RecentShortcuts = RecentShortcuts,
            MinimumAgeDays = reset == CleanerSettingsSection.Junk ? defaults.MinimumAgeDays : checked((int)MinimumAgeDays), DiskRoot = DiskRoot,
            ApplicationCaches = Applications.Where(x => x.IsSelected).Select(x => x.Value).ToArray()
        };
        return (reset switch
        {
            CleanerSettingsSection.Backups => current with { BackupBeforeCleaning = defaults.BackupBeforeCleaning, BackupBeforeRegistryDefrag = defaults.BackupBeforeRegistryDefrag },
            CleanerSettingsSection.Registry => current with { RegistryAreas = defaults.RegistryAreas, DeepClean = defaults.DeepClean, RegistryAccountSids = defaults.RegistryAccountSids },
            CleanerSettingsSection.Privacy => current with { RecentFiles = defaults.RecentFiles, BrowserCache = defaults.BrowserCache, BrowserHistory = defaults.BrowserHistory,
                BrowserOfflineCache = defaults.BrowserOfflineCache, WindowsHistory = defaults.WindowsHistory, ApplicationHistory = defaults.ApplicationHistory,
                ApplicationPrivacyCaches = defaults.ApplicationPrivacyCaches, CertificateCache = defaults.CertificateCache,
                JumpLists = defaults.JumpLists, DefenderLogs = defaults.DefenderLogs, PrivacyAccountSids = defaults.PrivacyAccountSids },
            CleanerSettingsSection.Junk => current with { UserTemp = defaults.UserTemp, WindowsTemp = defaults.WindowsTemp, WindowsLogs = defaults.WindowsLogs, ShaderCache = defaults.ShaderCache, ThumbnailCache = defaults.ThumbnailCache, ErrorReports = defaults.ErrorReports, CrashDumps = defaults.CrashDumps, InternetCache = defaults.InternetCache, MinimumAgeDays = defaults.MinimumAgeDays, ApplicationCaches = defaults.ApplicationCaches, JunkAccountSids = defaults.JunkAccountSids },
            CleanerSettingsSection.Shortcuts => current with { DesktopShortcuts = defaults.DesktopShortcuts, StartMenuShortcuts = defaults.StartMenuShortcuts,
                RecentShortcuts = defaults.RecentShortcuts, ShortcutAccountSids = defaults.ShortcutAccountSids },
            CleanerSettingsSection.Disk => current with { DiskRoot = defaults.DiskRoot },
            _ => current
        }).Validate();
    }

    private void QueueSave()
    {
        if (!_loaded || _applyingOptions) return;
        try { _saves.Enqueue(CaptureOptions()); }
        catch (Exception ex) when (ex is ArgumentException or OverflowException) { _saves.Reject(ex.Message); }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(DeepClean) or nameof(RecentFiles) or nameof(BrowserCache) or nameof(BrowserHistory)
            or nameof(BrowserOfflineCache) or nameof(WindowsHistory) or nameof(ApplicationHistory) or nameof(ApplicationPrivacyCaches)
            or nameof(CertificateCache) or nameof(JumpLists) or nameof(DefenderLogs)
            or nameof(BackupBeforeCleaning) or nameof(BackupBeforeRegistryDefrag) or nameof(UserTemp) or nameof(WindowsTemp)
            or nameof(WindowsLogs) or nameof(CrashDumps) or nameof(InternetCache)
            or nameof(ShaderCache) or nameof(ThumbnailCache) or nameof(ErrorReports) or nameof(DesktopShortcuts)
            or nameof(StartMenuShortcuts) or nameof(RecentShortcuts) or nameof(MinimumAgeDays) or nameof(DiskRoot)) QueueSave();
    }

    private void OnChoiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CleanerChoice<string>.IsSelected)) return;
        QueueSave();
        if (_loaded && !_applyingOptions && sender is CleanerChoice<string> choice && JunkAccounts.Contains(choice))
            QueueApplicationRefresh(Options with { JunkAccountSids = CaptureAccountSelection(JunkAccounts, Options.JunkAccountSids) });
    }

    /// <summary>Preserves the implicit current-profile default and explicit empty selections independently.</summary>
    private string[]? CaptureAccountSelection(ObservableCollection<CleanerChoice<string>> choices, string[]? previous)
    {
        var selected = choices.Where(account => account.IsSelected).Select(account => account.Value).ToArray();
        return previous is null && _currentAccountSids.SetEquals(selected) ? null : selected;
    }

    /// <summary>Serializes account inventories and rejects stale results when the user changes scopes again.</summary>
    private void QueueApplicationRefresh(CleanerOptions options)
    {
        var previous = _applicationRefresh;
        int revision = ++_applicationRevision;
        _applicationRefresh = RefreshAsync();
        async Task RefreshAsync()
        {
            await previous;
            if (_disposed || revision != _applicationRevision) return;
            try
            {
                var inventory = await _backend.GetApplicationsAsync(options, CancellationToken.None);
                if (_disposed || revision != _applicationRevision) return;
                ApplyApplications(inventory.Applications, Applications.Where(app => app.IsSelected).Select(app => app.Value).ToArray());
                ApplicationStatus = string.Join(Environment.NewLine, inventory.Notices);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            { if (!_disposed && revision == _applicationRevision) ApplicationStatus = ex.Message; }
        }
    }

    /// <summary>Replaces the inventory while retaining explicitly selected package identifiers absent from the new scope.</summary>
    private void ApplyApplications(IReadOnlyList<CleanerApplication> inventory, IReadOnlyCollection<string> selected)
    {
        foreach (var app in Applications) app.PropertyChanged -= OnChoiceChanged;
        Applications.Clear();
        foreach (var app in inventory.Concat(selected.Except(inventory.Select(app => app.Id), StringComparer.OrdinalIgnoreCase)
            .Select(id => new CleanerApplication(id, id))))
        {
            var choice = new CleanerChoice<string>(app.Id, "", _localization, app.DisplayName)
                { IsSelected = selected.Contains(app.Id, StringComparer.OrdinalIgnoreCase) };
            choice.PropertyChanged += OnChoiceChanged;
            Applications.Add(choice);
        }
    }

    public async Task<bool> FlushAsync()
    {
        if (!_loaded) return false;
        await _applicationRefresh;
        return await _saves.FlushAsync();
    }

    /// <summary>Restores only the opened category, preserving edits in every other category.</summary>
    public async Task<bool> ResetSectionAsync(CleanerSettingsSection section)
    {
        await LoadAsync();
        if (!_loaded) return false;
        try
        {
            var options = CaptureOptions(section);
            ApplyOptions(options);
            if (section == CleanerSettingsSection.Junk) QueueApplicationRefresh(options);
            _saves.Enqueue(options);
            return await _saves.FlushAsync();
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException) { _saves.Reject(ex.Message); return false; }
    }

    [RelayCommand]
    private async Task ReloadBackupsAsync()
    {
        if (IsBusy || _tasks.IsTaskRunning) { Status = Text("Cleaner_Wait"); return; }
        IsBusy = true;
        try
        {
            var list = await _backups.ListAsync(CancellationToken.None);
            Backups.Clear();
            foreach (var item in list) Backups.Add(item);
            SelectedBackup = Backups.FirstOrDefault();
            Status = Backups.Count == 0 ? Text("Cleaner_NoBackups") : "";
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RestoreBackupAsync()
    {
        if (_applicationModeService.CurrentMode == WinOptimizer.Core.Features.Common.Enums.WinOptimizerMode.Builder)
        { Status = Text("TrialMode_Blocked"); return; }
        if (IsBusy || _tasks.IsTaskRunning) { Status = Text("Cleaner_Wait"); return; }
        if (SelectedBackup is null) { Status = Text("Cleaner_SelectBackup"); return; }
        var selected = SelectedBackup;
        IsBusy = true;
        CancellationTokenSource? cts = null;
        try
        {
            var confirmation = await _dialogs.ShowConfirmationAsync(new WinOptimizer.Core.Features.Common.Models.ConfirmationRequest
            {
                Title = RestoreLabel, Message = Text("Cleaner_RestoreConfirm"), ConfirmButtonText = RestoreLabel,
                CancelButtonText = Text("Button_Cancel")
            });
            if (!confirmation.Confirmed) return;
            if (_applicationModeService.CurrentMode == WinOptimizer.Core.Features.Common.Enums.WinOptimizerMode.Builder)
            { Status = Text("TrialMode_Blocked"); return; }
            if (_tasks.IsTaskRunning) { Status = Text("Cleaner_Wait"); return; }
            cts = _tasks.StartTask(RestoreLabel, isIndeterminate: true);
            var result = await _backups.RestoreAsync(selected.Id, cts.Token);
            Status = string.Format(Text("Cleaner_RestoreComplete"), result.Restored, result.Errors.Count);
            if (result.Errors.Count > 0) Status += Environment.NewLine + string.Join(Environment.NewLine, result.Errors);
        }
        catch (OperationCanceledException) { Status = Text("Cleaner_Cancelled"); }
        catch (Exception ex) { Status = ex.Message; }
        finally { if (cts is not null) _tasks.CompleteTask(); IsBusy = false; }
    }

    [RelayCommand]
    private async Task OpenBackupsAsync()
    {
        try
        {
            if (!Directory.Exists(_backend.BackupDirectory)) { Status = Text("Cleaner_NoBackups"); return; }
            await _explorer.OpenFolderAsync(_backend.BackupDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status = ex.Message; }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(string.Empty);
        foreach (var item in RegistryCategories) item.RefreshText();
        foreach (var (choices, _) in AccountGroups(Options)) foreach (var item in choices) item.RefreshText();
    }
    public void Dispose()
    {
        _disposed = true;
        ++_applicationRevision;
        _localization.LanguageChanged -= OnLanguageChanged;
        foreach (var item in RegistryCategories) item.PropertyChanged -= OnChoiceChanged;
        foreach (var item in Applications) item.PropertyChanged -= OnChoiceChanged;
        foreach (var (choices, _) in AccountGroups(Options)) foreach (var item in choices) item.PropertyChanged -= OnChoiceChanged;
    }
}
