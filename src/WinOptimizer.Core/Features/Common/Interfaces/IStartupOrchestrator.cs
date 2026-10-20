using System;
using System.Threading.Tasks;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Core.Features.Common.Interfaces;

/// <summary>
/// Separates shell initialization from inventory and backup work started after the first visible frame.
/// </summary>
public interface IStartupOrchestrator
{
    /// <summary>
    /// Initializes settings needed by the shell without starting app or component discovery.
    /// </summary>
    /// <param name="statusProgress">Reports localization keys for loading overlay text.</param>
    /// <param name="detailedProgress">Receives detailed initialization progress.</param>
    /// <returns>The shell initialization result.</returns>
    Task<StartupResult> RunStartupSequenceAsync(
        IProgress<string> statusProgress,
        IProgress<TaskProgressDetail> detailedProgress);

    /// <summary>Loads app/component inventory and creates the initial full backup after the shell is visible.</summary>
    Task<StartupResult> RunDeferredInitializationAsync();
}
