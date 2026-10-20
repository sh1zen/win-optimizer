using System;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinOptimizer.Core.Features.AdvancedTools.Interfaces;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Features.AdvancedTools.Models;
using WinOptimizer.UI.Features.Common.Interfaces;

namespace WinOptimizer.UI.Features.AdvancedTools.ViewModels;

/// <summary>
/// Thin orchestrator ViewModel for the WIM Utility wizard.
/// Owns wizard navigation state and delegates step-specific work to sub-ViewModels.
/// </summary>
public partial class WimUtilViewModel : ObservableObject, IDisposable
{
    private readonly IOscdimgToolManager _oscdimgToolManager;
    private readonly ILocalizationService _localizationService;
    private readonly IDispatcherService _dispatcherService;
    private readonly IFileSystemService _fileSystemService;
    private bool _disposed;

    // Steps 2-4 auto-expand once per completed extraction; the flag stops a
    // later refresh from re-opening steps the user has manually collapsed.
    private bool _autoExpandApplied;

    // ── Sub-ViewModels (public for XAML binding) ──────────────────────

    public WimStep1ViewModel Step1 { get; }
    public WimImageFormatViewModel ImageFormat { get; }
    public WimStep2XmlViewModel Step2 { get; }
    public WimStep3DriversViewModel Step3 { get; }
    public WimStep4IsoViewModel Step4 { get; }

    // ── Wizard navigation state ──────────────────────────────────────

    [ObservableProperty]
    public partial WizardStepState Step1State { get; set; }

    [ObservableProperty]
    public partial WizardStepState Step2State { get; set; }

    [ObservableProperty]
    public partial WizardStepState Step3State { get; set; }

    [ObservableProperty]
    public partial WizardStepState Step4State { get; set; }

    // ── Localization labels (read-only, for XAML) ────────────────────

    public string Title => _localizationService?.GetString("WIMUtil_Title") ?? "Windows Installation Media Utility";
    public string CheckboxExtractedAlreadyText => _localizationService.GetString("WIMUtil_CheckboxExtractedAlready");
    public string ButtonSelectFolderText => _localizationService.GetString("WIMUtil_ButtonSelectFolder");
    public string ButtonStartExtractionText => _localizationService.GetString("WIMUtil_ButtonStartExtraction");
    public string OptionalConvertText => _localizationService.GetString("WIMUtil_OptionalConvert");
    public string BothImagesTitle => _localizationService.GetString("WIMUtil_Card_BothImages_Title");
    public string BothImagesDescription => _localizationService.GetString("WIMUtil_Card_BothImages_Description");
    public string ButtonDeleteWimText => _localizationService.GetString("WIMUtil_Button_DeleteWim");
    public string ButtonDeleteEsdText => _localizationService.GetString("WIMUtil_Button_DeleteEsd");
    public string DownloadIsoText => _localizationService.GetString("WIMUtil_DownloadISO");
    public string ButtonWindows10Text => _localizationService.GetString("WIMUtil_ButtonWindows10");
    public string ButtonWindows11Text => _localizationService.GetString("WIMUtil_ButtonWindows11");
    public string TooltipDownloadWindows10 => _localizationService.GetString("WIMUtil_Tooltip_DownloadWindows10");
    public string TooltipDownloadWindows11 => _localizationService.GetString("WIMUtil_Tooltip_DownloadWindows11");
    public string SelectOneOptionText => _localizationService.GetString("WIMUtil_SelectOneOption");
    public string ButtonGenerateText => _localizationService.GetString("WIMUtil_ButtonGenerate");
    public string GenerateXmlFilesText => _localizationService.GetString("WIMUtil_GenerateXMLFiles");
    public string ButtonSchneegansText => _localizationService.GetString("WIMUtil_ButtonSchneegans");
    public string TooltipSchneegans => _localizationService.GetString("WIMUtil_Tooltip_Schneegans");
    public string ButtonCreateIsoText => _localizationService.GetString("WIMUtil_ButtonCreateISO");

    // ── Constructor ──────────────────────────────────────────────────

