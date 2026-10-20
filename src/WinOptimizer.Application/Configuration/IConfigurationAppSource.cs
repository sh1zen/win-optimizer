using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Application.Configuration;

/// <summary>Captures app selection or installed state in the configuration boundary format.</summary>
public interface IConfigurationAppSource
{
    Task<ConfigSection> GetWindowsAppsAsync(bool useInstalledStatus);
    Task<ConfigSection> GetExternalAppsAsync();
}
