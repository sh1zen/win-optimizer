using CommunityToolkit.Mvvm.ComponentModel;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Features.AdvancedTools.Models;

namespace WinOptimizer.UI.Features.AdvancedTools.ViewModels;

/// <summary>
/// ViewModel for the AdvancedTools page, coordinating sub-feature navigation.
/// </summary>
public partial class AdvancedToolsViewModel : ObservableObject, IDisposable
{
    private bool _disposed;
    private readonly ILocalizationService _localizationService;

    [ObservableProperty]
    public partial string CurrentSectionKey { get; set; }

    /// <summary>
    /// Gets the localized page title.
    /// </summary>
    public string PageTitle => _localizationService.GetString("Nav_AdvancedTools");

    /// <summary>
    /// Gets the localized page description.
    /// </summary>
    public string PageDescription => _localizationService.GetString("Category_AdvancedTools_StatusText");

    /// <summary>
    /// Gets whether navigation is currently on a detail page (not overview).
    /// </summary>
    public bool IsInDetailPage => CurrentSectionKey != "Overview";

    /// <summary>
    /// Gets the localized WIM Utility display name for overview card.
    /// </summary>
    public string WimUtilDisplayName => _localizationService.GetString("WIMUtil_Title") ?? "WIMUtil";

    /// <summary>
    /// Gets the localized WIM Utility description for overview card.
    /// </summary>
    public string WimUtilDescription => _localizationService.GetString("WIMUtil_Subtitle") ?? "Create Custom Windows Installation Media";

    /// <summary>
    /// Gets the localized Autounattend XML display name for overview card.
    /// </summary>
    public string AutounattendXmlDisplayName => _localizationService.GetString("AdvancedTools_MenuItem_CreateXML") ?? "Create Autounattend XML";

    /// <summary>
    /// Gets the localized Autounattend XML description for overview card.
    /// </summary>
    public string AutounattendXmlDescription => _localizationService.GetString("AdvancedTools_GenerateCard_Description") ?? "Generate an autounattend.xml file based on your current win-optimizer selections to customize Windows during installation.";

    /// <summary>Gets the localized title of the restore destination.</summary>
    public string RestoreDisplayName => _localizationService.GetString("Nav_Restore") ?? "Restore";

    /// <summary>Gets the localized description of configuration and system recovery.</summary>
    public string RestoreDescription => _localizationService.GetString("Restore_Description");

    /// <summary>Gets the localized title of the system information destination.</summary>
    public string SystemInformationDisplayName => _localizationService.GetString("Nav_SystemInformation") ?? "System information";

    /// <summary>Gets the localized description of the machine inventory.</summary>
    public string SystemInformationDescription => _localizationService.GetString("SystemInfo_Description");

    /// <summary>
    /// Section definitions for navigation.
    /// </summary>
    public static readonly IReadOnlyList<AdvancedToolsSectionInfo> Sections = new List<AdvancedToolsSectionInfo>()
    {
        new("WimUtil", "WimUtilIconPath", "WIMUtil"),
        new("AutounattendXml", "AutounattendXmlIconPath", "Create Autounattend XML"),
    };

    public AdvancedToolsViewModel(ILocalizationService localizationService)
    {
        _localizationService = localizationService;
        CurrentSectionKey = "Overview";
        _localizationService.LanguageChanged += OnLanguageChanged;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _localizationService.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageDescription));
        OnPropertyChanged(nameof(WimUtilDisplayName));
        OnPropertyChanged(nameof(WimUtilDescription));
        OnPropertyChanged(nameof(AutounattendXmlDisplayName));
        OnPropertyChanged(nameof(RestoreDisplayName));
        OnPropertyChanged(nameof(RestoreDescription));
        OnPropertyChanged(nameof(SystemInformationDisplayName));
        OnPropertyChanged(nameof(SystemInformationDescription));
    }

    partial void OnCurrentSectionKeyChanged(string value)
    {
        OnPropertyChanged(nameof(IsInDetailPage));
    }
}
