using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinOptimizer.Core.Features.Common.Extensions;
using WinOptimizer.Core.Features.AdvancedTools.Interfaces;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.AdvancedTools.Models;
using WinOptimizer.UI.Features.Common.Interfaces;

namespace WinOptimizer.UI.Features.AdvancedTools.ViewModels;

/// <summary>
/// Sub-ViewModel for WIM Utility Step 3: driver extraction and injection.
/// </summary>
public partial class WimStep3DriversViewModel : ObservableObject, IDisposable
{
    /// <summary>Invalidates artifacts built from the media before a driver integration attempt.</summary>
    public event Action<string>? MediaContentChanging;
    private readonly IWimCustomizationService _wimCustomizationService;
    private readonly ITaskProgressService _taskProgressService;
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

    partial void OnWorkingDirectoryChanged(string value) => ResetForMediaChange();

    /// <summary>Clears driver evidence and source descriptions when the prepared media changes.</summary>
    public void ResetForMediaChange()
    {
        _mediaRevision++;
        AreDriversAdded = false;
        ExtractSystemDriversCard.IsComplete = SelectCustomDriversCard.IsComplete = false;
        ExtractSystemDriversCard.HasFailed = SelectCustomDriversCard.HasFailed = false;
        ExtractSystemDriversCard.Description = _localizationService.GetString("WIMUtil_Card_ExtractDrivers_Description");
        SelectCustomDriversCard.Description = _localizationService.GetString("WIMUtil_Card_CustomDrivers_Description");
    }

    [ObservableProperty]
    public partial bool AreDriversAdded { get; set; }

    public WizardActionCard ExtractSystemDriversCard { get; private set; } = new();
    public WizardActionCard SelectCustomDriversCard { get; private set; } = new();

    public WimStep3DriversViewModel(
        IWimCustomizationService wimCustomizationService,
        ITaskProgressService taskProgressService,
        IDialogService dialogService,
        ILocalizationService localizationService,
        IFileSystemService fileSystemService,
        IFilePickerService filePickerService,
        ILogService logService,
        IResourceService resourceService)
    {
        _wimCustomizationService = wimCustomizationService;
        _taskProgressService = taskProgressService;
        _dialogService = dialogService;
        _localizationService = localizationService;
        _fileSystemService = fileSystemService;
        _filePickerService = filePickerService;
        _logService = logService;
        _resourceService = resourceService;

        CreateActionCards();
    }

    private void CreateActionCards()
    {
        ExtractSystemDriversCard = new WizardActionCard
        {
            IconPath = _resourceService.GetResourceIconPath("MemoryArrowDownIconPath"),
            Title = _localizationService.GetString("WIMUtil_Card_ExtractDrivers_Title"),
            Description = _localizationService.GetString("WIMUtil_Card_ExtractDrivers_Description"),
            ButtonText = _localizationService.GetString("WIMUtil_Card_ExtractDrivers_Button"),
            ButtonCommand = ExtractAndAddSystemDriversCommand,
            IsEnabled = true
        };

        SelectCustomDriversCard = new WizardActionCard
        {
            IconPath = _resourceService.GetResourceIconPath("ExplorerIconPath"),
            Title = _localizationService.GetString("WIMUtil_Card_CustomDrivers_Title"),
            Description = _localizationService.GetString("WIMUtil_Card_CustomDrivers_Description"),
            ButtonText = _localizationService.GetString("WIMUtil_Card_CustomDrivers_Button"),
            ButtonCommand = SelectAndAddCustomDriversCommand,
            IsEnabled = true
        };
    }

    [RelayCommand]
    private async Task ExtractAndAddSystemDrivers()
    {
        if (_taskProgressService.IsTaskRunning)
        {
            await _dialogService.ShowWarningAsync(_localizationService.GetString("Cleaner_Wait"), _localizationService.GetString("Dialog_Warning"));
            return;
        }
        CancellationTokenSource? task = null;
        var workingDirectory = WorkingDirectory;
        var revision = _mediaRevision;
        try
        {
            ExtractSystemDriversCard.IsComplete = false;
            ExtractSystemDriversCard.HasFailed = false;

            if (string.IsNullOrEmpty(workingDirectory))
            {
                await _dialogService.ShowWarningAsync(
                    _localizationService.GetString("WIMUtil_Msg_WorkingDirectoryRequired"),
                    _localizationService.GetString("Dialog_Warning") ?? "Warning");
                return;
            }

            var confirmed = (await _dialogService.ShowConfirmationAsync(new ConfirmationRequest
            {
                Message = _localizationService.GetString("WIMUtil_Msg_ExtractDriversConfirm"),
                Title = _localizationService.GetString("WIMUtil_Card_ExtractDrivers_Title"),
                ConfirmButtonText = "Yes",
                CancelButtonText = "No",
            })).Confirmed;
            if (!confirmed) return;
            if (_taskProgressService.IsTaskRunning)
            {
                await _dialogService.ShowWarningAsync(_localizationService.GetString("Cleaner_Wait"), _localizationService.GetString("Dialog_Warning"));
                return;
            }

            if (revision != _mediaRevision) return;
            ExtractSystemDriversCard.IsProcessing = true;
            ExtractSystemDriversCard.IsEnabled = false;

            task = _taskProgressService.StartTask(_localizationService.GetString("WIMUtil_Status_ExportingDrivers"), true);
            var progress = _taskProgressService.CreatePowerShellProgress();

            MediaContentChanging?.Invoke(workingDirectory);
            var success = await _wimCustomizationService.AddDriversAsync(
                workingDirectory, null, progress, task.Token);

            ExtractSystemDriversCard.IsProcessing = false;
            ExtractSystemDriversCard.IsEnabled = true;

            if (success)
            {
                if (revision == _mediaRevision)
                    AreDriversAdded = ExtractSystemDriversCard.IsComplete = true;
                await _dialogService.ShowInformationAsync(
                    _localizationService.GetString("WIMUtil_Msg_DriversSuccess"),
                    _localizationService.GetString("Dialog_Success") ?? "Success");
            }
            else
            {
                ExtractSystemDriversCard.HasFailed = true;
                await _dialogService.ShowWarningAsync(
                    _localizationService.GetString("WIMUtil_Msg_NoDriversFound"),
                    _localizationService.GetString("Dialog_Warning") ?? "Warning");
            }
        }
        catch (Exception ex)
        {
            _logService.LogError($"Error extracting system drivers: {ex.Message}", ex);
            ExtractSystemDriversCard.IsProcessing = false;
            ExtractSystemDriversCard.IsEnabled = true;
            ExtractSystemDriversCard.HasFailed = true;
            await _dialogService.ShowErrorAsync(
                string.Format(_localizationService.GetString("WIMUtil_Msg_DriverExtractionError"), ex.Message),
                _localizationService.GetString("Dialog_Error") ?? "Error");
        }
        finally
        {
            _taskProgressService.CompleteOwnedTask(task);
        }
    }

