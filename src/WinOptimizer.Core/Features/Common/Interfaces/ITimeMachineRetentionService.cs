namespace WinOptimizer.Core.Features.Common.Interfaces;

/// <summary>Reads and applies the account's TimeMachine recovery retention policy.</summary>
public interface ITimeMachineRetentionService
{
    Task<int> RetentionDaysAsync();
    Task UpdateRetentionAsync(int days);
}