    public WimUtilViewModel(
        IOscdimgToolManager oscdimgToolManager,
        IIsoService isoService,
        IWimImageService wimImageService,
        IWimCustomizationService wimCustomizationService,
        ITaskProgressService taskProgressService,
        IDialogService dialogService,
        ILogService logService,
        IAutounattendXmlGeneratorService xmlGeneratorService,
        ISelectedAppsProvider selectedAppsProvider,
        ILocalizationService localizationService,
        IDispatcherService dispatcherService,
        IProcessExecutor processExecutor,
        IFileSystemService fileSystemService,
        IFilePickerService filePickerService,
        IResourceService resourceService)
    {
        _oscdimgToolManager = oscdimgToolManager;
        _localizationService = localizationService;
        _dispatcherService = dispatcherService;
        _fileSystemService = fileSystemService;

        // Create sub-ViewModels
        Step1 = new WimStep1ViewModel(
            isoService, taskProgressService, dialogService,
            localizationService, fileSystemService, filePickerService, logService,
            resourceService);

        ImageFormat = new WimImageFormatViewModel(
            wimImageService, taskProgressService, dialogService,
            dispatcherService, localizationService, logService);

        Step2 = new WimStep2XmlViewModel(
            xmlGeneratorService, wimCustomizationService, selectedAppsProvider,
            dialogService, localizationService, fileSystemService, filePickerService, logService,
            resourceService);

        Step3 = new WimStep3DriversViewModel(
            wimCustomizationService, taskProgressService, dialogService,
            localizationService, fileSystemService, filePickerService, logService,
            resourceService);

        Step4 = new WimStep4IsoViewModel(
            oscdimgToolManager, isoService, taskProgressService, processExecutor,
            dialogService, localizationService, fileSystemService, filePickerService, logService,
            resourceService);

        // Initialize wizard state
        Step1State = new WizardStepState();
        Step2State = new WizardStepState();
        Step3State = new WizardStepState();
        Step4State = new WizardStepState();

        InitializeStepStates();

        // Subscribe to language changes to update localized strings
        _localizationService.LanguageChanged += OnLanguageChanged;

        // Subscribe to sub-VM property changes to update wizard states
        Step1.PropertyChanged += OnSubViewModelPropertyChanged;
        ImageFormat.PropertyChanged += OnSubViewModelPropertyChanged;
        Step2.PropertyChanged += OnSubViewModelPropertyChanged;
        Step3.PropertyChanged += OnSubViewModelPropertyChanged;
        Step4.PropertyChanged += OnSubViewModelPropertyChanged;

        // Propagate Step1's initial WorkingDirectory to sub-VMs.
        // The constructor assignment above fires before subscriptions,
        // so without this, sub-VMs never receive the default value.
        var initialWorkingDir = Step1.WorkingDirectory;
        ImageFormat.WorkingDirectory = initialWorkingDir;
        Step2.WorkingDirectory = initialWorkingDir;
        Step3.WorkingDirectory = initialWorkingDir;
        Step4.WorkingDirectory = initialWorkingDir;
    }

    public async Task OnNavigatedToAsync()
    {
        Step4.IsOscdimgAvailable = await _oscdimgToolManager.IsOscdimgAvailableAsync();
        _dispatcherService.RunOnUIThread(Step4.UpdateDownloadOscdimgCardState);
        UpdateStepStates();
        RefreshStepExpansion();
    }

    // ── Wizard navigation ────────────────────────────────────────────

    [RelayCommand]
    private void NavigateToStep(string? stepParameter)
    {
        if (string.IsNullOrEmpty(stepParameter) || !int.TryParse(stepParameter, out int targetStep)) return;

        var state = GetStepState(targetStep);
        if (state is null || !IsStepAvailable(targetStep)) return;

        // Steps toggle independently — collapsing one leaves the others as-is.
        state.IsExpanded = !state.IsExpanded;
    }

    private WizardStepState? GetStepState(int step) => step switch
    {
        1 => Step1State,
        2 => Step2State,
        3 => Step3State,
        4 => Step4State,
        _ => null
    };

    private bool IsStepAvailable(int step) => step switch
    {
        1 => true,
        2 or 3 or 4 => Step1.IsExtractionComplete && !ImageFormat.IsConverting,
        _ => false
    };

    // ── Step state management ────────────────────────────────────────

