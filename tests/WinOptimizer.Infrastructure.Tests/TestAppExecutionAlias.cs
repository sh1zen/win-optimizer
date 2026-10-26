using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinOptimizer.Infrastructure.Tests;

/// <summary>Creates an isolated Windows app execution alias without installed packages or executable targets.</summary>
internal sealed class TestAppExecutionAlias : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "optimizer-alias-" + Guid.NewGuid().ToString("N") + ".exe");

    public TestAppExecutionAlias(uint tag = 0x8000001B)
    {
        try
        {
            using var file = File.OpenHandle(Path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            var strings = Encoding.Unicode.GetBytes("Fixture.Package_123\0FixtureApp\0C:\\Fixture\\app.exe\0");
            var data = new byte[12 + strings.Length];
            BitConverter.GetBytes(tag).CopyTo(data, 0);
            BitConverter.GetBytes((ushort)(4 + strings.Length)).CopyTo(data, 4);
            BitConverter.GetBytes(3u).CopyTo(data, 8);
            strings.CopyTo(data, 12);
            if (!DeviceIoControl(file, 0x000900A4, data, (uint)data.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch { File.Delete(Path); throw; }
    }

    public void Dispose() => File.Delete(Path);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint length,
        IntPtr output, uint outputLength, out uint returned, IntPtr overlapped);
}
