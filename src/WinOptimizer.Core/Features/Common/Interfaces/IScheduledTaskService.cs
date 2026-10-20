using System;
using System.Threading;
using System.Threading.Tasks;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.SoftwareApps.Models;

namespace WinOptimizer.Core.Features.Common.Interfaces;

public interface IScheduledTaskService
{
    Task<OperationResult> RegisterScheduledTaskAsync(RemovalScript script);
    Task<OperationResult> UnregisterScheduledTaskAsync(string taskName);
    Task<bool> IsTaskRegisteredAsync(string taskName);
    Task<OperationResult> RunScheduledTaskAsync(string taskName);
    Task<OperationResult> CreateUserLogonTaskAsync(string taskName, string command, string username, bool deleteAfterRun = true);
    /// <summary>Registers a persistent interactive desktop launch, or removes that exact task.</summary>
    Task<OperationResult> ConfigureUserLogonApplicationAsync(string taskName, string executablePath, string arguments, string userSid, bool enabled);
    Task<OperationResult> EnableTaskAsync(string taskPath);
    Task<OperationResult> DisableTaskAsync(string taskPath);
    Task<bool?> IsTaskEnabledAsync(string taskPath);
}
