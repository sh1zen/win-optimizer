using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using WinOptimizer.Core.Features.Cleaner.Models;

namespace WinOptimizer.Infrastructure.Features.Cleaner;

/// <summary>Deletes the verified open file, holding an exclusive handle throughout validation.</summary>
internal static class CleanerFileDeletion
{
    public static void Delete(CleanerFileTarget evidence)
    {
        using var source = OpenVerified(evidence, deleting: true);
        MarkForDeletion(source.SafeFileHandle);
    }

    /// <summary>Locks the exact scanned file for either backup or deletion and preserves access timestamps.</summary>
    public static FileStream OpenVerified(CleanerFileTarget evidence, bool deleting)
    {
        var handle = CreateFile(evidence.Path, deleting ? 0x80010000u : 0x80010100u, 0, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        try
        {
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "The file is busy or cannot be opened for cleanup.");
            ValidateHandlePath(handle, evidence.Path);
            if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            long size = ((long)info.SizeHigh << 32) | info.SizeLow;
            long writeTime = ((long)info.LastWrite.dwHighDateTime << 32) | (uint)info.LastWrite.dwLowDateTime;
            long accessTime = ((long)info.LastAccess.dwHighDateTime << 32) | (uint)info.LastAccess.dwLowDateTime;
            if ((info.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 ||
                size != evidence.Length || DateTime.FromFileTimeUtc(writeTime) != evidence.LastWriteUtc ||
                // Shell link inspection itself updates access time; shortcut targets are revalidated separately.
                evidence.ShortcutTarget is null && DateTime.FromFileTimeUtc(accessTime) > evidence.CutoffUtc)
                throw new IOException("The file changed or was used after the scan. Scan again.");
            if (!deleting)
            {
                var freezeAccess = new System.Runtime.InteropServices.ComTypes.FILETIME { dwHighDateTime = -1, dwLowDateTime = -1 };
                if (!SetFileTime(handle, IntPtr.Zero, ref freezeAccess, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return new FileStream(handle, FileAccess.Read);
        }
        catch { handle.Dispose(); throw; }
    }

    public static void ValidateHandlePath(SafeFileHandle handle, string expectedPath)
    {
        if (!string.Equals(ResolvedPath(handle), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The cleanup path was redirected after the scan.");
    }

    /// <summary>Resolves existing ownership references, including short names and junction aliases, without reading file contents.</summary>
    public static string ResolveReferencePath(string path)
    {
        using var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (!handle.IsInvalid) return ResolvedPath(handle).TrimEnd('\\');
        var error = Marshal.GetLastWin32Error();
        if (error is 2 or 3) return Path.GetFullPath(path).TrimEnd('\\');
        throw new Win32Exception(error, "An installer ownership reference could not be resolved.");
    }

    private static string ResolvedPath(SafeFileHandle handle)
    {
        var finalPath = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandle(handle, finalPath, (uint)finalPath.Capacity, 0);
        if (length == 0 || length >= finalPath.Capacity) throw new IOException("Unable to verify the opened cleanup path.");
        var resolved = finalPath.ToString();
        if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) resolved = resolved[4..];
        return resolved;
    }

    /// <summary>Marks only the verified open file for removal when its handle closes.</summary>
    public static void MarkForDeletion(SafeFileHandle handle)
    {
        var disposition = new FileDisposition { DeleteFile = true };
        if (!SetFileInformationByHandle(handle, 4, ref disposition, (uint)Marshal.SizeOf<FileDisposition>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to remove the selected file.");
    }

    /// <summary>Creates a missing restore destination with exclusive access and cleanup rights.</summary>
    public static FileStream CreateRestoreFile(string path)
    {
        var handle = CreateFile(path, 0xC0010100, 0, IntPtr.Zero, 1, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "The restore destination already exists or is unavailable.");
        }
        try
        {
            ValidateHandlePath(handle, path);
            return new FileStream(handle, FileAccess.ReadWrite);
        }
        catch
        {
            try { MarkForDeletion(handle); }
            finally { handle.Dispose(); }
            throw;
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, LastAccess, LastWrite;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileDisposition { [MarshalAs(UnmanagedType.Bool)] public bool DeleteFile; }
    [DllImport("kernel32.dll", EntryPoint="CreateFileW", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint="GetFinalPathNameByHandleW", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
    [DllImport("kernel32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref FileDisposition info, uint size);
    [DllImport("kernel32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileTime(SafeFileHandle handle, IntPtr creation, ref System.Runtime.InteropServices.ComTypes.FILETIME access, IntPtr write);
}
