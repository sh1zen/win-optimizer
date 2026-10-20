using Microsoft.Win32;

namespace WinOptimizer.Infrastructure.Features.Cleaner;

public sealed record CleanerRegistryValue(string Name, object Data, RegistryValueKind Kind);

/// <summary>Read-only Windows boundary used by registry discovery and reference checks.</summary>
public interface ICleanerRegistryReader
{
    string[] GetSubKeyNames(string path, bool view32);
    IReadOnlyList<CleanerRegistryValue> GetValues(string path, bool view32, CancellationToken ct, ICollection<string>? notices = null);
    bool KeyExists(string path, bool view32);
}

/// <summary>Opens registry keys for one read and preserves value types for the scanner's rules.</summary>
public sealed class WindowsCleanerRegistryReader(Func<string, bool, bool, RegistryKey?> open) : ICleanerRegistryReader
{
    public string[] GetSubKeyNames(string path, bool view32)
    {
        using var key = open(path, view32, false);
        return key?.GetSubKeyNames() ?? [];
    }

    public IReadOnlyList<CleanerRegistryValue> GetValues(string path, bool view32, CancellationToken ct, ICollection<string>? notices = null)
    {
        using var key = open(path, view32, false);
        if (key is null) return [];
        List<CleanerRegistryValue> values = [];
        foreach (var name in key.GetValueNames())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var kind = key.GetValueKind(name);
                var data = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (data is not null) values.Add(new(name, data, kind));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // A disappearing or unreadable value must not discard the rest of this key.
                if (notices is null) throw;
                notices.Add($"{path}\\{name} ({(view32 ? "32-bit" : "64-bit")}): {ex.Message}");
            }
        }
        return values;
    }

    public bool KeyExists(string path, bool view32)
    {
        using var key = open(path, view32, false);
        return key is not null;
    }

}

