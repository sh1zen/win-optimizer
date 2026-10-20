using Moq;
using Windows.Graphics.Imaging;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

/// <summary>Checks registered icon parsing and native extraction using synthetic local ICO data.</summary>
public sealed class DesktopAppIconSourceTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\Tool\\app.exe\",-7", @"C:\Program Files\Tool\app.exe", -7)]
    [InlineData(@"C:\Program Files\Tool\app.dll,2", @"C:\Program Files\Tool\app.dll", 2)]
    [InlineData(@"C:\Tool, Inc\app.ico", @"C:\Tool, Inc\app.ico", 0)]
    [InlineData(@"%LOCALAPPDATA%\Tool\app.exe,0", @"C:\Interactive\Local\Tool\app.exe", 0)]
    public void ParsesLocalResourceLocationsAndInteractiveUserVariables(string raw, string path, int index)
    {
        Assert.Equal((path, index), DesktopAppIconSource.ParseLocation(raw, _ => @"C:\Interactive\Local"));
    }

    [Theory]
    [InlineData(@"\\server\share\icon.ico")]
    [InlineData("https://example.invalid/app.ico")]
    [InlineData("relative.exe,0")]
    [InlineData("\"C:\\Tool\\app.exe\" /run")]
    [InlineData("\"C:\\Tool\\app.exe\",bad")]
    [InlineData(@"C:\Tool\app.exe:stream")]
    [InlineData(@"%UNKNOWN_VARIABLE%\app.exe")]
    public void RejectsRemoteRelativeAndInvalidLocations(string raw)
    {
        Assert.Null(DesktopAppIconSource.ParseLocation(raw, _ => @"C:\Interactive"));
    }

    [Fact]
    public async Task ExtractsARealTransparentPngFromAnIsolatedIconFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "desktop-icon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "fixture.ico");
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)1);
                writer.Write((byte)16); writer.Write((byte)16); writer.Write((ushort)0);
                writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(1128); writer.Write(22);
                writer.Write(40); writer.Write(16); writer.Write(32); writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write(0); writer.Write(1024); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
                for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++)
                {
                    bool visible = x is >= 4 and < 12 && y is >= 4 and < 12;
                    writer.Write(visible ? new byte[] { 20, 100, 220, 255 } : new byte[4]);
                }
                for (int y = 0; y < 16; y++) writer.Write(new byte[] { 0, 0, 0, 0 });
            }
            var source = new DesktopAppIconSource(Mock.Of<IInteractiveUserService>());
            var reference = Assert.IsType<DesktopIconReference>(source.Resolve(path));
            await using var icon = await source.GetIconStreamAsync(reference, default);
            Assert.NotNull(icon);
            using var random = icon.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(random);
            Assert.Equal(96u, decoder.PixelWidth); Assert.Equal(96u, decoder.PixelHeight);
            var pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
                new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
            Assert.Equal(0, pixels[3]);
            int center = (48 * 96 + 48) * 4;
            Assert.True(pixels[center + 3] > 240); Assert.True(pixels[center + 2] > pixels[center]);
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(directory, recursive: true);
        }
    }
}
