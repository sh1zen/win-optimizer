namespace WinOptimizer.UI.Features.Common.Models;

/// <summary>Retains a section destination and its search suggestion in the shell's native journal.</summary>
public sealed record SectionPageNavigation(string SectionKey, string? SearchText = null);
