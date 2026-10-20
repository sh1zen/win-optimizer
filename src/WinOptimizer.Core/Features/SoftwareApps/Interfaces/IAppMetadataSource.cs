using WinOptimizer.Core.Features.SoftwareApps.Models;

namespace WinOptimizer.Core.Features.SoftwareApps.Interfaces;

/// <summary>Reads sorting metadata for the supplied installed catalogue entries without changing Windows.</summary>
public interface IAppMetadataSource
{
    Task<IReadOnlyDictionary<string, AppMetadata>> ReadAsync(IReadOnlyList<ItemDefinition> definitions);
}
