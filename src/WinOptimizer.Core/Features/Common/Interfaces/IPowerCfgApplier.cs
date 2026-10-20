using System.Threading.Tasks;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Core.Features.Common.Interfaces;

public interface IPowerCfgApplier
{
    Task<OperationResult> ApplyPowerCfgSettingsAsync(SettingDefinition setting, bool enable, object? value);
}
