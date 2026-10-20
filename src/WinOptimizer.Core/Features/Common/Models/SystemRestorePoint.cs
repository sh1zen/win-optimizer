namespace WinOptimizer.Core.Features.Common.Models;

/// <summary>Identifies one Windows restore point independently of its displayed row.</summary>
public sealed record SystemRestorePoint(uint SequenceNumber, string Description, DateTimeOffset CreatedAt);
