namespace WinOptimizer.Core.Features.Management;

/// <summary>Separates ordinary startup controls from the complete Sysinternals inventory.</summary>
public enum AutorunView { Startup, Advanced }

/// <summary>A configured startup registration; an unknown state must not be presented as enabled.</summary>
public sealed record AutorunEntry(string Id, string Name, string Category, string Location,
    string Command, string ImagePath, string Publisher, string Signer, string Profile,
    bool? Enabled, bool CanToggle, string Details);

/// <summary>A dated inventory, including access failures and the account coverage of the scan.</summary>
public sealed record AutorunInventory(AutorunView View, DateTimeOffset CapturedAt,
    IReadOnlyList<AutorunEntry> Entries, IReadOnlyList<string> Warnings, string Coverage);

/// <summary>Manages the app's own sign-in launch and registrations authorized by a current startup snapshot.</summary>
public interface IAutorunService
{
    Task<AutorunInventory> ReadAsync(AutorunView view, bool acceptSysinternalsLicense, CancellationToken cancellationToken);
    Task SetEnabledAsync(string entryId, bool enabled, CancellationToken cancellationToken);
    /// <summary>Reads the current account's app task. A different desktop account has no editable state.</summary>
    Task<bool?> ReadApplicationStartupAsync();
    /// <summary>Enables or removes the current account's elevated sign-in task.</summary>
    Task SetApplicationStartupAsync(bool enabled);
    Task OpenStartupSettingsAsync();
}
