using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using System.Windows.Input;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.Common.Interfaces;

namespace WinOptimizer.UI.ViewModels;

/// <summary>
/// Tracks the update notification state for language changes and action selection.
/// </summary>
internal enum UpdateToastState
{
    None,
    UpdateAvailable,
    NoUpdates,
    CheckError,
    Downloading,
    DownloadFailed,
    UpdateDownloaded
}

/// <summary>
/// Child ViewModel for update checking in the main window.
/// Manages update notifications and check/install commands.
/// </summary>
public partial class UpdateCheckViewModel : ObservableObject, IDisposable
{
    private bool _disposed;
    private readonly IVersionService _versionService;
    private readonly ILocalizationService _localizationService;
    private readonly ILogService _logService;
    private readonly IBackgroundUpdateCheckService _backgroundUpdates;
    private readonly IDispatcherService _dispatcher;
    private readonly IApplicationCloseService _close;
    private readonly CancellationTokenSource _backgroundCancellation = new();
    private Task? _backgroundTask;

    [ObservableProperty]
    public partial bool IsUpdateToastOpen { get; set; }

    [ObservableProperty]
    public partial string UpdateToastTitle { get; set; }

    [ObservableProperty]
    public partial string UpdateToastMessage { get; set; }

    private UpdateToastState _updateToastState;
    private string? _currentVersion;
    private VersionInfo? _latestVersion;
    private VersionInfo? _notifiedVersion;
    private bool _lastCheckFailed;
    private string _cachedErrorMessage = string.Empty;

