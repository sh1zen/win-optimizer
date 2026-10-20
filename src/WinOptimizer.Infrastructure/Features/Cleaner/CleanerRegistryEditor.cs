using Microsoft.Win32;

namespace WinOptimizer.Infrastructure.Features.Cleaner;

/// <summary>Owns a single registry handle for evidence checks and durable exact-value changes.</summary>
public interface ICleanerRegistryKey : IDisposable
{
    CleanerRegistryValue? ReadValue(string name);
    void DeleteValue(string name);
    void SetValue(CleanerRegistryValue value);
    /// <summary>Removes a named child only when it is empty; never deletes a subtree recursively.</summary>
    void DeleteEmptySubKey(string name);
    /// <summary>Creates a recovery child under this already-open parent.</summary>
    ICleanerRegistryKey CreateSubKey(string name);
}

/// <summary>Opens existing parents; selected recovery children are created only beneath their verified parent.</summary>
public interface ICleanerRegistryEditor
{
    ICleanerRegistryKey OpenKey(string path, bool view32, bool writable);
}

/// <summary>Keeps the native handle alive until verification or the selected correction finishes.</summary>
public sealed class WindowsCleanerRegistryEditor(Func<string, bool, bool, RegistryKey?> open) : ICleanerRegistryEditor
{
    public ICleanerRegistryKey OpenKey(string path, bool view32, bool writable) =>
        new OpenRegistryKey(open(path, view32, writable)
            ?? throw new IOException("The registry parent key is no longer present; it has been preserved."));

    private sealed class OpenRegistryKey(RegistryKey key) : ICleanerRegistryKey
    {
        public CleanerRegistryValue? ReadValue(string name)
        {
            var data = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return data is null ? null : new(name, data, key.GetValueKind(name));
        }

        public void DeleteValue(string name)
        {
            key.DeleteValue(name, throwOnMissingValue: true);
            key.Flush();
        }

        public void DeleteEmptySubKey(string name)
        {
            ValidateChildName(name);
            using (var child = key.OpenSubKey(name))
            {
                if (child is null) return;
                if (child.SubKeyCount != 0 || child.ValueCount != 0)
                    throw new IOException("The registry key contains unselected or newly created data; it has been preserved.");
            }
            key.DeleteSubKey(name, throwOnMissingSubKey: true);
            key.Flush();
        }

        public ICleanerRegistryKey CreateSubKey(string name)
        {
            ValidateChildName(name);
            return new OpenRegistryKey(key.CreateSubKey(name, writable: true));
        }

        private static void ValidateChildName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['\\', '/', '\0']) >= 0)
                throw new IOException("A single registry child name is required.");
        }

        public void SetValue(CleanerRegistryValue value)
        {
            key.SetValue(value.Name, value.Data, value.Kind);
            key.Flush();
        }

        public void Dispose() => key.Dispose();
    }
}
