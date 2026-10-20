using FluentAssertions;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Customize.Models;
using WinOptimizer.Core.Features.Optimize.Models;
using Xunit;

namespace WinOptimizer.Core.Tests.Constants;

public class SettingIdsTests
{
    [Fact]
    public void PowerPlanSelection_MatchesDefinitionInPowerOptimizations()
    {
        var settings = PowerOptimizations.GetPowerOptimizations().Settings;
        settings.Should().Contain(s => s.Id == SettingIds.PowerPlanSelection);
    }

    [Fact]
    public void ThemeModeWindows_MatchesDefinitionInWindowsThemeCustomizations()
    {
        var settings = WindowsThemeCustomizations.GetWindowsThemeCustomizations().Settings;
        settings.Should().Contain(s => s.Id == SettingIds.ThemeModeWindows);
    }

    [Fact]
    public void UpdatesPolicyMode_MatchesDefinitionInUpdateOptimizations()
    {
        var settings = UpdateOptimizations.GetUpdateOptimizations().Settings;
        settings.Should().Contain(s => s.Id == SettingIds.UpdatesPolicyMode);
    }

    [Fact]
    public void Constants_AreDistinct()
    {
        var ids = new[] { SettingIds.PowerPlanSelection, SettingIds.ThemeModeWindows, SettingIds.UpdatesPolicyMode };
        ids.Should().OnlyHaveUniqueItems();
    }
}
