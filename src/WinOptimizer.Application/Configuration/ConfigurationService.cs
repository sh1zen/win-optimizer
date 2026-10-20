using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Application.Configuration;

/// <summary>
/// Thin facade that preserves the IConfigurationService contract.
/// All work is delegated to focused sub-services.
/// </summary>
public class ConfigurationService : IConfigurationService
{
    private readonly ILogService _logService;
    private readonly ICompatibleSettingsRegistry _compatibleSettingsRegistry;
    private readonly IGlobalSettingsPreloader _settingsPreloader;
    private readonly IConfigExportService _configExportService;
    private readonly IConfigLoadService _configLoadService;
    private readonly IConfigApplicationExecutionService _configExecutionService;
    private readonly IConfigReviewOrchestrationService _configReviewOrchestrationService;
    private readonly IDialogService _dialogService;
    private readonly ILocalizationService _localizationService;
    private readonly IApplicationModeService _applicationModeService;

    public ConfigurationService(
        ILogService logService,
        ICompatibleSettingsRegistry compatibleSettingsRegistry,
        IGlobalSettingsPreloader settingsPreloader,
        IConfigExportService configExportService,
        IConfigLoadService configLoadService,
        IConfigApplicationExecutionService configExecutionService,
        IConfigReviewOrchestrationService configReviewOrchestrationService,
        IDialogService dialogService,
        ILocalizationService localizationService,
        IApplicationModeService applicationModeService)
    {
        _logService = logService;
        _compatibleSettingsRegistry = compatibleSettingsRegistry;
        _settingsPreloader = settingsPreloader;
        _configExportService = configExportService;
        _configLoadService = configLoadService;
        _configExecutionService = configExecutionService;
        _configReviewOrchestrationService = configReviewOrchestrationService;
        _dialogService = dialogService;
        _localizationService = localizationService;
        _applicationModeService = applicationModeService;
    }

    private Task EnsureRegistryInitializedAsync()
        => ConfigRegistryInitializer.EnsureInitializedAsync(_compatibleSettingsRegistry, _settingsPreloader, _logService);

    public async Task ExportConfigurationAsync()
    {
        await _configExportService.ExportConfigurationAsync();
    }

    public async Task ImportConfigurationAsync()
    {
        _logService.Log(LogLevel.Info, "Starting configuration import");

        await EnsureRegistryInitializedAsync();

        var (selectedOption, importOptions) = await _dialogService.ShowConfigImportOptionsDialogAsync();
        if (selectedOption == null)
        {
            _logService.Log(LogLevel.Info, "Import canceled by user");
            return;
        }

        UnifiedConfigurationFile? config = selectedOption switch
        {
            ImportOption.ImportOwn => await _configLoadService.LoadAndValidateConfigurationFromFileAsync(),
            ImportOption.ImportRecommended => await _configLoadService.LoadRecommendedConfigurationAsync(),
            ImportOption.ImportBackup => await _configLoadService.LoadUserBackupConfigurationAsync(),
            ImportOption.ImportWindowsDefaults => await _configLoadService.LoadWindowsDefaultsConfigurationAsync(),
            _ => null
        };

        if (config == null)
        {
            if (selectedOption != ImportOption.ImportOwn)
                return;
            _logService.Log(LogLevel.Info, "Import canceled");
            return;
        }

        if (selectedOption == ImportOption.ImportWindowsDefaults)
        {
            importOptions = importOptions with { IsWindowsDefaults = true };
        }

        await _configExecutionService.ExecuteConfigImportAsync(
            config, importOptions);
    }

    public async Task ImportRecommendedConfigurationAsync()
    {
        _logService.Log(LogLevel.Info, "Starting recommended configuration import");

        await EnsureRegistryInitializedAsync();

        var config = await _configLoadService.LoadRecommendedConfigurationAsync();
        if (config == null) return;

        var confirmation = await _dialogService.ShowConfirmationAsync(new ConfirmationRequest
        {
            Title = _localizationService.GetString("Dialog_ImportConfig_Title"),
            Message = _localizationService.GetString("Dialog_ImportConfig_Apply_Message"),
            ConfirmButtonText = _localizationService.GetString("Button_Apply"),
            CancelButtonText = _localizationService.GetString("Button_Cancel")
        });
        if (!confirmation.Confirmed) return;

        await _configExecutionService.ExecuteConfigImportAsync(config, new ImportOptions());
    }

    /// <summary>Uses the existing defaults loader and execution policy, after explicit confirmation.</summary>
    public async Task RestoreWindowsDefaultsAsync()
    {
        using var operation = _applicationModeService.BeginSystemOperation();
        var confirmation = await _dialogService.ShowConfirmationAsync(new ConfirmationRequest
        {
            Title = _localizationService.GetString("Restore_DefaultsTitle"),
            Message = _localizationService.GetString("Restore_DefaultsConfirm"),
            ConfirmButtonText = _localizationService.GetString("Restore_DefaultsButton"),
            CancelButtonText = _localizationService.GetString("Button_Cancel")
        });
        if (!confirmation.Confirmed) return;
        await EnsureRegistryInitializedAsync();
        var config = await _configLoadService.LoadWindowsDefaultsConfigurationAsync();
        if (config is null) return;
        await _configExecutionService.ExecuteConfigImportAsync(config, new ImportOptions { IsWindowsDefaults = true });
    }

    public async Task CreateUserBackupConfigAsync()
    {
        await _configExportService.CreateUserBackupConfigAsync();
    }

    public async Task ApplyReviewedConfigAsync()
    {
        await _configReviewOrchestrationService.ApplyReviewedConfigAsync();
    }

    public async Task CancelReviewModeAsync()
    {
        await _configReviewOrchestrationService.CancelReviewModeAsync();
    }
}
