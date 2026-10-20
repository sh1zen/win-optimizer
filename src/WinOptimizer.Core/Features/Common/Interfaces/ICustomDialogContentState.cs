namespace WinOptimizer.Core.Features.Common.Interfaces;

/// <summary>Allows custom dialog content to retain its lifetime while an awaited mutation is running.</summary>
public interface ICustomDialogContentState
{
    bool CanClose { get; }
}
