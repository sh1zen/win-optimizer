namespace WinOptimizer.Core.Features.Common.Interfaces;

public interface IPolicyCleanupService
{
    /// <summary>Removes managed policy values and reports any writes that failed.</summary>
    int CleanupPolicyValues();
}
