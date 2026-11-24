using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Graphics.Imaging;
using WinOptimizer.Core.Features.Extensions;
using WinOptimizer.Infrastructure.Features.Common.Utilities;

namespace WinOptimizer.Infrastructure.Features.Extensions;

/// <summary>Reads artwork for the current user's default Windows handler without opening the selected item.</summary>
public static class WindowsPreviewIcon
{
    private const int IconSize = 128;
    private const uint IconAndIndex = 0x4100;
    private const uint UseFileAttributes = 0x10;
    private const uint UseItemId = 0x8;
    private const uint ProtocolAssociation = 0x1000;
    private static readonly Guid ImageListId = new("46EB5926-582E-4017-9FDF-E8998DAA0950");
    private static readonly uint FileInfoSize = (uint)Marshal.SizeOf<ShellFileInfo>();

    public static Task<SoftwareBitmap?> ReadAsync(QuickPreview item, CancellationToken ct) =>
        Task.Run(() => ReadBitmap(() => ReadPixels(item, ct), ct), ct);

    internal static Task<SoftwareBitmap?> ReadShellItemAsync(string parsingName, CancellationToken ct) =>
        Task.Run(() => ReadBitmap(() => ReadShellItemIcon(parsingName, ct), ct), ct);

    private static SoftwareBitmap? ReadBitmap(Func<byte[]?> readPixels, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int apartment = CoInitializeEx(0, 0); // Multithreaded apartment for Shell lookups.
        try
        {
            var pixels = readPixels();
            ct.ThrowIfCancellationRequested();
            return pixels is null ? null : SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(),
                BitmapPixelFormat.Bgra8, IconSize, IconSize, BitmapAlphaMode.Premultiplied);
        }
        catch (Exception ex) when (ex is COMException or Win32Exception) { return null; }
        finally { if (apartment >= 0) CoUninitialize(); }
    }

    private static byte[]? ReadPixels(QuickPreview item, CancellationToken ct)
    {
        var path = item.ContentPath ?? item.Path;
        bool folder = item.Kind == PreviewKind.Folder || (item.ContentPath is null && item.SourceIsFolder == true);
        string extension = folder ? "" : Path.GetExtension(path).ToLowerInvariant();
        string association = folder ? "Folder" : extension;
        uint flags = 0;
        if (extension is ".url" or ".website" &&
            Uri.TryCreate(item.Details?.FirstOrDefault(detail => detail.Label == "Target")?.Value, UriKind.Absolute, out var target) &&
            target.Scheme is "http" or "https")
        {
            association = target.Scheme;
            flags = ProtocolAssociation;
        }

        if (association.Length != 0)
        {
            var appId = Query(association, flags, AssociationValue.AppId);
            // Packaged apps expose their registered artwork through the Apps folder.
            if (appId is not null && appId.Contains('!') && appId.IndexOfAny(['\\', '/', ':']) < 0 &&
                ReadShellItemIcon(WindowsDesktopApplications.Folder + "\\" + appId, ct) is { } packaged)
                return packaged;
            if (ReadLocalResource(Query(association, flags, AssociationValue.Executable), ct) is { } application)
                return application;
        }
        if (item.Kind == PreviewKind.Executable && !item.IsPartial && ReadLocalResource(path, ct) is { } executable)
            return executable;

        ct.ThrowIfCancellationRequested();
        // Ask Shell about the type only. This also works for remote, offline and unreadable items.
        GetFileInfo("preview" + extension, folder ? 0x10u : 0x80u, out var info, FileInfoSize,
            IconAndIndex | UseFileAttributes);
        return RenderShellIcon(info, ct);
    }

    private static string? Query(string association, uint flags, AssociationValue value)
    {
        var output = new StringBuilder(512);
        uint length = (uint)output.Capacity;
        int status = AssocQueryString(flags, value, association, null, output, ref length);
        if (status == 1 && length > output.Capacity && length <= 32768)
        {
            output.EnsureCapacity((int)length);
            status = AssocQueryString(flags, value, association, null, output, ref length);
        }
        return status == 0 ? output.ToString() : null;
    }

    private static byte[]? ReadLocalResource(string? path, CancellationToken ct)
    {
        if (path is null || path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' ||
            !Path.IsPathFullyQualified(path) || path.AsSpan(2).Contains(':') ||
            Path.GetExtension(path).ToLowerInvariant() is not (".exe" or ".dll"))
            return null;
        try
        {
            if (new DriveInfo(Path.GetPathRoot(path)!).DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram))
                return null;
            return WindowsIconPixels.ReadResource(path, 0, IconSize, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or Win32Exception)
        { return null; }
    }

    private static byte[]? ReadShellItemIcon(string parsingName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int status = SHParseDisplayName(parsingName, 0, out var itemId, 0, out _);
        try
        {
            if (status < 0 || itemId == 0) return null;
            GetFileInfoById(itemId, 0, out var info, FileInfoSize, IconAndIndex | UseItemId);
            return RenderShellIcon(info, ct);
        }
        finally { Marshal.FreeCoTaskMem(itemId); }
    }

    /// <summary>Prefers the large Shell image and releases both owned icon handles.</summary>
    private static byte[]? RenderShellIcon(ShellFileInfo info, CancellationToken ct)
    {
        if (info.Icon == 0) return null;
        nint large = 0;
        try
        {
            const int jumbo = 4;
            const uint transparent = 1;
            if (SHGetImageList(jumbo, in ImageListId, out var images) >= 0 && images != 0)
            {
                try { large = ImageList_GetIcon(images, info.Index, transparent); }
                finally { Marshal.Release(images); }
            }
            return WindowsIconPixels.Render(large != 0 ? large : info.Icon, IconSize, ct);
        }
        finally
        {
            WindowsIconPixels.Release(large);
            WindowsIconPixels.Release(info.Icon);
        }
    }

    private enum AssociationValue : uint { Executable = 2, AppId = 21 }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public nint Icon;
        public int Index;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shlwapi.dll", EntryPoint = "AssocQueryStringW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int AssocQueryString(uint flags, AssociationValue value, string association, string? extra, StringBuilder output, ref uint length);
    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint GetFileInfo(string path, uint attributes, out ShellFileInfo info, uint size, uint flags);
    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", ExactSpelling = true)]
    private static extern nint GetFileInfoById(nint itemId, uint attributes, out ShellFileInfo info, uint size, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHParseDisplayName(string name, nint bindContext, out nint itemId, uint mask, out uint attributes);
    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetImageList(int type, in Guid iid, out nint imageList);
    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern nint ImageList_GetIcon(nint imageList, int index, uint flags);
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();
}
