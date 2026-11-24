using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using WinOptimizer.Infrastructure.Features.Extensions;

namespace WinOptimizer.UI.Features.Extensions;

/// <summary>Hosts a single desktop companion and marshals reactivation to its UI thread.</summary>
internal abstract class CompanionApplication
{
    protected abstract void OnLaunched(App app, string? language);
    protected abstract void Activate(AppActivationArguments arguments);
    protected virtual void ValidateProcess() => DesktopExtensionProcess.RequireNonElevated();

    public void Run(DesktopExtensionProcess process, string[] args, string? language = null)
    {
        try
        {
            ValidateProcess();
            bool startup = args is ["--startup"];
            if (startup && !process.StartsWithWindows()) return;
            var instance = AppInstance.FindOrRegisterForKey(process.Executable);
            if (!instance.IsCurrent)
            {
                if (!startup) RedirectTo(instance);
                return;
            }

            var ready = new TaskCompletionSource<DispatcherQueue?>(TaskCreationOptions.RunContinuationsAsynchronously);
            async void Reactivate(object? sender, AppActivationArguments arguments)
            {
                var dispatcher = await ready.Task.ConfigureAwait(false);
                dispatcher?.TryEnqueue(() => Activate(arguments));
            }
            instance.Activated += Reactivate;
            try
            {
                WinRT.ComWrappersSupport.InitializeComWrappers();
                Microsoft.UI.Xaml.Application.Start(_ =>
                {
                    var queue = DispatcherQueue.GetForCurrentThread();
                    SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(queue));
                    var app = new App(app =>
                    {
                        try
                        {
                            OnLaunched(app, language);
                            ready.TrySetResult(queue);
                        }
                        catch (Exception ex) { ReportStartupError(process.Name, ex); app.Exit(); }
                    });
                });
            }
            finally { instance.Activated -= Reactivate; ready.TrySetResult(null); }
        }
        catch (Exception ex) { ReportStartupError(process.Name, ex); }
    }

    /// <summary>Starts the resident host when enabling its shortcut, or redirects an existing host's request.</summary>
    protected bool TryRouteSettings(DesktopExtensionProcess process, string[] args, string? startLanguage)
    {
        DesktopExtensionProcess.RequireNonElevated();
        if (startLanguage is not null)
        {
            Run(process, args, startLanguage);
            return true;
        }
        if (AppInstance.GetInstances().FirstOrDefault(instance => instance.Key == process.Executable) is not { } running)
            return false;
        RedirectTo(running);
        return true;
    }

    private static void RedirectTo(AppInstance instance)
    {
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        Task.Run(async () => await instance.RedirectActivationToAsync(activation)).GetAwaiter().GetResult();
    }

    protected static void ReportStartupError(string name, Exception error)
    {
        ShowError(0, error.Message, name, 0x10);
        Environment.ExitCode = 1;
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int ShowError(nint owner, string text, string title, uint flags);
}
