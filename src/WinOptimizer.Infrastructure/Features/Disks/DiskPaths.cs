using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace WinOptimizer.Infrastructure.Features.Disks;

/// <summary>Validates drive-letter paths and resolves actual Windows volume identities.</summary>
internal static class DiskPaths
{
    public static string LocalPath(string input, bool allowRoot = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        var path = input.Replace('/', '\\').TrimEnd('\\');
        if (input.Length < 3 || path.Length < 2 || input[2] is not ('\\' or '/') || !char.IsAsciiLetter(path[0]) ||
            path[1] != ':' || path.AsSpan(2).Contains(':'))
            throw new ArgumentException("Enter a full local path such as C:\\Users\\Name\\file.txt.");
        if (path.Length == 2)
            return allowRoot ? path + '\\' : throw new ArgumentException("Select a file or folder below the volume root.");

        foreach (var component in path[3..].Split('\\'))
            ValidateName(component);
        return Path.GetFullPath(path);
    }

    public static void ValidateName(string name)
    {
        if (name.Length == 0 || name.EndsWith('.') || name.EndsWith(' ') ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsDeviceName(name))
            throw new ArgumentException("The path contains an invalid or reserved Windows name.");
    }

    public static FileAttributes? CheckTree(string fullPath, string root, bool allowMissing = false)
    {
        var current = root;
        var attributes = ReadAttributes(root, allowMissing);
        foreach (var component in fullPath[root.Length..].TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (attributes is null) break;
            current = Path.Join(current, component);
            attributes = ReadAttributes(current, allowMissing);
        }
        return attributes;
    }

    public static FileAttributes? ReadAttributes(string path, bool allowMissing = false)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"The path contains a junction, symbolic link or another reparse point: {path}");
            return attributes;
        }
        catch (FileNotFoundException) when (allowMissing) { return null; }
        catch (DirectoryNotFoundException) when (allowMissing) { return null; }
    }

    public static DriveInfo Drive(string root, bool fixedOnly = false)
    {
        if (!IsDriveRoot(root)) throw new ArgumentException("Select a local volume.");
        var drive = new DriveInfo(root);
        if (!drive.IsReady || (fixedOnly ? drive.DriveType != DriveType.Fixed : !IsLocal(drive)))
            throw new IOException($"The selected volume is unavailable: {root}");
        return drive;
    }

    public static bool IsDriveRoot(string root) =>
        root.Length == 3 && char.IsAsciiLetter(root[0]) && root[1..] == ":\\";

    public static bool IsLocal(DriveInfo drive) => drive.DriveType is DriveType.Fixed or DriveType.Removable;

    public static string VolumeId(string root)
    {
        var volume = new StringBuilder(50);
        if (!GetVolumeNameForVolumeMountPoint(root, volume, (uint)volume.Capacity))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not resolve volume {root}.");
        return volume.ToString();
    }

    private static bool IsDeviceName(string component)
    {
        var name = component.Split('.', 2)[0].TrimEnd(' ').ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) &&
            ("123456789¹²³".Contains(name[3]));
    }

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder volume, uint length);
}
