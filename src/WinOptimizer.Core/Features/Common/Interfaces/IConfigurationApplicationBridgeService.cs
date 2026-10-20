using System;
using System.Threading.Tasks;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Core.Features.Common.Interfaces;

public interface IConfigurationApplicationBridgeService
{
    Task<bool> ApplyConfigurationSectionAsync(
        ConfigSection section,
        string sectionName,
        Func<string, object?, SettingDefinition, Task<(bool confirmed, bool checkboxResult)>>? confirmationHandler = null);
}