    [RelayCommand]
    private async Task SelectAndAddCustomDrivers()
    {
        if (_taskProgressService.IsTaskRunning)
        {
            await _dialogService.ShowWarningAsync(_localizationService.GetString("Cleaner_Wait"), _localizationService.GetString("Dialog_Warning"));
            return;
        }
        CancellationTokenSource? task = null;
        var workingDirectory = WorkingDirectory;
        var revision = _mediaRevision;
        try
        {
            SelectCustomDriversCard.IsComplete = false;
            SelectCustomDriversCard.HasFailed = false;

            if (string.IsNullOrEmpty(workingDirectory))
            {
                await _dialogService.ShowWarningAsync(
                    _localizationService.GetString("WIMUtil_Msg_WorkingDirectoryRequired"),
                    _localizationService.GetString("Dialog_Warning") ?? "Warning");
                return;
            }

            var selectedPath = _filePickerService.PickFolder(_localizationService.GetString("WIMUtil_FolderDialog_SelectDrivers"));
            if (string.IsNullOrEmpty(selectedPath)) return;

            if (!_fileSystemService.DirectoryExists(selectedPath))
            {
                SelectCustomDriversCard.HasFailed = true;
                await _dialogService.ShowErrorAsync(
                    _localizationService.GetString("WIMUtil_Msg_InvalidFolder"),
                    _localizationService.GetString("Dialog_Error") ?? "Error");
                return;
            }

            var hasFiles = _fileSystemService.GetFiles(selectedPath, "*", SearchOption.AllDirectories).Length > 0 || _fileSystemService.GetDirectories(selectedPath, "*", SearchOption.AllDirectories).Length > 0;
            if (!hasFiles)
            {
                SelectCustomDriversCard.HasFailed = true;
                await _dialogService.ShowWarningAsync(
                    _localizationService.GetString("WIMUtil_Msg_EmptyFolder"),
                    _localizationService.GetString("Dialog_Warning") ?? "Warning");
                return;
            }
            if (_taskProgressService.IsTaskRunning)
            {
                await _dialogService.ShowWarningAsync(_localizationService.GetString("Cleaner_Wait"), _localizationService.GetString("Dialog_Warning"));
                return;
            }

            if (revision != _mediaRevision) return;
            SelectCustomDriversCard.IsProcessing = true;
            SelectCustomDriversCard.IsEnabled = false;
            SelectCustomDriversCard.Description = $"{_localizationService.GetString("WIMUtil_Label_Selected")}: {selectedPath}";

            task = _taskProgressService.StartTask(_localizationService.GetString("WIMUtil_Status_AddingCustomDrivers"), true);
            var progress = _taskProgressService.CreatePowerShellProgress();

            MediaContentChanging?.Invoke(workingDirectory);
            var success = await _wimCustomizationService.AddDriversAsync(
                workingDirectory, selectedPath, progress, task.Token);

            SelectCustomDriversCard.IsProcessing = false;
            SelectCustomDriversCard.IsEnabled = true;

            if (success)
            {
                if (revision == _mediaRevision)
                    AreDriversAdded = SelectCustomDriversCard.IsComplete = true;
                await _dialogService.ShowInformationAsync(
                    _localizationService.GetString("WIMUtil_Msg_DriverFilesAdded"),
                    _localizationService.GetString("Dialog_Success") ?? "Success");
            }
            else
            {
                SelectCustomDriversCard.HasFailed = true;
                await _dialogService.ShowWarningAsync(
                    string.Format(_localizationService.GetString("WIMUtil_Msg_NoCustomDrivers"), selectedPath),
                    _localizationService.GetString("Dialog_Warning") ?? "Warning");
            }
        }
        catch (Exception ex)
        {
            _logService.LogError($"Error adding custom drivers: {ex.Message}", ex);
            SelectCustomDriversCard.IsProcessing = false;
            SelectCustomDriversCard.IsEnabled = true;
            SelectCustomDriversCard.HasFailed = true;
            await _dialogService.ShowErrorAsync(
                string.Format(_localizationService.GetString("WIMUtil_Msg_DriverAdditionError"), ex.Message),
                _localizationService.GetString("Dialog_Error") ?? "Error");
        }
        finally
        {
            _taskProgressService.CompleteOwnedTask(task);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }

}
