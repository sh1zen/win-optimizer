using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Events;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Customize.Interfaces;
using WinOptimizer.UI.Features.Optimize.ViewModels;
using ISettingsLoadingService = WinOptimizer.UI.Features.Common.Interfaces.ISettingsLoadingService;

namespace WinOptimizer.UI.Features.Customize.ViewModels;

public partial class WindowsThemeCustomizationsViewModel : BaseSettingsFeatureViewModel, ICustomizationFeatureViewModel
{
    public WindowsThemeCustomizationsViewModel(
        ISettingsLoadingService settingsLoadingService,
        ILogService logService,
        ILocalizationService localizationService,
        IDispatcherService dispatcherService,
        IEventBus eventBus,
        IApplicationModeService applicationModeService)
        : base(settingsLoadingService, logService, localizationService, dispatcherService, eventBus, applicationModeService)
    {
    }

    public override string ModuleId => FeatureIds.WindowsTheme;

    protected override string GetDisplayNameKey() => "Feature_WindowsTheme_Name";
}