    private void InitializeStepStates()
    {
        Step1State = new WizardStepState
        {
            StepNumber = 1,
            Title = _localizationService.GetString("WIMUtil_Step1_Title") ?? "Select ISO",
            Icon = "DiscPlayer",
            StatusText = _localizationService.GetString("WIMUtil_Status_NoIsoSelected"),
            IsExpanded = true,
            IsAvailable = true
        };

        Step2State = new WizardStepState
        {
            StepNumber = 2,
            Title = _localizationService.GetString("WIMUtil_Step2_Title") ?? "Add XML File",
            Icon = "FileCode",
            StatusText = _localizationService.GetString("WIMUtil_Status_CompleteStep1")
        };

        Step3State = new WizardStepState
        {
            StepNumber = 3,
            Title = _localizationService.GetString("WIMUtil_Step3_Title") ?? "Add Drivers",
            Icon = "Chip",
            StatusText = _localizationService.GetString("WIMUtil_Status_CompleteStep1")
        };

        Step4State = new WizardStepState
        {
            StepNumber = 4,
            Title = _localizationService.GetString("WIMUtil_Step4_Title") ?? "Create ISO",
            Icon = "WrenchClock",
            StatusText = _localizationService.GetString("WIMUtil_Status_CompleteStep1")
        };
    }

    private void UpdateStepStates()
    {
        var extractionComplete = Step1.IsExtractionComplete;
        var isConverting = ImageFormat.IsConverting;
        var isExtracting = Step1.IsExtracting;

        Step1State.IsAvailable = true;
        Step1State.IsComplete = extractionComplete && !isConverting;
        Step1State.StatusText = isConverting
            ? _localizationService.GetString("WIMUtil_Status_Converting")
            : extractionComplete
                ? _localizationService.GetString("WIMUtil_Status_IsoExtracted")
                : isExtracting
                    ? _localizationService.GetString("WIMUtil_Status_Extracting")
                    : !string.IsNullOrEmpty(Step1.SelectedIsoPath)
                        ? _localizationService.GetString("WIMUtil_Status_IsoSelected")
                        : _localizationService.GetString("WIMUtil_Status_NoIsoSelected");

        Step2State.IsAvailable = extractionComplete && !isConverting;
        Step2State.IsComplete = Step2.IsXmlAdded;
        Step2State.StatusText = isConverting
            ? _localizationService.GetString("WIMUtil_Status_WaitForConversion")
            : !extractionComplete
                ? _localizationService.GetString("WIMUtil_Status_CompleteStep1")
                : Step2.IsXmlAdded
                    ? _localizationService.GetString("WIMUtil_Status_XmlAdded")
                    : _localizationService.GetString("WIMUtil_Status_NoXmlAdded");

        Step3State.IsAvailable = extractionComplete && !isConverting;
        Step3State.IsComplete = Step3.AreDriversAdded;
        Step3State.StatusText = isConverting
            ? _localizationService.GetString("WIMUtil_Status_WaitForConversion")
            : !extractionComplete
                ? _localizationService.GetString("WIMUtil_Status_CompleteStep1")
                : Step3.AreDriversAdded
                    ? _localizationService.GetString("WIMUtil_Status_DriversAdded")
                    : _localizationService.GetString("WIMUtil_Status_NoDriversAdded");

        Step4State.IsAvailable = extractionComplete && !isConverting;
        Step4State.IsComplete = Step4.IsIsoCreated;
        Step4State.StatusText = isConverting
            ? _localizationService.GetString("WIMUtil_Status_WaitForConversion")
            : !extractionComplete
                ? _localizationService.GetString("WIMUtil_Status_CompleteStep1")
                : Step4.IsIsoCreated
                    ? _localizationService.GetString("WIMUtil_Status_IsoCreated")
                    : !string.IsNullOrEmpty(Step4.OutputIsoPath)
                        ? $"{_localizationService.GetString("WIMUtil_Label_Output")}: {_fileSystemService.GetFileName(Step4.OutputIsoPath)}"
                        : _localizationService.GetString("WIMUtil_Status_ReadyToCreateIso");

        OnPropertyChanged(nameof(Step1State));
        OnPropertyChanged(nameof(Step2State));
        OnPropertyChanged(nameof(Step3State));
        OnPropertyChanged(nameof(Step4State));
    }

