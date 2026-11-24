using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.Common.Interfaces;

namespace WinOptimizer.UI.ViewModels;

/// <summary>
/// Tracks the current state of the update InfoBar for language-change re-rendering.
/// </summary>
internal enum UpdateInfoBarState
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
/// Manages update InfoBar state, check/install commands.
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
    public partial bool IsUpdateInfoBarOpen { get; set; }

    [ObservableProperty]
    public partial string UpdateInfoBarTitle { get; set; }

    [ObservableProperty]
    public partial string UpdateInfoBarMessage { get; set; }

    // Tracks InfoBar state for re-rendering on language change
    private UpdateInfoBarState _updateInfoBarState = UpdateInfoBarState.None;
    private string? _currentVersion;
    private VersionInfo? _latestVersion;
    private VersionInfo? _notifiedVersion;
    private bool _lastCheckFailed;
    private string _cachedErrorMessage = string.Empty;

    [ObservableProperty]
    public partial InfoBarSeverity UpdateInfoBarSeverity { get; set; }

    [ObservableProperty]
    public partial bool IsUpdateActionButtonVisible { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(UpdateStatusText))]
    public partial bool IsUpdateCheckInProgress { get; set; }

    [ObservableProperty]
    public partial bool IsRelaunchButtonVisible { get; set; }

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

        UpdateInfoBarTitle = string.Empty;
        UpdateInfoBarMessage = string.Empty;

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
        if (IsUpdateInfoBarOpen)
        {
            RefreshUpdateInfoBarText();
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
            _updateInfoBarState = UpdateInfoBarState.CheckError;
            RefreshUpdateInfoBarText();
            UpdateInfoBarSeverity = InfoBarSeverity.Error;
            IsUpdateActionButtonVisible = false;
            IsUpdateInfoBarOpen = true;
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
            _updateInfoBarState = UpdateInfoBarState.Downloading;
            RefreshUpdateInfoBarText();
            IsUpdateActionButtonVisible = false;
            IsRelaunchButtonVisible = false;

            await _versionService.DownloadAndInstallUpdateAsync();

            _updateInfoBarState = UpdateInfoBarState.UpdateDownloaded;
            RefreshUpdateInfoBarText();
            UpdateInfoBarSeverity = InfoBarSeverity.Success;
            IsRelaunchButtonVisible = true;
        }
        catch (Exception ex)
        {
            _logService.Log(Core.Features.Common.Enums.LogLevel.Error, $"Error installing update: {ex.Message}");

            _cachedErrorMessage = ex.Message;
            _updateInfoBarState = UpdateInfoBarState.DownloadFailed;
            RefreshUpdateInfoBarText();
            UpdateInfoBarSeverity = InfoBarSeverity.Error;
            IsUpdateActionButtonVisible = false;
            IsRelaunchButtonVisible = false;
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
        _updateInfoBarState = latestVersion.IsUpdateAvailable ? UpdateInfoBarState.UpdateAvailable : UpdateInfoBarState.NoUpdates;
        RefreshUpdateInfoBarText();
        UpdateInfoBarSeverity = InfoBarSeverity.Success;
        IsUpdateActionButtonVisible = latestVersion.IsUpdateAvailable;
        IsUpdateInfoBarOpen = true;
    }

    /// <summary>
    /// Dismisses the update InfoBar (called from code-behind on InfoBar.Closed).
    /// </summary>
    public void DismissUpdateInfoBar()
    {
        IsUpdateInfoBarOpen = false;
        _updateInfoBarState = UpdateInfoBarState.None;
    }

    /// <summary>
    /// Re-resolves InfoBar title/message from localization based on the current state.
    /// Called when the InfoBar state is first set and on language change.
    /// </summary>
    private void RefreshUpdateInfoBarText()
    {
        switch (_updateInfoBarState)
        {
            case UpdateInfoBarState.UpdateAvailable:
                var message = _localizationService.GetString("Dialog_Update_Message") ?? "Good News! A New Version of win-optimizer is available.";
                UpdateInfoBarTitle = _localizationService.GetString("Dialog_Update_Title") ?? "Update Available";
                UpdateInfoBarMessage = $"{message}  {CurrentVersionText}  →  {LatestVersionLabel} {_notifiedVersion?.Version}";
                break;

            case UpdateInfoBarState.NoUpdates:
                UpdateInfoBarTitle = _localizationService.GetString("Dialog_Update_NoUpdates_Title") ?? "No Updates Available";
                UpdateInfoBarMessage = _localizationService.GetString("Dialog_Update_NoUpdates_Message") ?? "You have the latest version of win-optimizer.";
                break;

            case UpdateInfoBarState.CheckError:
                UpdateInfoBarTitle = _localizationService.GetString("Dialog_Update_CheckError_Title") ?? "Update Check Error";
                var errorTemplate = _localizationService.GetString("Dialog_Update_CheckError_Message") ?? "An error occurred while checking for updates: {0}";
                UpdateInfoBarMessage = string.Format(errorTemplate, _cachedErrorMessage);
                break;

            case UpdateInfoBarState.Downloading:
                UpdateInfoBarTitle = _localizationService.GetString("Dialog_Update_Status_Downloading") ?? "Downloading update...";
                UpdateInfoBarMessage = string.Empty;
                break;

            case UpdateInfoBarState.DownloadFailed:
                UpdateInfoBarTitle = _localizationService.GetString("Dialog_Update_CheckError_Title") ?? "Update Error";
                var downloadError = _localizationService.GetString("Dialog_Update_Status_Error") ?? "Error downloading update: {0}";
                UpdateInfoBarMessage = string.Format(downloadError, _cachedErrorMessage);
                break;

            case UpdateInfoBarState.UpdateDownloaded:
                UpdateInfoBarTitle = _localizationService.GetString("Dialog_Update_Installed_Title") ?? "Update Ready";
                UpdateInfoBarMessage = _localizationService.GetString("Dialog_Update_Installed_Message") ?? "The update has been downloaded. Click Relaunch to install and restart win-optimizer.";
                break;
        }
    }
}
