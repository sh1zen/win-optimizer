using System.Collections.Generic;
using System.Threading.Tasks;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Core.Features.Common.Interfaces;

public interface ISystemSettingsDiscoveryService
{
    Task<Dictionary<string, Dictionary<string, object?>>> GetRawSettingsValuesAsync(IEnumerable<SettingDefinition> settings);
    Task<Dictionary<string, SettingStateResult>> GetSettingStatesAsync(IEnumerable<SettingDefinition> settings);
    /// <summary>Reads interpreted state without constructing presentation-only tooltip values.</summary>
    Task<Dictionary<string, SettingStateResult>> GetSettingStateSnapshotAsync(IReadOnlyList<SettingDefinition> settings) =>
        GetSettingStatesAsync(settings);
}
