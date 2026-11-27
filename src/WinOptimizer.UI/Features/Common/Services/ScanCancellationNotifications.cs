using WinOptimizer.Core.Features.Common.Interfaces;

namespace WinOptimizer.UI.Features.Common.Services;

/// <summary>Delivers cancellation notices, with an optional view-model owner for the active results page.</summary>
public sealed class ScanCancellationNotifications : IScanCancellationNotifications
{
    public event Action<ScanCancellationNotice>? Cancelled;

    public void Publish(string title, string message) => Publish(null, title, message);

    internal void Publish(object? owner, string title, string message) => Cancelled?.Invoke(new(owner, title, message));
}

public sealed record ScanCancellationNotice(object? Owner, string Title, string Message);
