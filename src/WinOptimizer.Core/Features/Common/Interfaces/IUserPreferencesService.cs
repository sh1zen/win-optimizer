using System.Collections.Generic;
using System.Threading.Tasks;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Core.Features.Common.Interfaces;

public interface IUserPreferencesService
{
    Task<Dictionary<string, object>> GetPreferencesAsync();
    Task<OperationResult> SavePreferencesAsync(Dictionary<string, object> preferences);
    /// <summary>Atomically merges a group of related fields while preserving other concurrent preference updates.</summary>
    Task<OperationResult> UpdatePreferencesAsync(IReadOnlyDictionary<string, object> changes);
    Task<T> GetPreferenceAsync<T>(string key, T defaultValue);
    Task<OperationResult> SetPreferenceAsync<T>(string key, T value);

    /// <summary>
    /// Synchronous version for use during startup to avoid async deadlocks.
    /// </summary>
    T GetPreference<T>(string key, T defaultValue);
}
