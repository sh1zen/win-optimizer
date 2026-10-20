using System.Text;

namespace WinOptimizer.Infrastructure.Features.SoftwareApps.Services.WinGet.Utilities;

/// <summary>Retains WinGet's transcript and distinguishes transient progress from permanent lines.</summary>
internal sealed class WinGetOutputLines(
    StringBuilder output,
    Action<string>? onOutputLine,
    Action<string>? onProgressLine,
    Func<string, string>? formatProgress = null)
{
    private readonly StringBuilder _currentLine = new();
    private string? _lastProgressLine;

    /// <summary>Publishes CR progress immediately and promotes CRLF progress without duplicating the transcript.</summary>
    public void Append(char value)
    {
        if (value == '\r')
        {
            EmitProgress();
        }
        else if (value != '\n')
        {
            _currentLine.Append(value);
        }
        else if (_currentLine.Length > 0)
        {
            var line = _currentLine.ToString();
            output.AppendLine(line);
            onOutputLine?.Invoke(line);
            _currentLine.Clear();
            _lastProgressLine = null;
        }
        else if (_lastProgressLine is not null)
        {
            onOutputLine?.Invoke(_lastProgressLine);
            _lastProgressLine = null;
        }
    }

    /// <summary>Also handles ConPTY cursor resets after terminal escape processing.</summary>
    public void EmitProgress()
    {
        if (_currentLine.Length == 0) return;
        var line = _currentLine.ToString();
        if (formatProgress is not null) line = formatProgress(line);
        _lastProgressLine = line;
        output.AppendLine(line);
        onProgressLine?.Invoke(line);
        _currentLine.Clear();
    }

    /// <summary>Publishes any unterminated text when the input reaches EOF.</summary>
    public void Complete()
    {
        if (_currentLine.Length == 0) return;
        var line = _currentLine.ToString();
        output.AppendLine(line);
        onOutputLine?.Invoke(line);
    }
}
