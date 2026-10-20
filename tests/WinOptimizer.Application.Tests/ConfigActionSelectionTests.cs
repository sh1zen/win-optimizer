using WinOptimizer.Application.Configuration;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Models;
using Xunit;

namespace WinOptimizer.Application.Tests;

/// <summary>Protects action-only scope and selection order shared by direct and reviewed imports.</summary>
public sealed class ConfigActionSelectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IncludeSections_PreservesSelectedSettingsAndAddsOnlyMissingActionSections(bool parentSelected)
    {
        var taskbar = $"Customize_{FeatureIds.Taskbar}";
        var start = $"Customize_{FeatureIds.StartMenu}";
        var theme = $"Customize_{FeatureIds.WindowsTheme}";
        var selections = new List<string> { "Optimize", taskbar };
        if (parentSelected) selections.Add("Customize");
        var options = new ImportOptions
        {
            ApplyCleanTaskbar = true, ApplyCleanStartMenu = true, ApplyThemeWallpaper = true,
            ProcessWindowsAppsRemoval = true, IsWindowsDefaults = true
        };

        var result = ConfigActionSelection.IncludeSections(selections, options);

        Assert.Equal(new[] { "Optimize", taskbar, "Customize", start, theme }, selections);
        Assert.Equal(new[] { start, theme }, result.ActionOnlySubsections.Order().ToArray());
        Assert.True(result.ProcessWindowsAppsRemoval);
        Assert.True(result.IsWindowsDefaults);
        Assert.Empty(options.ActionOnlySubsections);
    }

    [Fact]
    public void IncludeSections_NoActions_PreservesSelectionAndClearsPriorActionScope()
    {
        var selections = new List<string> { "Optimize" };
        var options = new ImportOptions { ActionOnlySubsections = ["Customize_Taskbar"] };
        var result = ConfigActionSelection.IncludeSections(selections, options);
        Assert.Equal(new[] { "Optimize" }, selections);
        Assert.Empty(result.ActionOnlySubsections);
    }
}
