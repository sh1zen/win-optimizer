using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinOptimizer.Core.Features.AdvancedTools.Interfaces;
using WinOptimizer.Core.Features.Common.Exceptions;
using WinOptimizer.Core.Features.Common.Extensions;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.AdvancedTools.Models;
using WinOptimizer.UI.Features.Common.Interfaces;

namespace WinOptimizer.UI.Features.AdvancedTools.ViewModels;

/// <summary>
/// Sub-ViewModel for WIM Utility Step 4: oscdimg download, output path selection, and ISO creation.
/// </summary>
public partial class WimStep4IsoViewModel : ObservableObject, IDisposable
{
    private readonly IOscdimgToolManager _oscdimgToolManager;
    private readonly IIsoService _isoService;
    private readonly ITaskProgressService _taskProgressService;
    private readonly IProcessExecutor _processExecutor;
    private readonly IDialogService _dialogService;
    private readonly ILocalizationService _localizationService;
    private readonly IFileSystemService _fileSystemService;
    private readonly IFilePickerService _filePickerService;
    private readonly ILogService _logService;
    private readonly IResourceService _resourceService;
    private bool _disposed;
    private int _mediaRevision;

    /// <summary>
    /// The working directory, set by the parent when Step 1 completes.
    /// </summary>
    [ObservableProperty]
    public partial string WorkingDirectory { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsOscdimgAvailable { get; set; }

    [ObservableProperty]
    public partial string OutputIsoPath { get; set; }

    [ObservableProperty]
    public partial bool IsIsoCreated { get; set; }

    // Completion belongs to the current input directory and destination file.
    partial void OnWorkingDirectoryChanged(string value) => ResetForMediaChange();
    partial void OnOutputIsoPathChanged(string value) => ResetForMediaChange();

    /// <summary>Invalidates completion and pending results when the media or destination changes.</summary>
    public void ResetForMediaChange()
    {
        _mediaRevision++;
        IsIsoCreated = false;
    }

    public WizardActionCard DownloadOscdimgCard { get; private set; } = new();
    public WizardActionCard SelectOutputCard { get; private set; } = new();

    public WimStep4IsoViewModel(
        IOscdimgToolManager oscdimgToolManager,
        IIsoService isoService,
        ITaskProgressService taskProgressService,
        IProcessExecutor processExecutor,
        IDialogService dialogService,
        ILocalizationService localizationService,
        IFileSystemService fileSystemService,
        IFilePickerService filePickerService,
        ILogService logService,
        IResourceService resourceService)
    {
        _oscdimgToolManager = oscdimgToolManager;
        _isoService = isoService;
        _taskProgressService = taskProgressService;
        _processExecutor = processExecutor;
        _dialogService = dialogService;
        _localizationService = localizationService;
        _fileSystemService = fileSystemService;
        _filePickerService = filePickerService;
        _logService = logService;
        _resourceService = resourceService;

        OutputIsoPath = string.Empty;

        CreateActionCards();
    }

    private void CreateActionCards()
    {
        DownloadOscdimgCard = new WizardActionCard
        {
            IconPath = _resourceService.GetResourceIconPath("ToolBoxIconPath"),
            Title = _localizationService.GetString("WIMUtil_Card_DownloadOscdimg_Title"),
            Description = _localizationService.GetString("WIMUtil_Card_DownloadOscdimg_Description"),
            ButtonText = _localizationService.GetString("WIMUtil_Card_DownloadOscdimg_Button"),
            ButtonCommand = DownloadOscdimgCommand,
            IsEnabled = true
        };

        SelectOutputCard = new WizardActionCard
        {
            Icon = "\uE74E",
            Title = _localizationService.GetString("WIMUtil_Card_SelectOutput_Title"),
            Description = _localizationService.GetString("WIMUtil_Label_NoLocation"),
            ButtonText = _localizationService.GetString("WIMUtil_Card_SelectOutput_Button"),
            ButtonCommand = SelectIsoOutputLocationCommand,
            IsEnabled = true
        };
    }

    [RelayCommand]
    private async Task DownloadOscdimg()
    {
        if (_taskProgressService.IsTaskRunning)
        {
            await _dialogService.ShowWarningAsync(_localizationService.GetString("Cleaner_Wait"),
                _localizationService.GetString("Dialog_Warning"));
            return;
        }
        CancellationTokenSource? task = null;
        try
        {
            DownloadOscdimgCard.IsComplete = false;
            DownloadOscdimgCard.HasFailed = false;
            DownloadOscdimgCard.IsProcessing = true;
            DownloadOscdimgCard.IsEnabled = false;

            task = _taskProgressService.StartTask(_localizationService.GetString("WIMUtil_Status_InstallingOscdimg"), true);
            var progress = _taskProgressService.CreatePowerShellProgress();

            var success = await _oscdimgToolManager.EnsureOscdimgAvailableAsync(
                progress, task.Token);

            DownloadOscdimgCard.IsProcessing = false;

            if (success)
            {
                IsOscdimgAvailable = true;
                DownloadOscdimgCard.IsComplete = true;
                DownloadOscdimgCard.IsEnabled = false;
                DownloadOscdimgCard.ButtonText = _localizationService.GetString("WIMUtil_Button_OscdimgFound");
                DownloadOscdimgCard.Description = _localizationService.GetString("WIMUtil_Desc_OscdimgInstalled");
                await _dialogService.ShowInformationAsync(
                    _localizationService.GetString("WIMUtil_Msg_AdkInstallComplete"),
                    _localizationService.GetString("Dialog_Success") ?? "Success");
            }
            else
            {
                DownloadOscdimgCard.IsEnabled = true;
                DownloadOscdimgCard.HasFailed = true;
                await _dialogService.ShowErrorAsync(
                    _localizationService.GetString("WIMUtil_Msg_AdkInstallFailed"),
                    _localizationService.GetString("Dialog_Error") ?? "Error");
            }
        }
        catch (Exception ex)
        {
            _logService.LogError($"Error installing ADK: {ex.Message}", ex);
            DownloadOscdimgCard.IsProcessing = false;
            DownloadOscdimgCard.IsEnabled = true;
            DownloadOscdimgCard.HasFailed = true;
            await _dialogService.ShowErrorAsync(
                string.Format(_localizationService.GetString("WIMUtil_Msg_AdkInstallError"), ex.Message),
                _localizationService.GetString("Dialog_Error") ?? "Error");
        }
        finally
        {
            _taskProgressService.CompleteOwnedTask(task);
        }
    }

    [RelayCommand]
    private void SelectIsoOutputLocation()
    {
        var path = _filePickerService.PickSaveFile(
            ["ISO Files", "*.iso"],
            "WinOptimizer_Windows.iso",
            "iso");
        if (!string.IsNullOrEmpty(path))
        {
            OutputIsoPath = path;
            SelectOutputCard.Description = $"{_localizationService.GetString("WIMUtil_Label_Output")}: {_fileSystemService.GetFileName(OutputIsoPath)}";
        }
    }

    [RelayCommand]
    private async Task CreateIso()
    {
        if (_taskProgressService.IsTaskRunning)
        {
            await _dialogService.ShowWarningAsync(_localizationService.GetString("Cleaner_Wait"),
                _localizationService.GetString("Dialog_Warning"));
            return;
        }
        CancellationTokenSource? task = null;
        var workingDirectory = WorkingDirectory;
        var outputIsoPath = OutputIsoPath;
        var revision = _mediaRevision;
        try
        {
            if (!IsOscdimgAvailable)
            {
                await _dialogService.ShowWarningAsync(
                    _localizationService.GetString("WIMUtil_Msg_OscdimgRequired"),
                    _localizationService.GetString("Dialog_Warning") ?? "Warning");
                return;
            }

            if (string.IsNullOrEmpty(outputIsoPath))
            {
                await _dialogService.ShowWarningAsync(
                    _localizationService.GetString("WIMUtil_Msg_OutputRequired"),
                    _localizationService.GetString("Dialog_Warning") ?? "Warning");
                return;
            }

            if (string.IsNullOrEmpty(workingDirectory))
            {
                await _dialogService.ShowWarningAsync(
                    _localizationService.GetString("WIMUtil_Msg_WorkingDirectoryRequired"),
                    _localizationService.GetString("Dialog_Warning") ?? "Warning");
                return;
            }

            IsIsoCreated = false;
            SelectOutputCard.IsEnabled = false;
            SelectOutputCard.Opacity = 0.5;

            task = _taskProgressService.StartTask(_localizationService.GetString("WIMUtil_Status_CreatingIso"), true);
            var progress = _taskProgressService.CreatePowerShellProgress();

            var success = await _isoService.CreateIsoAsync(workingDirectory, outputIsoPath, progress, task.Token);

            SelectOutputCard.IsEnabled = true;
            SelectOutputCard.Opacity = 1.0;

            if (success)
            {
                IsIsoCreated = revision == _mediaRevision;
                SelectOutputCard.Description = _localizationService.GetString("WIMUtil_Desc_IsoCreatedSuccess");

                var openFolder = (await _dialogService.ShowConfirmationAsync(new ConfirmationRequest
                {
                    Message = string.Format(_localizationService.GetString("WIMUtil_Msg_IsoCreatedSuccess"), outputIsoPath),
                    Title = _localizationService.GetString("WIMUtil_Desc_IsoCreatedSuccess"),
                    ConfirmButtonText = _localizationService.GetString("WIMUtil_Button_OpenFolder"),
                    CancelButtonText = _localizationService.GetString("Button_Close"),
                })).Confirmed;
                if (openFolder)
                {
                    _processExecutor.ShellExecuteAsync("explorer.exe", $"/select,\"{outputIsoPath}\"").FireAndForget(_logService);
                }
            }
            else
            {
                SelectOutputCard.Description = _localizationService.GetString("WIMUtil_Desc_IsoCreateFailed");
                await _dialogService.ShowErrorAsync(
                    _localizationService.GetString("WIMUtil_Msg_IsoCreationFailed"),
                    _localizationService.GetString("Dialog_Error") ?? "Error");
            }
        }
        catch (OperationCanceledException)
        {
            SelectOutputCard.IsEnabled = true;
            SelectOutputCard.Opacity = 1.0;
            SelectOutputCard.Description = _localizationService.GetString("WIMUtil_Desc_IsoCreateCancelled");
        }
        catch (InsufficientDiskSpaceException spaceEx)
        {
            SelectOutputCard.IsEnabled = true;
            SelectOutputCard.Opacity = 1.0;
            SelectOutputCard.Description = string.Format(_localizationService.GetString("WIMUtil_Status_InsufficientDiskSpace"), spaceEx.DriveName);
            await _dialogService.ShowWarningAsync(
                string.Format(_localizationService.GetString("WIMUtil_Msg_InsufficientSpace_Create"), spaceEx.DriveName, spaceEx.RequiredGB.ToString("F2"), spaceEx.AvailableGB.ToString("F2"), (spaceEx.RequiredGB - spaceEx.AvailableGB).ToString("F2")),
                string.Format(_localizationService.GetString("WIMUtil_Status_InsufficientDiskSpace"), spaceEx.DriveName));
        }
        catch (Exception ex)
        {
            SelectOutputCard.IsEnabled = true;
            SelectOutputCard.Opacity = 1.0;
            SelectOutputCard.Description = string.Format(_localizationService.GetString("WIMUtil_Status_ErrorPrefix"), ex.Message);
            await _dialogService.ShowErrorAsync(
                string.Format(_localizationService.GetString("WIMUtil_Msg_IsoCreationError"), ex.Message),
                _localizationService.GetString("Dialog_Error") ?? "Error");
        }
        finally
        {
            _taskProgressService.CompleteOwnedTask(task);
        }
    }

    public void UpdateDownloadOscdimgCardState()
    {
        if (IsOscdimgAvailable)
        {
            DownloadOscdimgCard.IsEnabled = false;
            DownloadOscdimgCard.IsComplete = true;
            DownloadOscdimgCard.ButtonText = _localizationService.GetString("WIMUtil_Button_OscdimgFound");
            DownloadOscdimgCard.Description = _localizationService.GetString("WIMUtil_Desc_OscdimgFound");
        }
        else
        {
            DownloadOscdimgCard.IsEnabled = true;
            DownloadOscdimgCard.IsComplete = false;
            DownloadOscdimgCard.ButtonText = _localizationService.GetString("WIMUtil_Button_Download");
            DownloadOscdimgCard.Description = _localizationService.GetString("WIMUtil_Card_DownloadOscdimg_Description");
            DownloadOscdimgCard.IconPath = _resourceService.GetResourceIconPath("ToolBoxIconPath");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }

}
