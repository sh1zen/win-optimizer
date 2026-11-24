using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace WinOptimizer.Tests.Fixtures;

/// <summary>Runs native registry operations against a temporary file accessible only through its process-local handle.</summary>
internal sealed class PrivateRegistryHive : IDisposable
{
    private const int KeyAllAccess = 0xF003F;
    private const int ProcessAppKey = 1;
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("win-optimizer-hive-");
    private readonly Dictionary<RegistryHive, RegistryKey> _roots = new();
    public RegistryKey Root { get; }

    public PrivateRegistryHive()
    {
        try
        {
            var result = RegLoadAppKeyW(Path.Combine(_directory.FullName, "fixture.hiv"),
                out var handle, KeyAllAccess, ProcessAppKey, 0);
            if (result != 0)
            {
                handle?.Dispose();
                throw new Win32Exception(result, $"Could not open the private registry fixture (Win32 error {result}).");
            }
            Root = RegistryKey.FromHandle(handle);
        }
        catch
        {
            _directory.Delete(recursive: true);
            throw;
        }
    }

    /// <summary>Provides stable, distinct roots for the same hive names used by the production service.</summary>
    public RegistryKey GetRoot(RegistryHive hive)
    {
        if (!_roots.TryGetValue(hive, out var root))
        {
            root = Root.CreateSubKey(hive.ToString());
            _roots.Add(hive, root);
        }
        return root;
    }

    public void Dispose()
    {
        foreach (var key in _roots.Values) key.Dispose();
        Root.Dispose();
        _directory.Delete(recursive: true);
    }

    [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int RegLoadAppKeyW(string file, out SafeRegistryHandle root, int access, int options, int reserved);
}
