using System.Collections.Generic;

namespace WinOptimizer.Core.Features.Common.Models;

public sealed record SettingGroup
{
    public required string Name { get; init; }
    public required string FeatureId { get; init; }
    public required IReadOnlyList<SettingDefinition> Settings { get; init; }
}
