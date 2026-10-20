using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Events;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Customize.Interfaces;
using WinOptimizer.UI.Features.Optimize.ViewModels;
using ISettingsLoadingService = WinOptimizer.UI.Features.Common.Interfaces.ISettingsLoadingService;

namespace WinOptimizer.UI.Features.Customize.ViewModels;

public partial class TaskbarCustomizationsViewModel : BaseSettingsFeatureViewModel, ICustomizationFeatureViewModel
{
    public TaskbarCustomizationsViewModel(
        ISettingsLoadingService settingsLoadingService,
        ILogService logService,
        ILocalizationService localizationService,
        IDispatcherService dispatcherService,
        IEventBus eventBus,
        IApplicationModeService applicationModeService)
        : base(settingsLoadingService, logService, localizationService, dispatcherService, eventBus, applicationModeService)
    {
    }

    public override string ModuleId => FeatureIds.Taskbar;

    protected override string GetDisplayNameKey() => "Feature_Taskbar_Name";
}
