using FluentAssertions;
using WinOptimizer.Core.Features.Optimize.Models;
using Xunit;

namespace WinOptimizer.Core.Tests.Validation;

/// <summary>Checks the Windows Update policy contract behind the notifications toggle.</summary>
public class UpdateNotificationsTests
{
    [Fact]
    public void NotificationsToggle_UsesMachinePolicyFlagAndNotificationLevel()
    {
        var setting = UpdateOptimizations.GetUpdateOptimizations().Settings
            .Single(setting => setting.Id == "updates-notification-level");

        setting.RegistrySettings.Should().HaveCount(2);
        setting.RegistrySettings.Should().OnlyContain(registry =>
            registry.KeyPath == @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate");
        var flag = setting.RegistrySettings.Single(registry => registry.ValueName == "SetUpdateNotificationLevel");
        var level = setting.RegistrySettings.Single(registry => registry.ValueName == "UpdateNotificationLevel");

        // On restores Windows notifications; Off enables suppression, including restart warnings.
        flag.EnabledValue.Should().Equal(new object?[] { null });
        level.EnabledValue.Should().Equal(new object?[] { null });
        flag.DisabledValue.Should().Equal(new object?[] { 1 });
        level.DisabledValue.Should().Equal(new object?[] { 2 });
        flag.DefaultValue.Should().BeNull();
        level.DefaultValue.Should().BeNull();
        flag.RecommendedValue.Should().BeNull();
        level.RecommendedValue.Should().BeNull();
        setting.RecommendedToggleState.Should().BeTrue();
        setting.DefaultToggleState.Should().BeTrue();
    }
}
