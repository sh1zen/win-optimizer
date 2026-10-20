using WinOptimizer.Core.Features.SoftwareApps.Models;

namespace WinOptimizer.Core.Features.SoftwareApps.Interfaces;

/// <summary>Reads capability and optional-feature status from Windows servicing.</summary>
public interface IWindowsComponentStatusSource
{
    /// <summary>Checks requested components in order, without changing their state.</summary>
    Task<WindowsComponentStatus> GetStatusAsync(
        IReadOnlyList<string> capabilities, IReadOnlyList<string> features);
}
