using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinOptimizer.Core.Features.Common.Interfaces;

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
        var pixels = await Task.Run(() => ReadPixels(icon, ct), ct).ConfigureAwait(false);
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

    /// <summary>Renders on black and white to recover alpha for both modern and legacy masked icon resources.</summary>
    private static byte[]? ReadPixels(DesktopIconReference icon, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int status = SHDefExtractIcon(icon.Path, icon.Index, 0, out var handle, IntPtr.Zero, IconSize);
        try
        {
            Marshal.ThrowExceptionForHR(status);
            if (handle == IntPtr.Zero) return null;
            var dc = CreateCompatibleDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr bitmap = IntPtr.Zero, previous = IntPtr.Zero;
            try
            {
                var header = new BitmapHeader { Size = (uint)Marshal.SizeOf<BitmapHeader>(), Width = IconSize, Height = -IconSize, Planes = 1, Bits = 32 };
                bitmap = CreateDIBSection(dc, ref header, 0, out var bits, IntPtr.Zero, 0);
                if (bitmap == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                previous = SelectObject(dc, bitmap);
                if (previous == IntPtr.Zero || previous == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var black = new byte[IconSize * IconSize * 4];
                var white = new byte[black.Length]; Array.Fill(white, (byte)255);
                Render(black); Render(white);
                for (int pixel = 0; pixel < black.Length; pixel += 4)
                    black[pixel + 3] = (byte)Math.Clamp(255 - white[pixel] + black[pixel], 0, 255);
                return black;

                void Render(byte[] background)
                {
                    ct.ThrowIfCancellationRequested();
                    Marshal.Copy(background, 0, bits, background.Length);
                    if (!DrawIconEx(dc, 0, 0, handle, IconSize, IconSize, 0, IntPtr.Zero, 3)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (!GdiFlush()) throw new Win32Exception(Marshal.GetLastWin32Error());
                    Marshal.Copy(bits, background, 0, background.Length);
                }
            }
            finally
            {
                if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(dc, previous);
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                DeleteDC(dc);
            }
        }
        finally { if (handle != IntPtr.Zero) DestroyIcon(handle); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, Bits;
        public uint Compression, ImageSize; public int XPixels, YPixels; public uint Colors, ImportantColors;
    }
    [DllImport("shell32.dll", EntryPoint = "SHDefExtractIconW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHDefExtractIcon(string file, int index, uint flags, out IntPtr large, IntPtr small, uint size);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapHeader info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GdiFlush();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, uint step, IntPtr brush, uint flags);
}
