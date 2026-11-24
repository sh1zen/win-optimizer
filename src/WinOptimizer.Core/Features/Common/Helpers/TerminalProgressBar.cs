namespace WinOptimizer.Core.Features.Common.Helpers;

/// <summary>Recognizes the block characters used by terminal progress bars.</summary>
public static class TerminalProgressBar
{
    public const char FullBlock = '\u2588';
    public const char Track = '\u2591';

    /// <summary>Matches full blocks and the partial blocks used at low progress percentages.</summary>
    public static bool IsFilledBlock(char character) => character is >= FullBlock and <= '\u258F';

    /// <summary>Detects a filled block or unfilled track in a terminal line.</summary>
    public static bool IsProgressLine(string line)
    {
        foreach (char character in line)
        {
            if (IsFilledBlock(character) || character == Track)
                return true;
        }

        return false;
    }
}
