namespace WinOptimizer.Core.Features.Optimize.Interfaces;

/// <summary>Measures the current connection without changing Windows settings.</summary>
public interface IInternetSpeedTest
{
    Task<InternetSpeedTestResult> MeasureAsync(CancellationToken cancellationToken);
}

/// <summary>Estimated application throughput and median idle HTTPS latency to the test server.</summary>
public sealed record InternetSpeedTestResult(double DownloadMbps, double UploadMbps, double LatencyMilliseconds);
