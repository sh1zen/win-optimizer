using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Infrastructure.Features.Common.Interfaces;

/// <summary>Isolates blocking WMI and native restore-point calls from application coordination.</summary>
public interface ISystemRestorePointStore
{
    IReadOnlyList<SystemRestorePoint> Read(CancellationToken cancellationToken);
    void Delete(uint sequenceNumber);
}
