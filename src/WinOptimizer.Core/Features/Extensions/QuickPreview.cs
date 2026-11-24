namespace WinOptimizer.Core.Features.Extensions;

public enum PreviewKind { Folder, Text, Image, Pdf, Audio, Video, Executable, Package, File, Link }

public sealed record PreviewEntry(string Name, bool IsFolder, long? Bytes);
public sealed record PreviewDetail(string Label, string Value);

/// <summary>Folder properties and recursive statistics, independent of the direct entry list.</summary>
public sealed record PreviewFolderDetails(DateTime Created, FileAttributes Attributes, int FileCount, int FolderCount,
    long? AllocatedBytes, bool IsListingTruncated);

public sealed record QuickPreview(string Path, string Name, PreviewKind Kind, DateTime Modified, long? Bytes,
    string? Text = null, IReadOnlyList<PreviewEntry>? Entries = null, IReadOnlyList<PreviewDetail>? Details = null, bool IsPartial = false)
{
    /// <summary>File to render when the selected item is a shortcut to another file.</summary>
    public string? ContentPath { get; init; }
    public bool? SourceIsFolder { get; init; }
    public string? ContentError { get; init; }
    public PreviewFolderDetails? FolderDetails { get; init; }
}
