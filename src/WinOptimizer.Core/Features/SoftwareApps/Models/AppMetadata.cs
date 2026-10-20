namespace WinOptimizer.Core.Features.SoftwareApps.Models;

/// <summary>Observed installation and shell-usage data; null fields mean Windows supplied no evidence.</summary>
public sealed record AppMetadata(DateTimeOffset? InstallationDate, long? SizeBytes, DateTimeOffset? LastUsageTime);
