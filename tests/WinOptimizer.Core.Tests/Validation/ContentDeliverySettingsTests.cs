using FluentAssertions;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Customize.Models;
using WinOptimizer.Core.Features.Optimize.Models;
using Xunit;

namespace WinOptimizer.Core.Tests.Validation;

/// <summary>Checks the individual Windows advertising and suggestion controls.</summary>
public class ContentDeliverySettingsTests
{
    [Fact]
    public void ContentDeliveryGroup_ContainsIndividualToggles()
    {
        var settings = PrivacyAndSecurityOptimizations.GetPrivacyAndSecurityOptimizations().Settings
            .Where(setting => setting.GroupName == "Content Delivery & Advertising")
            .ToList();

        settings.Should().HaveCount(8);
        settings.Should().OnlyContain(setting => setting.InputType == InputType.Toggle);
        settings.Should().OnlyContain(setting => setting.Dependencies.Count == 0);
    }

    [Theory]
    [InlineData("privacy-content-delivery-allowed", "ContentDeliveryAllowed", false)]
    [InlineData("privacy-subscribed-content", "SubscribedContentEnabled", false)]
    [InlineData("privacy-feature-management", "FeatureManagementEnabled", false)]
    [InlineData("privacy-soft-landing", "SoftLandingEnabled", false)]
    [InlineData("privacy-oem-preinstalled-apps", "OemPreInstalledAppsEnabled", false)]
    [InlineData("privacy-preinstalled-apps", "PreInstalledAppsEnabled", false)]
    [InlineData("privacy-preinstalled-apps-ever", "PreInstalledAppsEverEnabled", false)]
    [InlineData("privacy-silent-installed-apps", "SilentInstalledAppsEnabled", false)]
    [InlineData("privacy-rotating-lock-screen", "RotatingLockScreenEnabled", true)]
    [InlineData("privacy-lock-screen-overlay", "RotatingLockScreenOverlayEnabled", false)]
    [InlineData("privacy-settings-content", "SubscribedContent-338393Enabled", true)]
    [InlineData("privacy-timeline-suggestions", "SubscribedContent-353698Enabled", false)]
    [InlineData("notifications-welcome-experience", "SubscribedContent-310093Enabled", false)]
    [InlineData("notifications-tips-suggestions", "SubscribedContent-338389Enabled", false)]
    [InlineData("notifications-system-pane-suggestions", "SystemPaneSuggestionsEnabled", false)]
    [InlineData("start-show-suggestions", "SubscribedContent-338388Enabled", false)]
    public void IndividualControl_TargetsWindowsSettingWithRequiredDependencies(
        string settingId, string valueName, bool requiresRecommendedSection)
    {
        var settings = PrivacyAndSecurityOptimizations.GetPrivacyAndSecurityOptimizations().Settings
            .Concat(NotificationOptimizations.GetNotificationOptimizations().Settings)
            .Concat(StartMenuCustomizations.GetStartMenuCustomizations().Settings);
        var setting = settings.Single(item => item.Id == settingId);

        setting.InputType.Should().Be(InputType.Toggle);
        var registrySetting = setting.RegistrySettings.Single(item => item.ValueName == valueName);
        registrySetting.KeyPath.Should().Be(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager");

        if (requiresRecommendedSection)
        {
            var dependency = setting.Dependencies.Should().ContainSingle().Subject;
            dependency.DependencyType.Should().Be(SettingDependencyType.RequiresSpecificValue);
            dependency.RequiredSettingId.Should().Be("start-recommended-section");
            dependency.RequiredValue.Should().Be("Show");
        }
        else
        {
            setting.Dependencies.Should().BeEmpty();
        }
    }
}
