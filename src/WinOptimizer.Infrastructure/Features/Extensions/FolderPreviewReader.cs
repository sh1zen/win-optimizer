using System.Runtime.InteropServices;
using WinOptimizer.Core.Features.Extensions;

namespace WinOptimizer.Infrastructure.Features.Extensions;

/// <summary>Measures a local folder tree without reading file contents or descending into links.</summary>
internal static class FolderPreviewReader
{
    private const int ScanLimit = 100_000;
    private const FileAttributes Unavailable = FileAttributes.Offline | FileAttributes.ReparsePoint;
    private static readonly Guid ShellClass = new("13709620-C279-11CE-A49E-444553540000");

    internal static QuickPreview Read(QuickPreview preview, DirectoryInfo root, int listingLimit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        object? shell = null;
        try
        {
            try { shell = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellClass, throwOnError: true)!); }
            catch (COMException) { /* The folder's logical totals are still available without Shell. */ }
            return Scan(preview, root, listingLimit, shell as IShellDispatch, ct);
        }
        finally { Release(shell); }
    }

    private static QuickPreview Scan(QuickPreview preview, DirectoryInfo root, int listingLimit, IShellDispatch? shell, CancellationToken ct)
    {
        var entries = new List<PreviewEntry>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);
        var created = root.CreationTime;
        var attributes = root.Attributes;
        int folders = 0, files = 0;
        long bytes = 0;
        long? allocatedBytes = 0;
        bool partial = false, listingTruncated = false;

        while (pending.TryPop(out var folder))
        {
            ct.ThrowIfCancellationRequested();
            bool isRoot = folder == root;
            IShellFolder? shellFolder = null;
            try
            {
                // A directory may have changed while it was waiting to be scanned.
                if ((File.GetAttributes(folder.FullName) & Unavailable) != 0)
                {
                    partial = true;
                    continue;
                }

                foreach (var item in folder.EnumerateFileSystemInfos())
                {
                    ct.ThrowIfCancellationRequested();
                    if (isRoot && entries.Count >= listingLimit) listingTruncated = true;
                    if (folders + files >= ScanLimit)
                    {
                        partial = true;
                        pending.Clear();
                        break;
                    }

                    var itemAttributes = item.Attributes;
                    bool available = (itemAttributes & Unavailable) == 0;
                    bool isFolder = item is DirectoryInfo;
                    long? size = null;
                    if (isFolder)
                    {
                        folders++;
                        if (available) pending.Push((DirectoryInfo)item);
                    }
                    else
                    {
                        files++;
                        if (available)
                        {
                            size = ((FileInfo)item).Length;
                            bytes += size.Value;
                            if (allocatedBytes.HasValue)
                                allocatedBytes += ReadAllocation(shell, folder.FullName, ref shellFolder, item.Name);
                        }
                    }
                    if (!available) partial = true;
                    if (isRoot && !listingTruncated) entries.Add(new(item.Name, isFolder, size));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (isRoot && entries.Count == 0) return preview with { IsPartial = true, ContentError = ex.Message };
                partial = true;
            }
            finally { Release(shellFolder); }
        }

        return preview with
        {
            Bytes = bytes,
            Entries = entries.OrderByDescending(item => item.IsFolder)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(),
            FolderDetails = new(created, attributes, files, folders, allocatedBytes, listingTruncated),
            IsPartial = partial
        };
    }

    /// <summary>Uses the same file allocation property as Explorer, including its handling of resident data.</summary>
    private static long? ReadAllocation(IShellDispatch? shell, string folderPath, ref IShellFolder? folder, string name)
    {
        object? entry = null;
        try
        {
            folder ??= shell?.NameSpace(folderPath);
            entry = folder?.ParseName(name);
            var value = (entry as IShellFolderItem)?.ExtendedProperty("System.FileAllocationSize");
            return value is ulong bytes && bytes <= long.MaxValue ? (long)bytes : null;
        }
        catch (COMException) { return null; }
        finally { Release(entry); }
    }

    private static void Release(object? value)
    {
        if (value is not null) Marshal.ReleaseComObject(value);
    }

    // Dispatch IDs and argument types come from the Windows SDK Shell type library.
    [ComImport, Guid("D8F015C0-C278-11CE-A49E-444553540000"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IShellDispatch
    {
        [DispId(0x60020002)] [return: MarshalAs(UnmanagedType.Interface)]
        IShellFolder? NameSpace([MarshalAs(UnmanagedType.Struct)] object path);
    }

    [ComImport, Guid("BBCBDE60-C3FF-11CE-8350-444553540000"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IShellFolder
    {
        [DispId(0x60020005)] [return: MarshalAs(UnmanagedType.IDispatch)]
        object? ParseName([MarshalAs(UnmanagedType.BStr)] string name);
    }

    [ComImport, Guid("EDC817AA-92B8-11D1-B075-00C04FC33AA5"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IShellFolderItem
    {
        [DispId(0x60030001)] [return: MarshalAs(UnmanagedType.Struct)]
        object? ExtendedProperty([MarshalAs(UnmanagedType.BStr)] string propertyName);
    }
}
