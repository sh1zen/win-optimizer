using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WinOptimizer.Infrastructure.Features.Common.Utilities;

/// <summary>Extracts Windows icon resources and renders them with premultiplied alpha.</summary>
internal static class WindowsIconPixels
{
    public static byte[]? ReadResource(string path, int index, int size, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int status = SHDefExtractIcon(path, index, 0, out var icon, 0, (uint)size);
        try
        {
            Marshal.ThrowExceptionForHR(status);
            return icon == 0 ? null : Render(icon, size, ct);
        }
        finally { Release(icon); }
    }

    /// <summary>Renders on black and white to recover alpha for both modern and legacy masked icons.</summary>
    public static byte[] Render(nint icon, int size, CancellationToken ct)
    {
        var dc = CreateCompatibleDC(0);
        if (dc == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        nint bitmap = 0, previous = 0;
        try
        {
            var header = new BitmapHeader { Size = (uint)Marshal.SizeOf<BitmapHeader>(), Width = size, Height = -size, Planes = 1, Bits = 32 };
            bitmap = CreateDIBSection(dc, ref header, 0, out var bits, 0, 0);
            if (bitmap == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            previous = SelectObject(dc, bitmap);
            if (previous == 0 || previous == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
            var black = new byte[size * size * 4];
            var white = new byte[black.Length];
            Array.Fill(white, (byte)255);
            Draw(black);
            Draw(white);
            for (int pixel = 0; pixel < black.Length; pixel += 4)
                black[pixel + 3] = (byte)Math.Clamp(255 - white[pixel] + black[pixel], 0, 255);
            return black;

            void Draw(byte[] background)
            {
                ct.ThrowIfCancellationRequested();
                Marshal.Copy(background, 0, bits, background.Length);
                if (!DrawIconEx(dc, 0, 0, icon, size, size, 0, 0, 3)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!GdiFlush()) throw new Win32Exception(Marshal.GetLastWin32Error());
                Marshal.Copy(bits, background, 0, background.Length);
            }
        }
        finally
        {
            if (previous != 0 && previous != -1) SelectObject(dc, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            DeleteDC(dc);
        }
    }

    public static void Release(nint icon)
    {
        if (icon != 0) DestroyIcon(icon);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, Bits;
        public uint Compression, ImageSize; public int XPixels, YPixels; public uint Colors, ImportantColors;
    }

    [DllImport("shell32.dll", EntryPoint = "SHDefExtractIconW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHDefExtractIcon(string file, int index, uint flags, out nint large, nint small, uint size);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateDIBSection(nint dc, ref BitmapHeader info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GdiFlush();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawIconEx(nint dc, int x, int y, nint icon, int width, int height, uint step, nint brush, uint flags);
}
