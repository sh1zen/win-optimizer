using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.ViewModels;

namespace WinOptimizer.UI.Features.Common.ViewModels;

/// <summary>
/// Provides application version, update checking and help actions for Settings.
/// </summary>
public partial class ApplicationInfoViewModel : ObservableObject, IDisposable
{
    private bool _disposed;
    private readonly ILocalizationService _localizationService;
    private readonly IVersionService _versionService;
    private readonly ILogService _logService;
    private readonly IDialogService _dialogService;
    private readonly IProcessExecutor _processExecutor;
    public UpdateCheckViewModel Updates { get; }

    [ObservableProperty]
    public partial string VersionInfo { get; set; }

    public ApplicationInfoViewModel(
        ILocalizationService localizationService,
        IVersionService versionService,
        ILogService logService,
        IDialogService dialogService,
        IProcessExecutor processExecutor,
        UpdateCheckViewModel updates)
    {
        _localizationService = localizationService;
        _versionService = versionService;
        _logService = logService;
        _dialogService = dialogService;
        _processExecutor = processExecutor;
        Updates = updates;
        VersionInfo = "win-optimizer";

        // Subscribe to language changes
        _localizationService.LanguageChanged += OnLanguageChanged;

        InitializeVersionInfo();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _localizationService.LanguageChanged -= OnLanguageChanged;
    }

    /// <summary>
    /// Handles language changes to update localized strings.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(DocumentationLabel));
        OnPropertyChanged(nameof(ReportBugLabel));
        OnPropertyChanged(nameof(CheckForUpdatesLabel));
        OnPropertyChanged(nameof(SupportLabel));
        OnPropertyChanged(nameof(VersionLabel));
    }

    private void InitializeVersionInfo()
    {
        try
        {
            var versionInfo = _versionService.GetCurrentVersion();
            VersionInfo = $"win-optimizer {versionInfo.Version}";
        }
        catch (Exception ex)
        {
            _logService.LogDebug($"[ApplicationInfoViewModel] Failed to get version info: {ex.Message}");
            VersionInfo = "win-optimizer";
        }
    }

    #region Localized Strings

    public string DocumentationLabel =>
        _localizationService.GetString("Tooltip_Documentation") ?? "Documentation";

    public string ReportBugLabel =>
        _localizationService.GetString("Tooltip_ReportBug") ?? "Report a Bug";

    public string CheckForUpdatesLabel =>
        _localizationService.GetString("Menu_CheckForUpdates") ?? "Check for Updates";

    public string SupportLabel =>
        _localizationService.GetString("Menu_SupportWinOptimizer") ?? "Support win-optimizer";

    public string VersionLabel => _localizationService.GetString("Dialog_Update_CurrentVersion");

    #endregion

    #region Commands

    [RelayCommand]
    private async Task OpenDocsAsync()
    {
        try
        {
            await _processExecutor.ShellExecuteAsync("https://github.com/sh1zen/win-optimizer#readme");
        }
        catch (Exception ex)
        {
            _logService.LogError($"Failed to open documentation page: {ex.Message}", ex);
        }
    }

    [RelayCommand]
    private async Task ReportBugAsync()
    {
        try
        {
            await _processExecutor.ShellExecuteAsync("https://github.com/sh1zen/win-optimizer/issues");
        }
        catch (Exception ex)
        {
            _logService.LogError($"Failed to open bug report page: {ex.Message}", ex);
        }
    }

    [RelayCommand]
    private async Task SupportWinOptimizerAsync()
    {
        try
        {
            await _dialogService.ShowSponsorsDialogAsync(SponsorsDialogMode.Normal);
        }
        catch (Exception ex)
        {
            _logService.LogError($"Failed to open sponsors dialog: {ex.Message}", ex);
        }
    }

    #endregion
}
