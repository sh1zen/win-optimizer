using System.Security.Principal;
using WinOptimizer.Core.Features.Common.Interfaces;

namespace WinOptimizer.Core.Features.Common.Constants;

/// <summary>Owns the application data layout; resolving paths performs no filesystem mutations.</summary>
public static class AppDataPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "win-optimizer");
    public static string Logs => Path.Combine(Root, "Logs");
    public static string History => Path.Combine(Root, "History");
    public static string Backups => Path.Combine(Root, "Backups");
    public static string CleanerBackups => Path.Combine(Backups, "Cleaner");
    public static string EventLogBackups => Path.Combine(Backups, "EventLogs");
    public static string PowerPlanBackups => Path.Combine(Backups, "PowerPlans");
    public static string IconCache => Path.Combine(Root, "Cache", "Icons");
    public static string WinGetCache => Path.Combine(Root, "Cache", "WinGet");
    public static string Tools => Path.Combine(Root, "Tools");
    public static string Scripts => Path.Combine(Root, "Scripts");
    public static string Downloads => Path.Combine(Root, "Downloads");
    public static string Apps => Path.Combine(Root, "Apps");
    public static string Temp => Path.Combine(Root, "Temp");
    public static string State => Path.Combine(Root, "State");

    /// <summary>Accepts a single untrusted file or directory name without permitting path traversal.</summary>
    public static string Child(string parent, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || name.EndsWith(' '))
            throw new ArgumentException("Use a plain file or directory name.", nameof(name));
        return Path.Combine(parent, name);
    }

    /// <summary>Identifies the account whose personal settings are being edited, including alternate-account elevation.</summary>
    public static string InteractiveUserSid(IInteractiveUserService user)
    {
        if (user.InteractiveUserSid is { } sid) return ValidateSid(sid);
        using var account = WindowsIdentity.GetCurrent();
        return account.User?.Value ?? throw new UnauthorizedAccessException("The Windows account is unavailable.");
    }

    /// <summary>Returns an account directory only for a canonical Windows SID.</summary>
    public static string User(string sid) => Path.Combine(Root, "Users", ValidateSid(sid));
    public static string Preferences(IInteractiveUserService user) =>
        Path.Combine(User(InteractiveUserSid(user)), "Config", "UserPreferences.json");
    public static string ConfigurationBackups(IInteractiveUserService user) =>
        Path.Combine(User(InteractiveUserSid(user)), "Backups", "Configurations");

    private static string ValidateSid(string sid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sid);
        if (!new SecurityIdentifier(sid).Value.Equals(sid, StringComparison.Ordinal))
            throw new ArgumentException("Use a canonical Windows account SID.", nameof(sid));
        return sid;
    }
}
