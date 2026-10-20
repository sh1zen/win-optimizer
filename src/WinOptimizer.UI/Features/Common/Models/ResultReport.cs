namespace WinOptimizer.UI.Features.Common.Models;

/// <summary>A scan report displayed inline with ordered sections and measured values.</summary>
public sealed record ResultReport(string Title, IReadOnlyList<ResultReportSection> Sections);

/// <summary>Preserves a report's native section heading and ordered content.</summary>
public sealed record ResultReportSection(string Heading, IReadOnlyList<ResultReportRow> Rows)
{
    public bool HasHeading => Heading.Length != 0;
}

/// <summary>Displays a label/value pair or a paragraph spanning both columns.</summary>
public sealed record ResultReportRow(string Label, string? Value = null)
{
    public bool HasValue => Value is not null;
    public int LabelColumnSpan => HasValue ? 1 : 2;
}