    [ObservableProperty]
    public partial InfoBarSeverity UpdateToastSeverity { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(UpdateToastHasAction), nameof(UpdateToastActionText), nameof(UpdateToastActionCommand))]
    public partial bool IsUpdateActionButtonVisible { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(UpdateStatusText))]
    public partial bool IsUpdateCheckInProgress { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(UpdateToastHasAction), nameof(UpdateToastActionText), nameof(UpdateToastActionCommand))]
    public partial bool IsRelaunchButtonVisible { get; set; }

    public bool UpdateToastHasAction => IsUpdateActionButtonVisible || IsRelaunchButtonVisible;
    public string UpdateToastActionText => IsRelaunchButtonVisible ? RelaunchButtonText : InstallNowButtonText;
    public ICommand? UpdateToastActionCommand => IsRelaunchButtonVisible ? RelaunchCommand
        : IsUpdateActionButtonVisible ? InstallUpdateCommand : null;

    internal bool IsUpdateToastTransient => _updateToastState == UpdateToastState.NoUpdates;
    internal event Action? UpdateToastPresented;

    public string CheckForUpdatesLabel =>
        _localizationService.GetString("Menu_CheckForUpdates") ?? "Check for Updates";

    private string CurrentVersion => _currentVersion ??= _versionService.GetCurrentVersion()?.Version ?? string.Empty;
    private string CurrentVersionLabel => _localizationService.GetString("Dialog_Update_CurrentVersion") ?? "Current Version:";
    private string LatestVersionLabel => _localizationService.GetString("Dialog_Update_LatestVersion") ?? "Latest Version:";

    public string CurrentVersionText => $"{CurrentVersionLabel} {(string.IsNullOrWhiteSpace(CurrentVersion) ? "—" : CurrentVersion)}";
    public string LatestVersionText => $"{LatestVersionLabel} {_latestVersion?.Version ?? "—"}";

    /// <summary>Describes update checks independently of the dismissible notification.</summary>
    public string UpdateStatusText
    {
        get
        {
            if (IsUpdateCheckInProgress)
                return _localizationService.GetString("Menu_Update_Checking") ?? "Checking for updates…";
            if (_lastCheckFailed)
                return _localizationService.GetString("Dialog_Update_CheckError_Title") ?? "Update Check Error";
            return _latestVersion?.IsUpdateAvailable switch
            {
                true => _localizationService.GetString("Dialog_Update_Title") ?? "Update Available",
                false => _localizationService.GetString("Dialog_Update_NoUpdates_Title") ?? "No Updates Available",
                null => _localizationService.GetString("Menu_Update_NotChecked") ?? "Latest release has yet to be verified."
            };
        }
    }

    public string InstallNowButtonText =>
        _localizationService.GetString("Dialog_Update_Button_InstallNow") ?? "Install Now";

    public string RelaunchButtonText =>
        _localizationService.GetString("Dialog_Update_Button_Relaunch") ?? "Relaunch";

    public UpdateCheckViewModel(
        IVersionService versionService,
        ILocalizationService localizationService,
        ILogService logService,
        IBackgroundUpdateCheckService backgroundUpdates,
        IDispatcherService dispatcher, IApplicationCloseService close)
    {
        _versionService = versionService;
        _localizationService = localizationService;
        _logService = logService;
        _backgroundUpdates = backgroundUpdates;
        _dispatcher = dispatcher;
        _close = close;

        UpdateToastTitle = string.Empty;
        UpdateToastMessage = string.Empty;

        _localizationService.LanguageChanged += OnLanguageChanged;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _backgroundCancellation.Cancel();
        _backgroundCancellation.Dispose();
        _localizationService.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(CheckForUpdatesLabel));
        OnPropertyChanged(nameof(CurrentVersionText));
        OnPropertyChanged(nameof(LatestVersionText));
        OnPropertyChanged(nameof(UpdateStatusText));
        OnPropertyChanged(nameof(InstallNowButtonText));
        OnPropertyChanged(nameof(RelaunchButtonText));
        OnPropertyChanged(nameof(UpdateToastActionText));
        if (IsUpdateToastOpen)
        {
            RefreshUpdateToastText();
        }
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (IsUpdateCheckInProgress) return;

        try
        {
            IsUpdateCheckInProgress = true;
            _logService.Log(Core.Features.Common.Enums.LogLevel.Info, "Checking for updates...");

            var latestVersion = await _versionService.CheckForUpdateAsync();
            if (latestVersion is null || string.IsNullOrWhiteSpace(latestVersion.Version))
                throw new InvalidOperationException(_localizationService.GetString("Dialog_Update_CheckError_Title") ?? "The update check did not return a supported release.");
            _logService.Log(Core.Features.Common.Enums.LogLevel.Info, latestVersion.IsUpdateAvailable
                ? $"Update available: {latestVersion.Version}"
                : "No updates available");
            RecordVersionCheck(latestVersion);
            ShowVersionCheckNotification(latestVersion);
        }
        catch (Exception ex)
        {
            _logService.Log(Core.Features.Common.Enums.LogLevel.Error, $"Error checking for updates: {ex.Message}");

            _cachedErrorMessage = ex.Message;
            _lastCheckFailed = true;
            ShowUpdateToast(UpdateToastState.CheckError);
        }
        finally
        {
            IsUpdateCheckInProgress = false;
        }
    }

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        try
        {
            ShowUpdateToast(UpdateToastState.Downloading);

            await _versionService.DownloadAndInstallUpdateAsync();

            ShowUpdateToast(UpdateToastState.UpdateDownloaded);
        }
        catch (Exception ex)
        {
            _logService.Log(Core.Features.Common.Enums.LogLevel.Error, $"Error installing update: {ex.Message}");

            _cachedErrorMessage = ex.Message;
            ShowUpdateToast(UpdateToastState.DownloadFailed);
        }
    }

    [RelayCommand]
    private async Task RelaunchAsync()
    {
        try
        {
            await _close.CheckOperationsAndRestartAsync(_versionService.LaunchInstallerAndRestart);
        }
        catch (Exception ex)
        {
            _logService.Log(Core.Features.Common.Enums.LogLevel.Error, $"Error relaunching: {ex.Message}");
        }
    }

    /// <summary>
    /// Starts the deferred daily checks and presents the results on the UI thread.
    /// </summary>
    public Task StartBackgroundUpdateChecksAsync()
    {
        if (_disposed) return Task.CompletedTask;
        return _backgroundTask ??= _backgroundUpdates.RunAsync(latestVersion =>
            _dispatcher.RunOnUIThread(() =>
            {
                if (_disposed) return;
                RecordVersionCheck(latestVersion);
                if (latestVersion.IsUpdateAvailable)
                    ShowVersionCheckNotification(latestVersion);
            }), _backgroundCancellation.Token);
    }

    /// <summary>Keeps the verified release visible after the notification is dismissed.</summary>
    private void RecordVersionCheck(VersionInfo latestVersion)
    {
        _latestVersion = latestVersion;
        _lastCheckFailed = false;
        OnPropertyChanged(nameof(LatestVersionText));
        OnPropertyChanged(nameof(UpdateStatusText));
    }

    /// <summary>Shows the shared update notification for a completed check.</summary>
    private void ShowVersionCheckNotification(VersionInfo latestVersion)
    {
        _notifiedVersion = latestVersion;
        ShowUpdateToast(latestVersion.IsUpdateAvailable ? UpdateToastState.UpdateAvailable : UpdateToastState.NoUpdates);
    }

    /// <summary>Publishes one toast for every reportable update state, including after dismissal.</summary>
    private void ShowUpdateToast(UpdateToastState state)
    {
        _updateToastState = state;
        RefreshUpdateToastText();
        UpdateToastSeverity = state switch
        {
            UpdateToastState.CheckError or UpdateToastState.DownloadFailed => InfoBarSeverity.Error,
            UpdateToastState.Downloading => InfoBarSeverity.Informational,
            _ => InfoBarSeverity.Success
        };
        IsUpdateActionButtonVisible = state == UpdateToastState.UpdateAvailable;
        IsRelaunchButtonVisible = state == UpdateToastState.UpdateDownloaded;
        IsUpdateToastOpen = true;
        UpdateToastPresented?.Invoke();
    }

    /// <summary>Dismisses the toast while keeping the verified release in the version menu.</summary>
    public void DismissUpdateToast()
    {
        _updateToastState = UpdateToastState.None;
        IsUpdateToastOpen = false;
    }

    /// <summary>
    /// Resolves the current toast text when presenting it or changing language.
    /// </summary>
    private void RefreshUpdateToastText()
    {
        switch (_updateToastState)
        {
            case UpdateToastState.UpdateAvailable:
                var message = _localizationService.GetString("Dialog_Update_Message") ?? "Good News! A New Version of win-optimizer is available.";
                UpdateToastTitle = _localizationService.GetString("Dialog_Update_Title") ?? "Update Available";
                UpdateToastMessage = $"{message}  {CurrentVersionText}  →  {LatestVersionLabel} {_notifiedVersion?.Version}";
                break;

            case UpdateToastState.NoUpdates:
                UpdateToastTitle = _localizationService.GetString("Dialog_Update_NoUpdates_Title") ?? "No Updates Available";
                UpdateToastMessage = _localizationService.GetString("Dialog_Update_NoUpdates_Message") ?? "You have the latest version of win-optimizer.";
                break;

            case UpdateToastState.CheckError:
                UpdateToastTitle = _localizationService.GetString("Dialog_Update_CheckError_Title") ?? "Update Check Error";
                var errorTemplate = _localizationService.GetString("Dialog_Update_CheckError_Message") ?? "An error occurred while checking for updates: {0}";
                UpdateToastMessage = string.Format(errorTemplate, _cachedErrorMessage);
                break;

            case UpdateToastState.Downloading:
                UpdateToastTitle = _localizationService.GetString("Dialog_Update_Status_Downloading") ?? "Downloading update...";
                UpdateToastMessage = string.Empty;
                break;

            case UpdateToastState.DownloadFailed:
                UpdateToastTitle = _localizationService.GetString("Dialog_Update_CheckError_Title") ?? "Update Error";
                var downloadError = _localizationService.GetString("Dialog_Update_Status_Error") ?? "Error downloading update: {0}";
                UpdateToastMessage = string.Format(downloadError, _cachedErrorMessage);
                break;

            case UpdateToastState.UpdateDownloaded:
                UpdateToastTitle = _localizationService.GetString("Dialog_Update_Installed_Title") ?? "Update Ready";
                UpdateToastMessage = _localizationService.GetString("Dialog_Update_Installed_Message") ?? "The update has been downloaded. Click Relaunch to install and restart win-optimizer.";
                break;
        }
    }
}
