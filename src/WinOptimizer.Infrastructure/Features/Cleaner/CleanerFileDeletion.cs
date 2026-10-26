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
    public static FileStream OpenVerified(CleanerFileTarget evidence, bool deleting, bool asynchronous = false)
    {
        var flags = 0x00200000u | (asynchronous ? 0x40000000u : 0);
        var handle = CreateFile(NativePath(evidence.Path), deleting ? 0x80010000u : 0x80010100u, 0, IntPtr.Zero, 3, flags, IntPtr.Zero);
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
            return new FileStream(handle, FileAccess.Read, 4096, asynchronous);
        }
        catch { handle.Dispose(); throw; }
    }

    public static void ValidateHandlePath(SafeFileHandle handle, string expectedPath)
    {
        if (!string.Equals(ResolvedPath(handle), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The cleanup path was redirected after the scan.");
    }

    /// <summary>Resolves ownership paths and retains app execution aliases without invoking their launch targets.</summary>
    public static string ResolveReferencePath(string path)
    {
        using var handle = CreateFile(NativePath(path), 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (!handle.IsInvalid) return ResolvedPath(handle).TrimEnd('\\');
        var error = Marshal.GetLastWin32Error();
        if (error is 2 or 3) return Path.GetFullPath(path).TrimEnd('\\');
        if (error == 1920) // ERROR_CANT_ACCESS_FILE: app execution aliases cannot be followed by CreateFile.
        {
            using var alias = CreateFile(NativePath(path), 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (!alias.IsInvalid && GetFileInformationByHandleEx(alias, 9, out var tag, (uint)Marshal.SizeOf<FileAttributeTag>()) &&
                (tag.Attributes & (uint)FileAttributes.ReparsePoint) != 0 && tag.ReparseTag == 0x8000001B) // IO_REPARSE_TAG_APPEXECLINK
                return ResolvedPath(alias).TrimEnd('\\');
        }
        throw new Win32Exception(error, $"Installer ownership reference '{path}' could not be resolved (Windows error {error}).");
    }

    /// <summary>Verifies short paths with a small buffer and grows it for long native paths.</summary>
    private static string ResolvedPath(SafeFileHandle handle)
    {
        var finalPath = new StringBuilder(260);
        while (true)
        {
            uint length = GetFinalPathNameByHandle(handle, finalPath, (uint)finalPath.Capacity, 0);
            if (length == 0 || length >= 32768) throw new IOException("Unable to verify the opened cleanup path.");
            if (length < finalPath.Capacity) break;
            finalPath.EnsureCapacity((int)length + 1);
        }
        var resolved = finalPath.ToString();
        if (resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) resolved = @"\\" + resolved[8..];
        else if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) resolved = resolved[4..];
        return resolved;
    }

    /// <summary>Opens long paths independently of the launching executable's Win32 manifest.</summary>
    private static string NativePath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.Length < 260 || full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
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
        var handle = CreateFile(NativePath(path), 0xC0010100, 0, IntPtr.Zero, 1, 0x00200000, IntPtr.Zero);
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
    [StructLayout(LayoutKind.Sequential)] private struct FileAttributeTag { public uint Attributes, ReparseTag; }
    [DllImport("kernel32.dll", EntryPoint="CreateFileW", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint="GetFinalPathNameByHandleW", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
    [DllImport("kernel32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out FileAttributeTag info, uint size);
    [DllImport("kernel32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref FileDisposition info, uint size);
    [DllImport("kernel32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileTime(SafeFileHandle handle, IntPtr creation, ref System.Runtime.InteropServices.ComTypes.FILETIME access, IntPtr write);
}
