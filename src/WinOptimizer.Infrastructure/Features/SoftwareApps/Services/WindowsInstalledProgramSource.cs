using System.Security.Cryptography;
using System.Text;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;

namespace WinOptimizer.Infrastructure.Features.SoftwareApps.Services;

/// <summary>Uninstall registration evidence, including hidden records used by catalogue fingerprints.</summary>
public sealed record InstalledProgram(string KeyPath, string Name, string? UninstallCommand,
    bool IsSystemComponent = false, string? ParentKeyName = null, string? ReleaseType = null, string? WebsiteUrl = null,
    string? DisplayIcon = null)
{
    public string KeyName => KeyPath[(KeyPath.LastIndexOf('\\') + 1)..];
    public bool IsVisible => !string.IsNullOrWhiteSpace(Name) && !IsSystemComponent &&
        string.IsNullOrWhiteSpace(ParentKeyName) &&
        !new[] { "Update", "Hotfix", "Security Update" }.Contains(ReleaseType, StringComparer.OrdinalIgnoreCase);

    /// <summary>Uses catalogue fingerprints before exact product names; never merges by a fuzzy name.</summary>
    public bool Matches(ItemDefinition item) =>
        item.RegistryUninstallKeyPath is { } exact ? string.Equals(KeyPath, exact, StringComparison.OrdinalIgnoreCase) :
        string.Equals(KeyName, item.Name, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Name, item.Name, StringComparison.OrdinalIgnoreCase) ||
        (!string.IsNullOrEmpty(item.RegistrySubKeyName) && AppStatusDiscoveryService.MatchesPattern(KeyName, item.RegistrySubKeyName)) ||
        (!string.IsNullOrEmpty(item.RegistryDisplayName) && AppStatusDiscoveryService.MatchesPattern(Name, item.RegistryDisplayName));

    /// <summary>Creates a stable, filename-safe identity without persisting an executable command.</summary>
    public ItemDefinition ToDefinition() => new()
    {
        Id = "installed-program-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(KeyPath.ToUpperInvariant()))).ToLowerInvariant(),
        Name = Name,
        Description = string.Empty,
        GroupName = "Other Utilities",
        RegistryUninstallKeyPath = KeyPath,
        RegistrySubKeyName = KeyName,
        CanBeReinstalled = false,
        WebsiteUrl = Uri.TryCreate(WebsiteUrl, UriKind.Absolute, out var url) && url.Scheme is "https" or "http" ? url.AbsoluteUri : null,
    };
}

/// <summary>Separates installed-program discovery and fresh command lookup from application operations.</summary>
public interface IInstalledProgramSource
{
    Task<IReadOnlyList<InstalledProgram>> ReadAsync();
    Task<string?> GetUninstallCommandAsync(string keyPath);
}

/// <summary>Reads both registry views for the machine and interactive user through the shared registry boundary.</summary>
public sealed class WindowsInstalledProgramSource(IWindowsRegistryService registry) : IInstalledProgramSource
{
    internal static readonly string[] Roots =
    [
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"HKCU\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    ];

    public Task<IReadOnlyList<InstalledProgram>> ReadAsync() => Task.Run<IReadOnlyList<InstalledProgram>>(() =>
    {
        var result = new List<InstalledProgram>();
        foreach (var root in Roots)
        foreach (var name in registry.GetSubKeyNames(root))
        {
            var path = root + "\\" + name;
            if (!IsProgramKey(path)) continue;
            result.Add(new InstalledProgram(path, registry.GetValue(path, "DisplayName") as string ?? "",
                registry.GetValue(path, "UninstallString") as string,
                registry.GetValue(path, "SystemComponent") is int component && component == 1,
                registry.GetValue(path, "ParentKeyName") as string,
                registry.GetValue(path, "ReleaseType") as string,
                registry.GetValue(path, "URLInfoAbout") as string,
                registry.GetValue(path, "DisplayIcon") as string));
        }
        return result;
    });

    /// <summary>Only reads commands from direct children of known uninstall roots, using fresh registry values.</summary>
    public Task<string?> GetUninstallCommandAsync(string keyPath)
    {
        if (!IsProgramKey(keyPath)) throw new ArgumentException("Invalid installed-program registry key.", nameof(keyPath));
        return Task.Run(() => registry.GetValue(keyPath, "UninstallString") as string);
    }

    private static bool IsProgramKey(string path) => Roots.Any(root =>
        path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) && path.Length > root.Length + 1 &&
        !path[(root.Length + 1)..].Contains('\\'));
}
