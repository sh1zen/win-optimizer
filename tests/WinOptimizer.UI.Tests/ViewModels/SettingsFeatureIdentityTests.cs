using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Events;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Customize.Interfaces;
using WinOptimizer.UI.Features.Customize.ViewModels;
using WinOptimizer.UI.Features.Optimize.Interfaces;
using WinOptimizer.UI.Features.Optimize.ViewModels;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

/// <summary>Checks the feature-specific contracts of settings pages with shared behavior.</summary>
public class SettingsFeatureIdentityTests
{
    [Theory]
    [InlineData(FeatureIds.ExplorerCustomization, "Feature_Explorer_Name", false)]
    [InlineData(FeatureIds.StartMenu, "Feature_StartMenu_Name", false)]
    [InlineData(FeatureIds.Taskbar, "Feature_Taskbar_Name", false)]
    [InlineData(FeatureIds.WindowsTheme, "Feature_WindowsTheme_Name", false)]
    [InlineData(FeatureIds.GamingPerformance, "Feature_GamingPerformance_Name", true)]
    [InlineData(FeatureIds.Notifications, "Feature_Notifications_Name", true)]
    [InlineData(FeatureIds.Privacy, "Feature_Privacy_Name", true)]
    [InlineData(FeatureIds.Sound, "Feature_Sound_Name", true)]
    [InlineData(FeatureIds.Update, "Feature_Update_Name", true)]
    public void Feature_UsesItsModuleAndLocalizedName(string featureId, string nameKey, bool optimization)
    {
        var loading = Mock.Of<ISettingsLoadingService>();
        var log = Mock.Of<ILogService>();
        var localization = new Mock<ILocalizationService>();
        localization.Setup(l => l.GetString(nameKey)).Returns("Localized feature");
        var dispatcher = Mock.Of<IDispatcherService>();
        var events = Mock.Of<IEventBus>();
        var mode = Mock.Of<IApplicationModeService>();

        using BaseSettingsFeatureViewModel feature = featureId switch
        {
            FeatureIds.ExplorerCustomization => new ExplorerCustomizationsViewModel(loading, log, localization.Object, dispatcher, events, mode),
            FeatureIds.StartMenu => new StartMenuCustomizationsViewModel(loading, log, localization.Object, dispatcher, events, mode),
            FeatureIds.Taskbar => new TaskbarCustomizationsViewModel(loading, log, localization.Object, dispatcher, events, mode),
            FeatureIds.WindowsTheme => new WindowsThemeCustomizationsViewModel(loading, log, localization.Object, dispatcher, events, mode),
            FeatureIds.GamingPerformance => new GamingOptimizationsViewModel(loading, log, localization.Object, dispatcher, events, mode),
            FeatureIds.Notifications => new NotificationOptimizationsViewModel(loading, log, localization.Object, dispatcher, events, mode),
            FeatureIds.Privacy => new PrivacyOptimizationsViewModel(loading, log, localization.Object, dispatcher, events, mode),
            FeatureIds.Sound => new SoundOptimizationsViewModel(loading, log, localization.Object, dispatcher, events, mode),
            FeatureIds.Update => new UpdateOptimizationsViewModel(loading, log, localization.Object, dispatcher, events, mode),
            _ => throw new ArgumentOutOfRangeException(nameof(featureId)),
        };

        feature.ModuleId.Should().Be(featureId);
        feature.DisplayName.Should().Be("Localized feature");
        if (optimization)
            feature.Should().BeAssignableTo<IOptimizationFeatureViewModel>();
        else
            feature.Should().BeAssignableTo<ICustomizationFeatureViewModel>();
    }
}
