using System.Collections.Generic;
using WinOptimizer.Core.Features.Common.Enums;

namespace WinOptimizer.Core.Features.Common.Models;

/// <summary>
/// One typed ComboBox option with value mappings, script data and presentation metadata.
/// </summary>
public sealed record ComboBoxOption
{
    public required string DisplayName { get; init; }
    public Dictionary<string, object?>? ValueMappings { get; init; }
    public int? SimpleValue { get; init; }
    public bool? CommandValue { get; init; }
    public ScriptOption? Script { get; init; }
    public string? Tooltip { get; init; }
    public string? Warning { get; init; }
    public (string Title, string Message)? Confirmation { get; init; }
    public Dictionary<string, string>? ScriptVariables { get; init; }
    public bool IsDefault { get; init; }
    public bool IsRecommended { get; init; }
}
