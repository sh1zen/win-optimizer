using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.UI.Features.Cleaner;
using WinOptimizer.UI.Features.Common.Services;
using WinOptimizer.UI.Features.Management;
using WinOptimizer.UI.Features.SoftwareApps;

namespace WinOptimizer.UI.Helpers;

/// <summary>Shows transient notices and returns an active cancelled review to its owning screen.</summary>
internal sealed class ScanCancellationPresentation : IDisposable
{
    private readonly ScanCancellationNotifications _notifications;
    private readonly Frame _frame;
    private readonly InfoBar _toast;
    private readonly Action _navigationChanged;
    private readonly DispatcherQueueTimer _dismiss;
    private bool _disposed;

    internal ScanCancellationPresentation(ScanCancellationNotifications notifications, Frame frame, InfoBar toast, Action navigationChanged)
    {
        _notifications = notifications;
        _frame = frame;
        _toast = toast;
        _navigationChanged = navigationChanged;
        _dismiss = toast.DispatcherQueue.CreateTimer();
        _dismiss.Interval = TimeSpan.FromSeconds(8);
        _dismiss.IsRepeating = false;
        _dismiss.Tick += (_, _) => _toast.IsOpen = false;
        _toast.Closed += OnToastClosed;
        _notifications.Cancelled += OnCancelled;
    }

    private void OnCancelled(ScanCancellationNotice notice) => _toast.DispatcherQueue.TryEnqueue(() =>
    {
        if (_disposed) return;
        switch (_frame.Content)
        {
            case WindowsStatusResultsPage page when ReferenceEquals(page.ViewModel, notice.Owner):
                ReturnFromResults(typeof(WindowsStatusPage));
                break;
            case CleanerResultsPage page when ReferenceEquals(page.ViewModel, notice.Owner):
                ReturnFromResults(typeof(CleanerPage));
                break;
            case UninstallResiduePage page when page.OwnsReview(notice.Owner):
                ReturnFromResults(typeof(SoftwareAppsPage), usePreviousPage: true);
                break;
        }

        _dismiss.Stop();
        _toast.Title = notice.Title;
        _toast.Message = notice.Message;
        _toast.IsOpen = true;
        _dismiss.Start();
    });

    /// <summary>Leaves the cancelled review out of the journal while preserving the route to the overview.</summary>
    private void ReturnFromResults(Type overview, bool usePreviousPage = false)
    {
        var review = _frame.Content;
        var reviewType = _frame.CurrentSourcePageType;
        if (_frame.CanGoBack && (usePreviousPage || _frame.BackStack[^1].SourcePageType == overview))
        {
            _frame.GoBack();
            if (!ReferenceEquals(_frame.Content, review) && _frame.ForwardStack.Count > 0 &&
                _frame.ForwardStack[^1].SourcePageType == reviewType) _frame.ForwardStack.RemoveAt(_frame.ForwardStack.Count - 1);
        }
        else if (_frame.Navigate(overview) && _frame.BackStack.Count > 0 && _frame.BackStack[^1].SourcePageType == reviewType)
            _frame.BackStack.RemoveAt(_frame.BackStack.Count - 1);
        _navigationChanged();
    }

    private void OnToastClosed(InfoBar sender, InfoBarClosedEventArgs args) => _dismiss.Stop();

    public void Dispose()
    {
        _disposed = true;
        _notifications.Cancelled -= OnCancelled;
        _toast.Closed -= OnToastClosed;
        _dismiss.Stop();
    }
}
