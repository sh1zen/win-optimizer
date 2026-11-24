using System.Globalization;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Infrastructure.Features.Common.Utilities;

namespace WinOptimizer.Infrastructure.Features.SoftwareApps.Services;

/// <summary>Identifies one registered icon resource and the file version that supplies its artwork.</summary>
public sealed record DesktopIconReference(string Path, int Index, long Length, DateTime LastWriteUtc)
{
    public string CacheKey => FormattableString.Invariant($"desktop-v1|{Path.ToUpperInvariant()}|{Index}|{Length}|{LastWriteUtc.Ticks}");
}

/// <summary>Separates local icon metadata and native resource extraction from cache and app matching policy.</summary>
public interface IDesktopAppIconSource
{
    DesktopIconReference? Resolve(string displayIcon);
    Task<Stream?> GetIconStreamAsync(DesktopIconReference icon, CancellationToken ct);
}

/// <summary>Reads registered EXE/DLL/ICO resources without executing the application or loading remote artwork.</summary>
public sealed class DesktopAppIconSource(IInteractiveUserService user) : IDesktopAppIconSource
{
    private const int IconSize = 96;

    public DesktopIconReference? Resolve(string displayIcon)
    {
        var location = ParseLocation(displayIcon, folder => user.IsOtsElevation
            ? user.GetInteractiveUserFolderPath(folder) : Environment.GetFolderPath(folder));
        if (location == null) return null;
        Cleaner.CleanerPaths.ValidateDescendant(location.Value.Path[..3], location.Value.Path);
        var file = new FileInfo(location.Value.Path);
        return file.Exists ? new(file.FullName, location.Value.Index, file.Length, file.LastWriteTimeUtc) : null;
    }

    /// <summary>Preserves signed resource indices and quoted paths, expanding user variables for the interactive account.</summary>
    internal static (string Path, int Index)? ParseLocation(string value, Func<Environment.SpecialFolder, string> folder)
    {
        var text = value.Trim();
        if (text.Length == 0) return null;
        string path, suffix;
        if (text[0] == '"')
        {
            int close = text.IndexOf('"', 1);
            if (close < 0) return null;
            path = text[1..close]; suffix = text[(close + 1)..].Trim();
        }
        else
        {
            int comma = text.LastIndexOf(',');
            bool indexed = comma >= 0 && int.TryParse(text[(comma + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
            path = indexed ? text[..comma].Trim() : text;
            suffix = indexed ? text[comma..] : "";
        }
        int index = 0;
        if (suffix.Length > 0 && (suffix[0] != ',' || !int.TryParse(suffix.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out index))) return null;
        path = AppPaths.Expand(path, folder);
        if (!Cleaner.CleanerPaths.IsLocalPath(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.Contains('%') || path[3..].Contains(':') || path.IndexOfAny(['*', '?', '"', '\0', '\r', '\n']) >= 0 ||
            !new[] { ".exe", ".dll", ".ico", ".icl", ".cpl", ".ocx" }.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) return null;
        return (System.IO.Path.GetFullPath(path), index);
    }

    public async Task<Stream?> GetIconStreamAsync(DesktopIconReference icon, CancellationToken ct)
    {
        var pixels = await Task.Run(() => WindowsIconPixels.ReadResource(icon.Path, icon.Index, IconSize, ct), ct).ConfigureAwait(false);
        if (pixels == null) return null;
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask(ct).ConfigureAwait(false);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, IconSize, IconSize, 96, 96, pixels);
        await encoder.FlushAsync().AsTask(ct).ConfigureAwait(false);
        using var encoded = stream.AsStreamForRead();
        var result = new MemoryStream();
        try
        {
            await encoded.CopyToAsync(result, ct).ConfigureAwait(false);
            result.Position = 0;
            return result;
        }
        catch { result.Dispose(); throw; }
    }
}
