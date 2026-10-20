using System.Security.Principal;
using WinOptimizer.Core.Features.Cleaner.Models;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Infrastructure.Features.Cleaner;

namespace WinOptimizer.Infrastructure.Features.SoftwareApps.Uninstall;

/// <summary>Uninstall discovery includes registered user and Windows service profiles.</summary>
public interface IUninstallRegistryProfiles : ICleanerRegistryProfiles { }
public interface IUninstallAccountSessions : ICleanerRegistrySessions { }

public sealed class WindowsUninstallRegistryProfiles : IUninstallRegistryProfiles
{
    public IReadOnlyList<CleanerRegistryProfile> Read() => WindowsCleanerRegistryProfiles.ReadCatalogue(includeSystemAccounts: true);
    public CleanerRegistryProfile? Find(string sid) => WindowsCleanerRegistryProfiles.FindInCatalogue(sid, includeSystemAccounts: true);
}

/// <summary>Shares the native hive owner with Cleaner while discovering a broader explicit uninstall account scope.</summary>
public sealed class WindowsUninstallAccountSessions(WindowsCleanerRegistrySessions sessions,
    IUninstallRegistryProfiles profiles, IInteractiveUserService user) : IUninstallAccountSessions
{
    public IReadOnlyList<CleanerRegistryAccount> GetAccounts()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var current = user.IsOtsElevation ? user.InteractiveUserSid : identity.User?.Value;
        return profiles.Read().Select(profile => new CleanerRegistryAccount(profile.Sid, profile.DisplayName, profile.Sid == current)).ToArray();
    }

    public ICleanerRegistrySession Open(string? sid) => sid == null ? sessions.Open(null) : sessions.OpenRegisteredProfile(
        profiles.Find(sid) ?? throw new IOException($"The Windows account is no longer registered: {sid}"));
}
