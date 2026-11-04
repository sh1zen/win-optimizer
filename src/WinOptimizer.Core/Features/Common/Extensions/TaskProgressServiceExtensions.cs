using System.Threading;
using WinOptimizer.Core.Features.Common.Interfaces;

namespace WinOptimizer.Core.Features.Common.Extensions;

/// <summary>
/// Convenience extensions over <see cref="ITaskProgressService"/>.
/// </summary>
public static class TaskProgressServiceExtensions
{
    /// <summary>Releases a caller's task without completing an operation that replaced it.</summary>
    public static void CompleteOwnedTask(this ITaskProgressService service, CancellationTokenSource? owner)
    {
        if (owner is null) return;
        if (ReferenceEquals(owner, service.CurrentTaskCancellationSource)) service.CompleteTask();
        else owner.Dispose();
    }

    /// <summary>
    /// The cancellation token of the in-progress task, or <see cref="CancellationToken.None"/>
    /// when no task is running.
    /// </summary>
    public static CancellationToken GetCurrentCancellationToken(this ITaskProgressService? taskProgressService)
        => taskProgressService?.CurrentTaskCancellationSource?.Token ?? CancellationToken.None;
}
