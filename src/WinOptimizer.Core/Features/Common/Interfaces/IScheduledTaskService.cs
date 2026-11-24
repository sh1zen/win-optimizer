using System;
using System.Collections.Generic;
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
    /// <summary>Reads an app logon task, treating only a missing task or folder as disabled.</summary>
    Task<bool> IsUserLogonApplicationEnabledAsync(string taskName);
    Task<OperationResult> EnableTaskAsync(string taskPath);
    Task<OperationResult> DisableTaskAsync(string taskPath);
    Task<bool?> IsTaskEnabledAsync(string taskPath);

    /// <summary>Reads states in input order, keeping failures local to each task.</summary>
    async Task<(bool? Enabled, Exception? Error)[]> GetTaskStatesAsync(IReadOnlyList<string> taskPaths)
    {
        var states = new (bool?, Exception?)[taskPaths.Count];
        for (var index = 0; index < states.Length; index++)
        {
            try { states[index] = (await IsTaskEnabledAsync(taskPaths[index]).ConfigureAwait(false), null); }
            catch (Exception ex) { states[index] = (null, ex); }
        }
        return states;
    }
}
