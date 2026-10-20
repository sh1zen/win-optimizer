using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.Common.Interfaces;

namespace WinOptimizer.UI.Features.Common.Models;

/// <summary>
/// Captures all initialization data for a SettingItemViewModel, replacing
/// the object initializer pattern in SettingViewModelFactory.CreateAsync().
/// </summary>
public record SettingItemViewModelConfig
{
    public required SettingDefinition SettingDefinition { get; init; }
    public ISettingsFeatureViewModel? ParentFeatureViewModel { get; init; }
    public required string SettingId { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public string GroupName { get; init; } = string.Empty;
    public string Icon { get; init; } = string.Empty;
    public string IconPack { get; init; } = BaseDefinition.DefaultIconPack;
    public required InputType InputType { get; init; }
    public bool IsSelected { get; init; }
    public string OnText { get; init; } = "On";
    public string OffText { get; init; } = "Off";
    public string ActionButtonText { get; init; } = "Apply";
}
