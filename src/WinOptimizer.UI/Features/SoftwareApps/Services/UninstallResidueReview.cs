using Microsoft.UI.Xaml;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.SoftwareApps.ViewModels;

namespace WinOptimizer.UI.Features.SoftwareApps.Services;

/// <summary>Marshals residue/recovery dialogs to the UI thread while their owning workflow retains the session.</summary>
public sealed class UninstallResidueReview(IUninstallResidueService service, IDialogService dialogs,
    IDispatcherService dispatcher, ITaskProgressService tasks, ILocalizationService text,
    IApplicationModeService mode) : IUninstallResidueReview
{
    private readonly object _reviewGate = new();
    private readonly Queue<PendingReview> _pending = new();
    private int _deferrals;
    private sealed record PendingReview(IUninstallResidueSession Session, UninstallResidueScan Scan, CancellationToken Cancellation, IDisposable Reservation);

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

    public async Task<UninstallReviewDisposition> ShowAsync(IUninstallResidueSession session, UninstallResidueScan scan, CancellationToken ct)
    {
        lock (_reviewGate)
        {
            if (_deferrals > 0)
            {
                _pending.Enqueue(new(session, scan, ct, mode.BeginSystemOperation()));
                return UninstallReviewDisposition.Deferred;
            }
        }
        await ShowCoreAsync(session, scan, ct);
        return UninstallReviewDisposition.Completed;
    }

    private Task ShowCoreAsync(IUninstallResidueSession session, UninstallResidueScan scan, CancellationToken ct) =>
        dispatcher.RunOnUIThreadWithContextAsync(async () =>
        {
            ct.ThrowIfCancellationRequested();
            using var viewModel = new UninstallResidueViewModel(session, scan, service, tasks, text);
            var content = new UninstallResidueReviewControl(viewModel);
            using var layout = new DialogLayout(content);
            await dialogs.ShowCustomContentDialogAsync(text.GetString("UninstallResidue_Title"), content, text.GetString("UninstallResidue_Finish"));
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
            try { await ShowCoreAsync(review.Session, review.Scan, review.Cancellation); }
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
        var viewModel = new UninstallBackupsViewModel(service, tasks, text);
        await viewModel.LoadAsync();
        var content = new UninstallBackupsControl(viewModel);
        using var layout = new DialogLayout(content);
        await dialogs.ShowCustomContentDialogAsync(text.GetString("UninstallResidue_Backups"), content, text.GetString("UninstallResidue_Finish"));
    });

    /// <summary>Sizes both review dialogs to their current window and releases root subscriptions when closed.</summary>
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
