using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Core.Features.Common.Interfaces;

public interface IApplicationCloseService
{
    Func<Task>? BeforeShutdown { get; set; }
    Task<OperationResult> CheckOperationsAndCloseAsync();
    /// <summary>Confirms and drains current work, launches the prepared installer, then exits the application.</summary>
    Task<OperationResult> CheckOperationsAndRestartAsync(Action launchInstaller);
}
