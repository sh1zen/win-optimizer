namespace WinOptimizer.Core.Features.Extensions;

/// <summary>Reads local telemetry explicitly; implementations serialize samples and support cancellation.</summary>
public interface ISystemMonitorService
{
    Task<SystemMonitorSnapshot> ReadAsync(CancellationToken cancellationToken);
    void Reset();
    Task StopAsync();
}
