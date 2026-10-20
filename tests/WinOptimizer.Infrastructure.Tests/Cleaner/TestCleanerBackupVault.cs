using System.Security.Cryptography;
using WinOptimizer.Infrastructure.Features.Cleaner;

namespace WinOptimizer.Infrastructure.Tests.Cleaner;

/// <summary>Isolates Windows ACL/DPAPI operations while retaining deterministic metadata tamper detection.</summary>
internal sealed class TestCleanerBackupVault(string root) : ICleanerBackupVault
{
    public string RootDirectory => root;
    public void EnsureDirectory(string path) { ValidatePath(path); Directory.CreateDirectory(path); }
    public void ValidateDirectory(string path) { ValidatePath(path); if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path); }
    private void ValidatePath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) && !full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Test vault path escaped its root.");
    }
    public byte[] Seal(byte[] data) => [.. SHA256.HashData(data), .. data];
    public byte[] Unseal(byte[] data)
    {
        if (data.Length < 32 || !CryptographicOperations.FixedTimeEquals(data.AsSpan(0, 32), SHA256.HashData(data.AsSpan(32))))
            throw new CryptographicException("Damaged test metadata.");
        return data[32..];
    }
}
