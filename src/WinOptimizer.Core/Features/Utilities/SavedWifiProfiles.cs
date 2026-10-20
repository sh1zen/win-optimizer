namespace WinOptimizer.Core.Features.Utilities;

/// <summary>Distinguishes readable keys from open networks and unavailable or external credentials.</summary>
public enum WifiPasswordState { Available, OpenNetwork, NotStored, Unavailable }

/// <summary>A saved WLAN profile held only for the lifetime of the password page.</summary>
public sealed class SavedWifiProfile(string name, string adapter, string authentication, string encryption,
    string? password, WifiPasswordState passwordState)
{
    public string Name { get; } = name;
    public string Adapter { get; } = adapter;
    public string Authentication { get; } = authentication;
    public string Encryption { get; } = encryption;
    public string? Password { get; } = password;
    public WifiPasswordState PasswordState { get; } = passwordState;
    public override string ToString() => Name;
}

/// <summary>Preserves partial inventory and explicit discovery failures separately from an empty result.</summary>
public sealed record SavedWifiSnapshot(IReadOnlyList<SavedWifiProfile> Profiles, string Diagnostic);

/// <summary>Reads saved local profiles on demand without persisting credentials.</summary>
public interface ISavedWifiService
{
    Task<SavedWifiSnapshot> ReadAsync(CancellationToken cancellationToken);
}
