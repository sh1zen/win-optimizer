using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using WinOptimizer.Core.Features.Common.Helpers;

namespace WinOptimizer.UI.Features.Common.Utilities;

/// <summary>
/// Renders colored terminal output in the Terminal Output dialog.
/// </summary>
internal static class TerminalLineRenderer
{
    // Terminal-style color brushes
    public static readonly SolidColorBrush DefaultBrush =
        new(Windows.UI.Color.FromArgb(0xFF, 0xCC, 0xCC, 0xCC));

    public static readonly SolidColorBrush ErrorBrush =
        new(Windows.UI.Color.FromArgb(0xFF, 0xF4, 0x4C, 0x4C));

    public static readonly SolidColorBrush WarningBrush =
        new(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xCC, 0x00));

    public static readonly SolidColorBrush SuccessBrush =
        new(Windows.UI.Color.FromArgb(0xFF, 0x6A, 0xBF, 0x69));

    public static readonly SolidColorBrush MetadataBrush =
        new(Windows.UI.Color.FromArgb(0xFF, 0x56, 0x9C, 0xD6));

    public static readonly SolidColorBrush SeparatorBrush =
        new(Windows.UI.Color.FromArgb(0xFF, 0x60, 0x60, 0x60));

    public static readonly SolidColorBrush BarTrackBrush =
        new(Windows.UI.Color.FromArgb(0xFF, 0x40, 0x40, 0x40));

    public static readonly Windows.UI.Color TerminalBackground =
        Windows.UI.Color.FromArgb(0xFF, 0x1E, 0x1E, 0x1E);

    public static readonly Microsoft.UI.Xaml.Media.FontFamily MonoFont = new("Consolas");

    /// <summary>
    /// Returns the appropriate foreground brush based on line content.
    /// </summary>
    public static SolidColorBrush GetLineBrush(string line)
    {
        var trimmed = line.TrimStart();

        // Strip the script slot prefix before classifying terminal output.
        if (trimmed.Length > 2 && trimmed[0] == '[')
        {
            var closeBracket = trimmed.IndexOf("] ", StringComparison.Ordinal);
            if (closeBracket > 0 && closeBracket < 30)
                trimmed = trimmed.Substring(closeBracket + 2).TrimStart();
        }

        if (trimmed.StartsWith("Command:", StringComparison.Ordinal)
            || trimmed.StartsWith("Start Time:", StringComparison.Ordinal)
            || trimmed.StartsWith("End Time:", StringComparison.Ordinal)
            || trimmed.StartsWith("Process return value:", StringComparison.Ordinal))
            return MetadataBrush;
        if (trimmed == "---")
            return SeparatorBrush;
        if (line.Contains("error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("failed", StringComparison.OrdinalIgnoreCase))
            return ErrorBrush;
        if (line.Contains("warning", StringComparison.OrdinalIgnoreCase))
            return WarningBrush;
        if (line.Contains("successfully", StringComparison.OrdinalIgnoreCase)
            || line.Contains("complete", StringComparison.OrdinalIgnoreCase))
            return SuccessBrush;
        return DefaultBrush;
    }

    /// <summary>
    /// Creates color-coded Runs for a terminal line.
    /// Progress bar lines containing both filled (█) and unfilled (░) characters are split
    /// into multiple Runs so that filled and unfilled portions use the same character
    /// (consistent height) but different colors.
    /// </summary>
    /// <param name="line">The terminal line text.</param>
    public static Run[] CreateLineRuns(string line)
    {
        // Check if this is a progress bar line (contains filled block chars and ░ unfilled track).
        // At low percentages the filled portion may be a partial block (U+2589–U+258F)
        // rather than a full block (U+2588), so check the whole range.
        bool hasFilledBlocks = false;
        foreach (char c in line)
        {
            if (TerminalProgressBar.IsFilledBlock(c)) { hasFilledBlocks = true; break; }
        }
        if (hasFilledBlocks && line.Contains(TerminalProgressBar.Track))
        {
            var runs = new List<Run>();
            int i = 0;
            while (i < line.Length)
            {
                bool isTrack = line[i] == TerminalProgressBar.Track;
                int start = i;
                while (i < line.Length && (line[i] == TerminalProgressBar.Track) == isTrack)
                    i++;

                var segment = line.Substring(start, i - start);
                if (isTrack)
                {
                    // Unfilled track: render as █ in dim color (same char = same height)
                    runs.Add(new Run
                    {
                        Text = segment.Replace(TerminalProgressBar.Track, TerminalProgressBar.FullBlock),
                        Foreground = BarTrackBrush
                    });
                }
                else
                {
                    runs.Add(new Run
                    {
                        Text = segment,
                        Foreground = GetLineBrush(line)
                    });
                }
            }
            runs[^1].Text += "\n";
            return runs.ToArray();
        }

        return [new Run
        {
            Text = line + "\n",
            Foreground = GetLineBrush(line)
        }];
    }
}
