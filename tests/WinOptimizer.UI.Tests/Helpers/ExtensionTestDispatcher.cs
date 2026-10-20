using Microsoft.UI.Dispatching;
using WinOptimizer.UI.Features.Common.Interfaces;

namespace WinOptimizer.UI.Tests.Helpers;

/// <summary>Runs the desktop dispatch boundary synchronously for isolated extension lifetime tests.</summary>
internal sealed class ExtensionTestDispatcher : IDispatcherService
{
    public bool HasThreadAccess => true;
    public void Initialize(DispatcherQueue dispatcherQueue) { }
    public void RunOnUIThread(Action action) => action();
    public void RunOnUIThread(DispatcherQueuePriority priority, Action action) => action();
    public Task RunOnUIThreadAsync(Func<Task> asyncAction) => asyncAction();
    public Task RunOnUIThreadAsync(DispatcherQueuePriority priority, Func<Task> asyncAction) => asyncAction();
    public Task RunOnUIThreadWithContextAsync(Func<Task> asyncAction) => asyncAction();
}
