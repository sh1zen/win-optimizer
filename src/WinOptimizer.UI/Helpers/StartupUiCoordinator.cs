using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinOptimizer.Core.Features.Common.Extensions;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Common.Services;
using WinOptimizer.UI.Features.Common.Controls;
using WinOptimizer.UI.Features.SoftwareApps;
using WinOptimizer.UI.ViewModels;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace WinOptimizer.UI.Helpers;

/// <summary>
/// Manages the startup loading overlay and initial navigation sequence.
/// Extracted from MainWindow to reduce code-behind complexity.
/// </summary>
internal sealed class StartupUiCoordinator
{
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly ILogService? _logService;

    /// <summary>
    /// Whether this is the first launch of the application.
    /// </summary>
    public bool IsFirstLaunch { get; private set; }

    public StartupUiCoordinator(DispatcherQueue dispatcherQueue, ILogService? logService)
    {
        _dispatcherQueue = dispatcherQueue;
        _logService = logService;
    }

    /// <summary>
    /// Presents the loading shell before resolving startup services and initializing settings.
    /// </summary>
    public async Task RunStartupAndCompleteAsync(
        TextBlock loadingStatusText,
        Frame contentFrame,
        NavSidebar navSidebar,
        Grid loadingOverlay,
        FrameworkElement shell,
        Func<MainWindowViewModel?> getViewModel,
        Action markStartupComplete)
    {
        try
        {
            // Activate schedules presentation; synchronous service resolution must wait for its first frame.
            try { await RunAfterFirstRenderAsync(shell, () => Task.CompletedTask); }
            catch (OperationCanceledException) { return; }
            var orchestrator = App.Services.GetRequiredService<IStartupOrchestrator>();

            var statusProgress = new Progress<string>(localizationKey =>
            {
                _dispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        var localizationService = App.Services.GetService<ILocalizationService>();
                        loadingStatusText.Text = localizationService?.GetString(localizationKey) ?? localizationKey;
                    }
                    catch
                    {
                        loadingStatusText.Text = localizationKey;
                    }
                });
            });

            var detailedProgress = new Progress<TaskProgressDetail>(detail =>
            {
                if (!string.IsNullOrEmpty(detail.StatusText))
                {
                    _dispatcherQueue.TryEnqueue(() =>
                    {
                        try { loadingStatusText.Text = detail.StatusText; }
                        catch (Exception ex)
                        {
                            App.Services.GetService<ILogService>()?.LogDebug(
                                $"Failed to update loading status text: {ex.Message}");
                        }
                    });
                }
            });

            var result = await orchestrator.RunStartupSequenceAsync(statusProgress, detailedProgress)
                .ConfigureAwait(false);
            IsFirstLaunch = result.IsFirstLaunch;
        }
        catch (Exception ex)
        {
            StartupLogger.Log("StartupUiCoordinator", $"RunStartupAndCompleteAsync EXCEPTION: {ex}");
        }

        // Always complete startup on the UI thread so the app is usable.
        // A bare DispatcherQueue.TryEnqueue callback runs on the UI thread but
        // installs no SynchronizationContext — so every `await` of background
        // work inside CompleteStartupAsync (SoftwareApps load, icon resolution)
        // would resume on a thread-pool thread and mutate bound UI off-thread,
        // leaving the Windows/External Apps lists stuck on "Loaded N items"
        // with stale install status. Install the dispatcher SynchronizationContext
        // so continuations marshal back to the UI thread for the whole chain.
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (SynchronizationContext.Current is null)
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(_dispatcherQueue));
            }

            _ = CompleteStartupAsync(contentFrame, navSidebar, loadingOverlay, getViewModel, markStartupComplete);
        });
    }

    /// <summary>
    /// Presents SoftwareApps before starting its inventory and the initial full backup.
    /// </summary>
    private async Task CompleteStartupAsync(
        Frame contentFrame,
        NavSidebar navSidebar,
        Grid loadingOverlay,
        Func<MainWindowViewModel?> getViewModel,
        Action markStartupComplete)
    {
        StartupLogger.Log("StartupUiCoordinator", "CompleteStartupAsync starting");

        try
        {
            // Navigation creates the page without starting discovery before its first frame.
            navSidebar.SelectedTag = "SoftwareApps";
            contentFrame.Navigate(typeof(SoftwareAppsPage), "startup");
        }
        catch (Exception ex)
        {
            StartupLogger.Log("StartupUiCoordinator", $"SoftwareApps initialization failed: {ex.Message}");
            _logService?.LogWarning($"SoftwareApps init failed: {ex.Message}");
        }

        // Hide overlay and mark startup complete
        markStartupComplete();
        loadingOverlay.Visibility = Visibility.Collapsed;
        StartupLogger.Log("StartupUiCoordinator", "Startup complete, overlay hidden");

        // Scheduling starts only after the shell is usable; the service defers the first check.
        getViewModel()?.UpdateCheck.StartBackgroundUpdateChecksAsync().FireAndForget(_logService!);

        try
        {
            await RunAfterFirstRenderAsync(contentFrame, async () =>
            {
                var page = contentFrame.Content as SoftwareAppsPage;
                if (page is not null)
                {
                    await page.ViewModel.InitializeWindowsAppsAsync();
                    page.ViewModel.InitializeExternalAppsAsync().FireAndForget(_logService!);
                }
                IsFirstLaunch = (await App.Services.GetRequiredService<IStartupOrchestrator>()
                    .RunDeferredInitializationAsync()).IsFirstLaunch;
            });
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _logService?.LogWarning($"Deferred startup initialization failed: {ex.Message}");
        }

        // Show first-launch restore point offer if this is the first launch
        try
        {
            if (IsFirstLaunch)
            {
                var startupNotifications = App.Services.GetRequiredService<IStartupNotificationService>();
                await startupNotifications.ShowFirstLaunchRestoreOfferAsync();
            }
        }
        catch (Exception ex)
        {
            StartupLogger.Log("StartupUiCoordinator", $"Startup notification failed: {ex.Message}");
        }

        // Pre-warm the sponsors cache for the manually opened support dialog.
        App.Services.GetService<ISponsorsService>()?.GetSponsorsAsync().FireAndForget(_logService!);

    }

    /// <summary>Starts deferred work after a rendered frame; unloading cancels and releases both event subscriptions.</summary>
    internal static async Task RunAfterFirstRenderAsync(FrameworkElement content, Func<Task> initialize)
    {
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<object>? rendering = null;
        RoutedEventHandler? unloaded = null;
        void Unsubscribe()
        {
            CompositionTarget.Rendering -= rendering;
            content.Unloaded -= unloaded;
        }
        rendering = (_, _) =>
        {
            CompositionTarget.Rendering -= rendering;
            if (!content.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                { Unsubscribe(); rendered.TrySetResult(); }))
            {
                Unsubscribe();
                rendered.TrySetException(new InvalidOperationException("The startup dispatcher is shutting down."));
            }
        };
        unloaded = (_, _) => { Unsubscribe(); rendered.TrySetCanceled(); };
        CompositionTarget.Rendering += rendering;
        content.Unloaded += unloaded;
        await rendered.Task;
        if (!content.IsLoaded) throw new OperationCanceledException("The startup content was unloaded.");
        await initialize();
    }
}