    /// <summary>
    /// Opens the right step panels for the current extraction state. Once the
    /// ISO is extracted, steps 2-4 expand so the user sees every remaining task
    /// — including step 4's oscdimg download card — without expanding panels by
    /// hand. Step 1 is left alone (it stays expanded, showing its result).
    /// Applied once per completed-extraction state; the user can freely
    /// collapse/re-expand afterwards. Restarting extraction resets it.
    /// </summary>
    private void RefreshStepExpansion()
    {
        if (Step1.IsExtractionComplete)
        {
            if (_autoExpandApplied) return;
            _autoExpandApplied = true;

            Step2State.IsExpanded = true;
            Step3State.IsExpanded = true;
            Step4State.IsExpanded = true;
        }
        else
        {
            _autoExpandApplied = false;

            Step1State.IsExpanded = true;
            Step2State.IsExpanded = false;
            Step3State.IsExpanded = false;
            Step4State.IsExpanded = false;
        }
    }

    // ── Sub-VM observation ───────────────────────────────────────────

    private void OnSubViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Propagate working directory to sub-VMs that need it
        if (sender == Step1)
        {
            switch (e.PropertyName)
            {
                case nameof(WimStep1ViewModel.WorkingDirectory):
                    var wd = Step1.WorkingDirectory;
                    ImageFormat.WorkingDirectory = wd;
                    Step2.WorkingDirectory = wd;
                    Step3.WorkingDirectory = wd;
                    Step4.WorkingDirectory = wd;
                    break;

                case nameof(WimStep1ViewModel.IsExtractionComplete):
                    if (Step1.IsExtractionComplete)
                    {
                        _ = ImageFormat.SafeDetectImageFormatAsync();
                    }
                    RefreshStepExpansion();
                    break;
            }
        }

        // Update wizard step states whenever completion flags change
        UpdateStepStates();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // Update step state titles and status text
        Step1State.Title = _localizationService.GetString("WIMUtil_Step1_Title") ?? "Select ISO";
        Step2State.Title = _localizationService.GetString("WIMUtil_Step2_Title") ?? "Add XML File";
        Step3State.Title = _localizationService.GetString("WIMUtil_Step3_Title") ?? "Add Drivers";
        Step4State.Title = _localizationService.GetString("WIMUtil_Step4_Title") ?? "Create ISO";
        UpdateStepStates();

        // Raise property changes for all localization label properties
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(CheckboxExtractedAlreadyText));
        OnPropertyChanged(nameof(ButtonSelectFolderText));
        OnPropertyChanged(nameof(ButtonStartExtractionText));
        OnPropertyChanged(nameof(OptionalConvertText));
        OnPropertyChanged(nameof(BothImagesTitle));
        OnPropertyChanged(nameof(BothImagesDescription));
        OnPropertyChanged(nameof(ButtonDeleteWimText));
        OnPropertyChanged(nameof(ButtonDeleteEsdText));
        OnPropertyChanged(nameof(DownloadIsoText));
        OnPropertyChanged(nameof(ButtonWindows10Text));
        OnPropertyChanged(nameof(ButtonWindows11Text));
        OnPropertyChanged(nameof(TooltipDownloadWindows10));
        OnPropertyChanged(nameof(TooltipDownloadWindows11));
        OnPropertyChanged(nameof(SelectOneOptionText));
        OnPropertyChanged(nameof(ButtonGenerateText));
        OnPropertyChanged(nameof(GenerateXmlFilesText));
        OnPropertyChanged(nameof(ButtonSchneegansText));
        OnPropertyChanged(nameof(TooltipSchneegans));
        OnPropertyChanged(nameof(ButtonCreateIsoText));
    }

    // ── IDisposable ─────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Unsubscribe from language changes
        _localizationService.LanguageChanged -= OnLanguageChanged;

        // Unsubscribe from sub-VM PropertyChanged events
        Step1.PropertyChanged -= OnSubViewModelPropertyChanged;
        ImageFormat.PropertyChanged -= OnSubViewModelPropertyChanged;
        Step2.PropertyChanged -= OnSubViewModelPropertyChanged;
        Step3.PropertyChanged -= OnSubViewModelPropertyChanged;
        Step4.PropertyChanged -= OnSubViewModelPropertyChanged;

        // Dispose sub-VMs that implement IDisposable
        (Step3 as IDisposable)?.Dispose();
        (Step4 as IDisposable)?.Dispose();
        (ImageFormat as IDisposable)?.Dispose();
    }
}
