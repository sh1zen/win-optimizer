using System.Text;
using WinOptimizer.UI.Features.Common.Models;

namespace WinOptimizer.UI.Features.Cleaner.ViewModels;

/// <summary>Structures localized Windows disk output without interpreting or changing its measurements.</summary>
internal static class CleanerDiskReport
{
    public static ResultReport Parse(string drive, string output, string? unavailableReason)
    {
        var sections = new List<ResultReportSection>();
        var rows = new List<ResultReportRow>();
        var paragraph = new StringBuilder();
        var heading = "";

        void FlushParagraph()
        {
            if (paragraph.Length == 0) return;
            rows.Add(new(paragraph.ToString()));
            paragraph.Clear();
        }

        void FlushSection()
        {
            FlushParagraph();
            if (heading.Length != 0 || rows.Count != 0) sections.Add(new(heading, rows.ToArray()));
            rows.Clear();
        }

        using var reader = new StringReader(output);
        while (reader.ReadLine() is { } line)
        {
            var text = line.Trim();
            if (text.Length == 0) { FlushParagraph(); continue; }
            var separator = text.IndexOf('=');
            if (separator > 0)
            {
                FlushParagraph();
                rows.Add(new(text[..separator].Trim(), text[(separator + 1)..].Trim()));
            }
            else if (text.EndsWith(':'))
            {
                FlushSection();
                heading = text[..^1];
            }
            else
            {
                if (paragraph.Length != 0) paragraph.Append(' ');
                paragraph.Append(text);
            }
        }
        FlushParagraph();
        if (unavailableReason is not null) rows.Add(new(unavailableReason));
        FlushSection();
        return new(drive, sections);
    }
}
