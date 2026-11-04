using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.SoftwareApps.ViewModels;

namespace WinOptimizer.UI.Features.SoftwareApps.Services;

/// <summary>Marshals residue pages and recovery dialogs while their owning workflow retains the session.</summary>
public sealed class UninstallResidueReview(IUninstallResidueService service, IDialogService dialogs,
    IDispatcherService dispatcher, ITaskProgressService tasks, ILocalizationService text,
    IApplicationModeService mode, Func<Frame> reviewFrame, Func<IDisposable> suspendWindow) : IUninstallResidueReview
{
    private readonly object _reviewGate = new();
    private readonly Queue<PendingReview> _pending = new();
    private int _deferrals;
    private sealed record PendingReview(IUninstallResidueSession Session, AppUninstallResult Verification, CancellationToken Cancellation, IDisposable Reservation);

    /// <summary>Acquires and releases native window state on the UI dispatcher, including failed launches.</summary>
    public async Task<IAsyncDisposable> SuspendForUninstallAsync(CancellationToken ct)
    {
        IDisposable? suspension = null;
        await dispatcher.RunOnUIThreadWithContextAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            suspension = suspendWindow();
            return Task.CompletedTask;
        });
        return new WindowSuspension(dispatcher, suspension
            ?? throw new InvalidOperationException("The application window could not be suspended."));
    }

    private sealed class WindowSuspension(IDispatcherService dispatcher, IDisposable suspension) : IAsyncDisposable
    {
        private IDisposable? _suspension = suspension;
        public ValueTask DisposeAsync() => new(dispatcher.RunOnUIThreadWithContextAsync(() =>
        {
            Interlocked.Exchange(ref _suspension, null)?.Dispose();
            return Task.CompletedTask;
        }));
    }

    /// <summary>Shows fallback consent on the UI dispatcher even when native discovery completes on a worker.</summary>
    public async Task<bool> ConfirmAutomaticUninstallAsync(ItemDefinition application, CancellationToken ct)
    {
        var confirmed = false;
        await dispatcher.RunOnUIThreadWithContextAsync(async () =>
        {
            ct.ThrowIfCancellationRequested();
            confirmed = (await dialogs.ShowConfirmationAsync(new ConfirmationRequest
            {
                Title = text.GetString("Uninstall_AutomaticTitle"),
                Message = text.GetString("Uninstall_AutomaticPrompt", application.Name),
                ConfirmButtonText = text.GetString("Uninstall_AutomaticProceed"),
                CancelButtonText = text.GetString("Button_Cancel")
            })).Confirmed;
        });
        ct.ThrowIfCancellationRequested();
        return confirmed;
    }

    public async Task<UninstallReviewDisposition> ShowAsync(IUninstallResidueSession session, AppUninstallResult verification, CancellationToken ct)
    {
        lock (_reviewGate)
        {
            if (_deferrals > 0)
            {
                _pending.Enqueue(new(session, verification, ct, mode.BeginSystemOperation()));
                return UninstallReviewDisposition.Deferred;
            }
        }
        await ShowCoreAsync(session, verification, ct);
        return UninstallReviewDisposition.Completed;
    }

    private Task ShowCoreAsync(IUninstallResidueSession session, AppUninstallResult verification, CancellationToken ct) =>
        dispatcher.RunOnUIThreadWithContextAsync(async () =>
        {
            ct.ThrowIfCancellationRequested();
            using var viewModel = new UninstallResidueViewModel(session, verification, service, tasks, text);
            var frame = reviewFrame();
            if (!frame.Navigate(typeof(UninstallResiduePage), viewModel))
                throw new InvalidOperationException("The uninstall results page could not be opened.");
            try { await ((UninstallResiduePage)frame.Content).Closed; }
            finally
            {
                // A completed review cannot reopen with an expired inspection session.
                RemoveReviewEntries(frame.BackStack);
                RemoveReviewEntries(frame.ForwardStack);
            }

            void RemoveReviewEntries(IList<Microsoft.UI.Xaml.Navigation.PageStackEntry> entries)
            {
                for (var index = entries.Count - 1; index >= 0; index--)
                    if (ReferenceEquals(entries[index].Parameter, viewModel)) entries.RemoveAt(index);
            }
        });

    public IAsyncDisposable Defer()
    {
        lock (_reviewGate) _deferrals++;
        return new Deferral(this);
    }

    private async ValueTask EndDeferralAsync()
    {
        PendingReview[] pending;
        lock (_reviewGate)
        {
            if (--_deferrals != 0) return;
            pending = _pending.ToArray();
            _pending.Clear();
        }
        List<Exception> errors = [];
        foreach (var review in pending)
        {
            try { await ShowCoreAsync(review.Session, review.Verification, review.Cancellation); }
            catch (OperationCanceledException) { /* The enclosing operation was cancelled before this review opened. */ }
            catch (Exception ex)
            {
                try { await dispatcher.RunOnUIThreadWithContextAsync(() => dialogs.ShowErrorAsync(ex.Message, text.GetString("UninstallResidue_Title"))); }
                catch (Exception presentationError) { errors.Add(presentationError); }
            }
            finally
            {
                try { await review.Session.DisposeAsync(); }
                catch (Exception disposalError) { errors.Add(disposalError); }
                finally { review.Reservation.Dispose(); }
            }
        }
        if (errors.Count > 0) throw new AggregateException("Some deferred uninstall reviews could not complete.", errors);
    }

    private sealed class Deferral(UninstallResidueReview owner) : IAsyncDisposable
    {
        private UninstallResidueReview? _owner = owner;
        public ValueTask DisposeAsync() => Interlocked.Exchange(ref _owner, null)?.EndDeferralAsync() ?? ValueTask.CompletedTask;
    }

    public Task ShowBackupsAsync() => dispatcher.RunOnUIThreadWithContextAsync(async () =>
    {
        using var operation = mode.BeginSystemOperation();
        using var viewModel = new UninstallBackupsViewModel(service, tasks, text, dispatcher);
        await viewModel.LoadAsync();
        var content = new UninstallBackupsControl(viewModel);
        using var layout = new DialogLayout(content);
        await dialogs.ShowCustomContentDialogAsync(text.GetString("UninstallResidue_Backups"), content, text.GetString("UninstallResidue_Finish"));
    });

    /// <summary>Sizes the recovery dialog to its window and releases root subscriptions when closed.</summary>
    private sealed class DialogLayout : IDisposable
    {
        private readonly FrameworkElement _content;
        private XamlRoot? _root;
        public DialogLayout(FrameworkElement content)
        {
            _content = content;
            content.Loaded += OnLoaded;
            content.Unloaded += OnUnloaded;
        }
        private void OnLoaded(object sender, RoutedEventArgs args)
        {
            DetachRoot();
            _root = _content.XamlRoot;
            _root.Changed += OnRootChanged;
            Update();
        }
        private void OnUnloaded(object sender, RoutedEventArgs args)
            => DetachRoot();
        private void DetachRoot()
        {
            if (_root != null) _root.Changed -= OnRootChanged;
            _root = null;
        }
        private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Update();
        private void Update()
        {
            if (_root == null) return;
            _content.Width = Math.Max(0, Math.Min(860, _root.Size.Width - 100));
            _content.MaxHeight = Math.Max(0, _root.Size.Height - 180);
        }
        public void Dispose()
        {
            DetachRoot();
            _content.Loaded -= OnLoaded;
            _content.Unloaded -= OnUnloaded;
        }
    }
}
