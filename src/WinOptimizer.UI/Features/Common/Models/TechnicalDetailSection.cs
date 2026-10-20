using System.Collections.Generic;

namespace WinOptimizer.UI.Features.Common.Models;

public sealed record TechnicalDetailSection(
    DetailRowType Type,
    string Label,
    bool StartsExpanded,
    IReadOnlyList<TechnicalDetailRow> Rows)
{
    public int Count => Rows.Count;
}
