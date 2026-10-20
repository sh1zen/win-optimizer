using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.SoftwareApps.ViewModels;

namespace WinOptimizer.UI.Features.SoftwareApps.Services;

/// <summary>Projects Windows app selections into the shared configuration format.</summary>
internal static class WindowsAppConfigurationMapper
{
    /// <summary>Preserves the displayed name and prefers AppX, capability, then feature identity.</summary>
    public static ConfigurationItem CreateItem(AppItemViewModel item)
    {
        var configItem = new ConfigurationItem
        {
            Id = item.Id,
            Name = item.Name,
            IsSelected = true,
            InputType = InputType.Toggle
        };

        if (item.Definition.AppxPackageName?.Length > 0)
            configItem.AppxPackageName = item.Definition.AppxPackageName;
        else if (!string.IsNullOrEmpty(item.Definition.CapabilityName))
            configItem.CapabilityName = item.Definition.CapabilityName;
        else if (!string.IsNullOrEmpty(item.Definition.OptionalFeatureName))
            configItem.OptionalFeatureName = item.Definition.OptionalFeatureName;

        return configItem;
    }
}
