using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WinOptimizer.Core.Features.Disks;
using WinOptimizer.Infrastructure.Features.Cleaner;

namespace WinOptimizer.Infrastructure.Features.Disks;

/// <summary>Deletes the selected file by handle and retries after releasing locks on the same file identity.</summary>
internal sealed class DiskFileDeletion(Func<string, IFileLockSession> openLockSession)
{
    public IReadOnlyList<DiskProcess> GetLocks(string input)
    {
        ReadIdentity(input, out var path);
        using var session = openLockSession(path);
        return session.GetLocks();
    }

    public bool Unlock(string input, CancellationToken ct)
    {
        var identity = ReadIdentity(input, out var path);
        using var session = openLockSession(path);
        var locks = session.GetLocks();
        if (locks.Count == 0) return false;
        ReleaseLocks(session, locks, path, identity, ct);
        if (session.GetLocks().Count > 0)
            throw new IOException("Windows still reports processes using the selected file.");
        return true;
    }

    public void Delete(string input, CancellationToken ct)
    {
        var identity = ReadIdentity(input, out var path);
        ct.ThrowIfCancellationRequested();
        if (TryDelete(path, identity)) return;

        using var session = openLockSession(path);
        var locks = session.GetLocks();
        if (locks.Count == 0) throw new IOException("The file is busy, but Windows identified no process it can close.");
        try { ReleaseLocks(session, locks, path, identity, ct); }
        catch (Win32Exception)
        {
            if (TryDelete(path, identity)) return;
            throw;
        }
        // Once a locker has been closed, finish the requested deletion before reporting to the caller.
        if (!TryDelete(path, identity))
            throw new IOException("The file is still in use after Windows closed the locking applications.");
    }

    private static void ReleaseLocks(IFileLockSession session, IReadOnlyList<DiskProcess> locks, string path,
        FileIdentity identity, CancellationToken ct)
    {
        if (locks.Any(process => process.Id == Environment.ProcessId))
            throw new IOException("WinOptimizer is using the selected file. Close the operation that uses it and try again.");
        if (ReadIdentity(path, out _) != identity)
            throw new IOException("The selected file changed while Windows was identifying its locks.");
        ct.ThrowIfCancellationRequested();
        session.ForceShutdown();
    }

    private static FileIdentity ReadIdentity(string input, out string path)
    {
        path = DiskPaths.LocalPath(input);
        var root = DiskPaths.Drive(Path.GetPathRoot(path)!).Name;
        if (DiskPaths.CheckTree(path, root)!.Value.HasFlag(FileAttributes.Directory))
            throw new ArgumentException("Select a single file to delete.");
        using var handle = Open(path, 0);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        return Identity(handle, path);
    }

    private static bool TryDelete(string path, FileIdentity expected)
    {
        const uint deleteAccess = 0x00010000;
        using var file = Open(path, deleteAccess);
        if (file.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            if (error is 32 or 33) return false; // Sharing or lock violation.
            throw new Win32Exception(error);
        }
        if (Identity(file, path) != expected)
            throw new IOException("The selected file changed while its locks were being released.");
        uint deletePosixIgnoringReadOnly = 0x01 | 0x02 | 0x10;
        if (SetFileInformationByHandle(file, 21, in deletePosixIgnoringReadOnly, sizeof(uint))) return true;
        int dispositionError = Marshal.GetLastWin32Error();
        if (dispositionError is 32 or 33 or 1224) return false; // Includes mapped files.
        if (dispositionError is not (1 or 50 or 87)) throw new Win32Exception(dispositionError);
        CleanerFileDeletion.MarkForDeletion(file);
        return true;
    }

    private static SafeFileHandle Open(string path, uint access) =>
        CreateFile(@"\\?\" + path, access, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);

    private static FileIdentity Identity(SafeFileHandle file, string path)
    {
        CleanerFileDeletion.ValidateHandlePath(file, path);
        if (!GetFileInformationByHandle(file, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((info.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException("The selected path no longer refers to a regular file.");
        return new(info.VolumeSerial, info.IndexHigh, info.IndexLow, info.Creation);
    }

    private readonly record struct FileIdentity(uint Volume, uint IndexHigh, uint IndexLow,
        System.Runtime.InteropServices.ComTypes.FILETIME Created);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, LastAccess, LastWrite;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, in uint flags, uint size);
}
