using WinOptimizer.Core.Features.SoftwareApps.Interfaces;

namespace WinOptimizer.Core.Features.SoftwareApps.Models;

/// <summary>Holds the definitions and status reader for one external app inventory refresh.</summary>
public sealed class ExternalAppInventory(IReadOnlyList<ItemDefinition> applications,
    Func<IEnumerable<ItemDefinition>, Task<Dictionary<string, bool>>> readStatus)
{
    public IReadOnlyList<ItemDefinition> Applications { get; } = applications;
    public IReadOnlyList<IRegisteredDesktopIcon>? DesktopRegistrations { get; init; }

    /// <summary>Checks the reconciled definitions against this refresh's evidence.</summary>
    public Task<Dictionary<string, bool>> ReadStatusAsync(IEnumerable<ItemDefinition> definitions) => readStatus(definitions);
}

/// <summary>Registry evidence for installation checks, including hidden entries and exact registration paths.</summary>
public sealed record AppRegistrySnapshot(IReadOnlySet<string> KeyNames, IReadOnlySet<string> DisplayNames,
    IReadOnlySet<string> AllKeyNames, IReadOnlySet<string> KeyPaths);
