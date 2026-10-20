using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Core.Features.Common.Interfaces;

public interface IApplicationCloseService
{
    Func<Task>? BeforeShutdown { get; set; }
    Task<OperationResult> CheckOperationsAndCloseAsync();
}
