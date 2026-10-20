using WinOptimizer.UI.Features.Common.Interfaces;

namespace WinOptimizer.UI.Features.Common.Models;

/// <summary>Connects existing section metadata to its detail page without copying catalogue data.</summary>
public sealed record SectionPageRoute(ISectionInfo Section, Type PageType);
