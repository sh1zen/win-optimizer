namespace WinOptimizer.Core.Features.Common.Interfaces;

/// <summary>Reports a scan that has finished cancelling to the application's presentation layer.</summary>
public interface IScanCancellationNotifications
{
    void Publish(string title, string message);
}
