using WinOptimizer.Core.Features.Common.Enums;

namespace WinOptimizer.Core.Features.Common.Models;

public class TaskProgressDetail
{
    /// <summary>False when the owning page presents the task's progress and cancellation controls.</summary>
    public bool ShowInTaskBar { get; set; } = true;
    /// <summary>Whether the operation supports cancellation from the shared task controls.</summary>
    public bool CanCancel { get; set; } = true;
    public double? Progress { get; set; }
    public string? StatusText { get; set; }
    public string? DetailedMessage { get; set; }
    public LogLevel LogLevel { get; set; } = LogLevel.Info;
    public bool IsIndeterminate { get; set; }
    public string? TerminalOutput { get; set; }
    public bool IsActive { get; set; }
    public int QueueTotal { get; set; }
    public int QueueCurrent { get; set; }
    public string? QueueNextItemName { get; set; }
    public bool IsCompletion { get; set; }           // true = intentional task completion signal
    public bool IsProgressIndicator { get; set; }   // true = replaces previous progress line (like \r in a terminal)
}
