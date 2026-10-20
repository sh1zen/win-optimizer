using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Application.Configuration;

/// <summary>Includes approved Customize actions without importing unselected settings from their sections.</summary>
internal static class ConfigActionSelection
{
    public static ImportOptions IncludeSections(List<string> selectedSections, ImportOptions options)
    {
        var actionOnly = new HashSet<string>();
        Include(options.ApplyCleanTaskbar, FeatureIds.Taskbar);
        Include(options.ApplyCleanStartMenu, FeatureIds.StartMenu);
        Include(options.ApplyThemeWallpaper, FeatureIds.WindowsTheme);
        return options with { ActionOnlySubsections = actionOnly };

        void Include(bool enabled, string featureId)
        {
            var section = $"Customize_{featureId}";
            if (!enabled || selectedSections.Contains(section)) return;
            if (!selectedSections.Contains("Customize")) selectedSections.Add("Customize");
            selectedSections.Add(section);
            actionOnly.Add(section);
        }
    }
}
