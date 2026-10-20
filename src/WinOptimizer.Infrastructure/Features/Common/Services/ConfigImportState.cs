using WinOptimizer.Core.Features.Common.Interfaces;

namespace WinOptimizer.Infrastructure.Features.Common.Services;

public class ConfigImportState : IConfigImportState
{
    public bool IsActive { get; set; }
    public string? SourceName { get; set; }
    public bool ImportSuppliesPowerValues { get; set; }
}
