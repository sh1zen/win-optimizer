using System.Collections.Generic;
using WinOptimizer.Core.Features.Common.Enums;

namespace WinOptimizer.Core.Features.Common.Models;

public class TaskProgressDetail
{
    /// <summary>False when the owning page presents the task's progress and cancellation controls.</summary>
    public bool ShowInTaskBar { get; set; } = true;
    /// <summary>Whether the operation supports cancellation from the footer.</summary>
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
    public int ScriptSlotIndex { get; set; } = -1;  // -1 = single-mode (existing behavior)
    public int ScriptSlotCount { get; set; }         // 0 = single-mode
    public bool IsCompletion { get; set; }           // true = intentional task completion signal
    public bool IsProgressIndicator { get; set; }   // true = replaces previous progress line (like \r in a terminal)
}
