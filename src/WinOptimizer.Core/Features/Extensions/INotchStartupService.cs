using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Core.Features.Extensions;

/// <summary>Reconciles the current user's Notch logon task.</summary>
public interface INotchStartupService
{
    Task<OperationResult> ApplyAsync(bool enabled);
}
