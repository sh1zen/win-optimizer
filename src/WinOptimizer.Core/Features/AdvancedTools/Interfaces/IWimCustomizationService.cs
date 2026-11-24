using System;
using System.Threading;
using System.Threading.Tasks;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Core.Features.AdvancedTools.Interfaces;

public interface IWimCustomizationService
{
    Task<bool> AddDriversAsync(
        string workingDirectory,
        string? driverSourcePath = null,
        IProgress<TaskProgressDetail>? progress = null,
        CancellationToken cancellationToken = default);

    /// <exception cref="InvalidDataException">The file is not a well-formed Windows answer file.</exception>
    Task<bool> AddXmlToImageAsync(
        string xmlPath,
        string workingDirectory);
}
