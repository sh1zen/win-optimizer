using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Events;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Optimize.Interfaces;
namespace WinOptimizer.UI.Features.Optimize.ViewModels;

public partial class UpdateOptimizationsViewModel : BaseSettingsFeatureViewModel, IOptimizationFeatureViewModel
{
    public override string ModuleId => FeatureIds.Update;

    protected override string GetDisplayNameKey() => "Feature_Update_Name";

    public UpdateOptimizationsViewModel(
        ISettingsLoadingService settingsLoadingService,
        ILogService logService,
        ILocalizationService localizationService,
        IDispatcherService dispatcherService,
        IEventBus eventBus,
        IApplicationModeService applicationModeService)
        : base(settingsLoadingService, logService, localizationService, dispatcherService, eventBus, applicationModeService)
    {
    }
}
