using System.Collections.Generic;
using System.Threading.Tasks;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Core.Features.Common.Interfaces;

public interface IConfigApplicationExecutionService
{
    Task ExecuteConfigImportAsync(UnifiedConfigurationFile config, ImportOptions options);
    Task ApplyConfigurationWithOptionsAsync(
        UnifiedConfigurationFile config,
        List<string> selectedSections,
        ImportOptions options);
}
