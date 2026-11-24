using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using WinOptimizer.Core.Features.Extensions;
using WinOptimizer.Infrastructure.Features.Common.Utilities;

namespace WinOptimizer.Infrastructure.Features.Extensions;

/// <summary>Reads bounded metadata and content from files selected in Explorer without executing them.</summary>
public sealed class FilePreviewLoader
{
    private const int EntryLimit = 400;
    private const int TextLimit = 96 * 1024;
    private const int LinkDepthLimit = 8;
    private static readonly HashSet<string> TextExtensions = new(StringComparer.Ordinal)
    { "txt", "md", "markdown", "log", "csv", "tsv", "json", "jsonl", "xml", "yaml", "yml", "ini", "toml", "html", "htm", "css", "js", "cs", "py", "ps1", "bat", "cmd" };

    public Task<QuickPreview> ReadAsync(string path, CancellationToken ct) => Task.Run(() => Read(path, ct), ct);

    private static QuickPreview Read(string path, CancellationToken ct, int linkDepth = 0)
    {
        ct.ThrowIfCancellationRequested();
        path = NormalizePath(path);
        var format = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        var preview = new QuickPreview(path, name.Length == 0 ? path : name,
            format is "lnk" or "url" or "website" ? PreviewKind.Link : PreviewKind.File, default, null);
        try
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal) ||
                new DriveInfo(Path.GetPathRoot(path)!).DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram) ||
                (linkDepth != 0 && HasLinkedParent(path)))
                return preview with { IsPartial = true };

            var attributes = File.GetAttributes(path);
            bool isFolder = (attributes & FileAttributes.Directory) != 0;
            FileSystemInfo entry = isFolder ? new DirectoryInfo(path) : new FileInfo(path);
            preview = preview with
            {
                Kind = isFolder ? PreviewKind.Folder : preview.Kind,
                Modified = entry.LastWriteTime,
                Bytes = entry is FileInfo file ? file.Length : null
            };
            if ((attributes & FileAttributes.Offline) != 0) return preview with { IsPartial = true };
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                var target = entry.ResolveLinkTarget(returnFinalTarget: false);
                return target is null ? preview with { IsPartial = true } : ReadLink(preview, target.FullName, linkDepth, ct);
            }
            if (entry is DirectoryInfo folder) return FolderPreviewReader.Read(preview, folder, EntryLimit, ct);
            if (format == "lnk")
            {
                if (preview.Bytes > 1024 * 1024) throw new InvalidDataException("The shortcut exceeds 1 MiB.");
                return ReadLink(preview, WindowsShellLink.ReadRawTarget(path), linkDepth, ct);
            }
            if (format is "url" or "website") return ReadInternetShortcut(preview, ct);
            return ReadFile(preview, format, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException
            or COMException or BadImageFormatException or XmlException)
        {
            return preview with { IsPartial = true, ContentError = ex.Message };
        }
    }

    private static QuickPreview ReadFile(QuickPreview preview, string format, CancellationToken ct)
    {
        var kind = format switch
        {
            "png" or "jpg" or "jpeg" or "bmp" or "gif" or "tif" or "tiff" or "webp" or "ico" => PreviewKind.Image,
            "pdf" => PreviewKind.Pdf,
            "mp3" or "m4a" or "wav" or "flac" or "wma" or "aac" or "ogg" => PreviewKind.Audio,
            "mp4" or "mov" or "mkv" or "webm" or "avi" or "wmv" => PreviewKind.Video,
            "exe" or "dll" => PreviewKind.Executable,
            "apk" or "zip" => PreviewKind.Package,
            "docx" or "odt" or "pptx" => PreviewKind.Text,
            _ when TextExtensions.Contains(format) => PreviewKind.Text,
            _ => PreviewKind.File
        };
        ct.ThrowIfCancellationRequested();
        preview = preview with { Kind = kind };
        return kind switch
        {
            PreviewKind.Text => ReadText(preview, format, ct),
            PreviewKind.Executable => preview with { Details = ReadProgram(preview.Path) },
            PreviewKind.Package => ReadPackage(preview, ct),
            _ => preview
        };
    }

    /// <summary>Follows a stored local target without searching for moved shortcuts or opening web addresses.</summary>
    private static QuickPreview ReadLink(QuickPreview source, string target, int depth, CancellationToken ct)
    {
        var link = source with { Kind = PreviewKind.Link, SourceIsFolder = source.Kind == PreviewKind.Folder };
        if (string.IsNullOrWhiteSpace(target)) return link with { IsPartial = true };
        target = Environment.ExpandEnvironmentVariables(target);
        link = link with { Details = [new("Target", target)] };
        if (depth >= LinkDepthLimit) return link with { IsPartial = true };

        try
        {
            if (!Path.IsPathRooted(target) && !target.Contains(':'))
                target = Path.GetFullPath(target, Path.GetDirectoryName(source.Path)!);
            var content = Read(target, ct, depth + 1);
            if (content.ContentError is not null || (content.IsPartial && (content.Modified == default || content.Kind == PreviewKind.Link)))
                return link with { IsPartial = true, ContentError = content.ContentError };

            return content with
            {
                Path = source.Path,
                Name = source.Name,
                SourceIsFolder = link.SourceIsFolder,
                ContentPath = content.ContentPath ?? content.Path,
                Details = [.. link.Details, .. (content.Details ?? []).Where(detail => detail.Label != "Target")]
            };
        }
        catch (ArgumentException ex) { return link with { IsPartial = true, ContentError = ex.Message }; }
    }

    private static QuickPreview ReadInternetShortcut(QuickPreview preview, CancellationToken ct)
    {
        if (preview.Bytes > 64 * 1024) throw new InvalidDataException("The internet shortcut exceeds 64 KiB.");
        using var lines = new StringReader(ReadText(preview, "url", ct).Text!);
        bool internetShortcut = false;
        while (lines.ReadLine() is { } value)
        {
            var line = value.AsSpan().Trim();
            if (line.StartsWith("["))
            {
                internetShortcut = line.Equals("[InternetShortcut]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!internetShortcut) continue;

            var equals = line.IndexOf('=');
            if (equals <= 0 || !line[..equals].Trim().Equals("URL", StringComparison.OrdinalIgnoreCase)) continue;
            var target = line[(equals + 1)..].Trim();
            if (!target.IsEmpty) return preview with { Details = [new("Target", target.ToString())] };
        }
        return preview with { IsPartial = true };
    }

    private static string NormalizePath(string path)
    {
        path = path.Replace('/', '\\');
        bool fullyQualified = Path.IsPathFullyQualified(path);
        bool networkPath = fullyQualified && path.StartsWith(@"\\", StringComparison.Ordinal) &&
            !path.StartsWith(@"\\.\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal) && !path.Contains(':');
        if (!networkPath && (!fullyQualified || path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path.AsSpan(2).Contains(':')))
            throw new ArgumentException("A fully qualified local drive path is required.");
        return Path.GetFullPath(path);
    }

    // Inspect ancestors from the drive root before a shortcut can traverse a redirected directory.
    private static bool HasLinkedParent(string path)
    {
        var parents = new Stack<string>();
        for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
            parents.Push(parent);
        while (parents.TryPop(out var parent))
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }

    private static QuickPreview ReadText(QuickPreview preview, string format, CancellationToken ct)
    {
        if (format is "docx" or "odt" or "pptx")
        {
            if (preview.Bytes > 128L * 1024 * 1024) throw new InvalidDataException("The document exceeds 128 MiB.");
            var excerpt = OfficePreviewText.Read(preview.Path, format, TextLimit, ct);
            return preview with { Text = excerpt.Text, IsPartial = excerpt.IsPartial };
        }
        using var stream = File.Open(preview.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        Span<char> buffer = stackalloc char[4096];
        var text = new StringBuilder((int)Math.Min(preview.Bytes ?? 0, buffer.Length));
        var remaining = TextLimit + 1;
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            int read = reader.Read(buffer[..Math.Min(buffer.Length, remaining)]);
            if (read == 0) break;
            if (buffer[..read].Contains('\0')) throw new InvalidDataException("The file does not contain plain text.");
            text.Append(buffer[..Math.Min(read, remaining - 1)]);
            remaining -= read;
        }
        return preview with { Text = text.ToString(), IsPartial = remaining == 0 };
    }

    private static IReadOnlyList<PreviewDetail> ReadProgram(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        if (pe.PEHeaders.PEHeader is null) throw new BadImageFormatException("Invalid executable header.");
        var version = FileVersionInfo.GetVersionInfo(path);
        var details = new List<PreviewDetail>();
        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) details.Add(new(name, value));
        }
        Add("Product", version.ProductName);
        Add("Publisher", version.CompanyName);
        Add("FileVersion", version.FileVersion);
        Add("ProductVersion", version.ProductVersion);
        Add("Architecture", pe.PEHeaders.CoffHeader.Machine.ToString());
        return details;
    }

    private static QuickPreview ReadPackage(QuickPreview preview, CancellationToken ct)
    {
        if (preview.Bytes > 128L * 1024 * 1024) throw new InvalidDataException("The archive exceeds 128 MiB.");
        using var archive = ZipFile.OpenRead(preview.Path);
        var entries = new List<PreviewEntry>();
        foreach (var item in archive.Entries.Take(EntryLimit))
        {
            ct.ThrowIfCancellationRequested();
            bool folder = string.IsNullOrEmpty(item.Name);
            entries.Add(new(item.FullName, folder, folder ? null : item.Length));
        }
        return preview with { Entries = entries, IsPartial = archive.Entries.Count > EntryLimit };
    }
}
