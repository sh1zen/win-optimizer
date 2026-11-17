namespace WinOptimizer.Core.Features.SoftwareApps.Models;

/// <summary>Installed identities and optional registrations from a single account enumeration.</summary>
public sealed record AppxInventorySnapshot(HashSet<string> Names, IReadOnlyList<AppxRegistration>? Registrations = null);

/// <summary>Defers reading detailed evidence until an installed catalogue item needs it.</summary>
public sealed record AppxRegistration(string Name, Func<AppxRegistrationDetails> ReadDetails);

public readonly record struct AppxRegistrationDetails(string FullName, string Family, DateTimeOffset Installed,
    string Path, bool IsDevelopmentMode);
