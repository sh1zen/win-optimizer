using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.ViewModels;
using WinOptimizer.UI.Features.Customize.Interfaces;
using WinOptimizer.UI.Features.Customize.Models;

namespace WinOptimizer.UI.Features.Customize.ViewModels;

/// <summary>
/// ViewModel for the Customize page, coordinating all customization feature ViewModels.
/// </summary>
public partial class CustomizeViewModel : SectionPageViewModel<CustomizeSectionInfo>
{
    protected override string PageTitleKey => "Category_Customize_Title";
    protected override string PageDescriptionKey => "Category_Customize_StatusText";
    protected override string LogPrefix => "CustomizeViewModel";
    protected override IReadOnlyList<CustomizeSectionInfo> SectionDefinitions => Sections;

    /// <summary>
    /// Section definitions for navigation.
    /// </summary>
    public static readonly IReadOnlyList<CustomizeSectionInfo> Sections = new List<CustomizeSectionInfo>()
    {
        new("Explorer", "ExplorerIconPath", "Explorer", FeatureIds.ExplorerCustomization),
        new("StartMenu", "StartMenuIconPath", "Start Menu", FeatureIds.StartMenu),
        new("Taskbar", "TaskbarIconPath", "Taskbar", FeatureIds.Taskbar),
        new("WindowsTheme", "WindowsThemeIconPath", "Windows Theme", FeatureIds.WindowsTheme),
    };

    // Named properties for XAML binding (typed as interface, not concrete)
    public ISettingsFeatureViewModel ExplorerViewModel { get; }
    public ISettingsFeatureViewModel StartMenuViewModel { get; }
    public ISettingsFeatureViewModel TaskbarViewModel { get; }
    public ISettingsFeatureViewModel WindowsThemeViewModel { get; }

    public CustomizeViewModel(
        ILogService logService,
        ILocalizationService localizationService,
        IEnumerable<ICustomizationFeatureViewModel> featureViewModels)
        : base(logService, localizationService, featureViewModels.Cast<ISettingsFeatureViewModel>())
    {
        InitializeSectionMappings();

        ExplorerViewModel = GetFeatureByModuleId(FeatureIds.ExplorerCustomization);
        StartMenuViewModel = GetFeatureByModuleId(FeatureIds.StartMenu);
        TaskbarViewModel = GetFeatureByModuleId(FeatureIds.Taskbar);
        WindowsThemeViewModel = GetFeatureByModuleId(FeatureIds.WindowsTheme);
    }
}
