using System.ComponentModel;
using WinOptimizer.Infrastructure.Features.Cleaner;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Cleaner;

/// <summary>Exercises the production hive owner with a fake privileged Windows boundary and isolated files.</summary>
public sealed class WindowsCleanerRegistryHiveAccessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hive-mount-" + Guid.NewGuid().ToString("N"));
    private readonly NativeFixture _native = new();

    [Fact]
    public void MountAndUnmountPassThePredefinedUsersRootAndExactOwnedMount()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "NTUSER.DAT");
        File.WriteAllText(file, "synthetic hive supplied to the fake native API");
        var access = new WindowsCleanerRegistryHiveAccess(_native);
        var mount = access.Mount(file);
        access.Unmount(mount);
        var root = new IntPtr(unchecked((int)0x80000003));
        Assert.Equal(root, _native.LoadRoot); Assert.Equal(root, _native.UnloadRoot);
        Assert.Equal(file, _native.File); Assert.Equal(mount, _native.LoadName); Assert.Equal(mount, _native.UnloadName);
        Assert.StartsWith("WinOptimizer_Cleaner_", mount);
        Assert.True(Guid.TryParseExact(mount["WinOptimizer_Cleaner_".Length..], "N", out _));
        Assert.Equal("synthetic hive supplied to the fake native API", File.ReadAllText(file));
    }

    [Fact]
    public void MissingHiveIsRejectedBeforeWindowsCanCreateAFile()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "NTUSER.DAT");
        Assert.Throws<IOException>(() => new WindowsCleanerRegistryHiveAccess(_native).Mount(file));
        Assert.Null(_native.File); Assert.False(File.Exists(file));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(32)]
    [InlineData(87)]
    [InlineData(1009)]
    public void FailedMountPreservesTheNativeErrorAndReportsItsCode(int status)
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "NTUSER.DAT"); File.WriteAllBytes(file, []);
        _native.Status = status;
        var error = Assert.Throws<IOException>(() => new WindowsCleanerRegistryHiveAccess(_native).Mount(file));
        Assert.Equal(status, Assert.IsType<Win32Exception>(error.InnerException).NativeErrorCode);
        Assert.Contains(file, error.Message); Assert.Contains("Windows error " + status, error.Message);
        Assert.Null(_native.UnloadName);
    }

    [Fact]
    public void FailedUnmountReportsTheOwnedMountAndPreservesTheNativeFailure()
    {
        _native.Status = 170;
        var error = Assert.Throws<IOException>(() => new WindowsCleanerRegistryHiveAccess(_native).Unmount("WinOptimizer_Cleaner_fixture"));
        Assert.Contains("WinOptimizer_Cleaner_fixture", error.Message); Assert.Contains("Windows error 170", error.Message);
        Assert.Equal(170, Assert.IsType<Win32Exception>(error.InnerException).NativeErrorCode);
    }

    public void Dispose()
    {
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(_root), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("hive-mount-", Path.GetFileName(_root));
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>Records native parameters without enabling privileges or loading any Windows account hive.</summary>
    private sealed class NativeFixture : IRegistryHiveMountApi
    {
        public int Status { get; set; }
        public IntPtr LoadRoot { get; private set; }
        public IntPtr UnloadRoot { get; private set; }
        public string? File { get; private set; }
        public string? LoadName { get; private set; }
        public string? UnloadName { get; private set; }
        public int Load(IntPtr root, string name, string file) { LoadRoot = root; LoadName = name; File = file; return Status; }
        public int Unload(IntPtr root, string name) { UnloadRoot = root; UnloadName = name; return Status; }
    }
}
