using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Customize.Interfaces;

namespace WinOptimizer.UI.Features.Customize.Services;

/// <summary>Loads local shell artwork with bounded I/O and creates dispatcher-bound images on the UI thread.</summary>
public sealed class ProgramIconSource(IDispatcherService dispatcher, ILogService log) : IProgramIconSource
{
    private readonly SemaphoreSlim _gate = new(4, 4);

    public async Task<ImageSource?> LoadAsync(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 64, ThumbnailOptions.UseCurrentScale);
            if (thumbnail == null) return null;
            ImageSource? source = null;
            await dispatcher.RunOnUIThreadWithContextAsync(async () =>
            {
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(thumbnail);
                source = bitmap;
            }).ConfigureAwait(false);
            return source;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or ArgumentException or COMException)
        {
            log.LogDebug($"Context-menu program icon unavailable for '{path}': {ex.Message}");
            return null;
        }
        finally { _gate.Release(); }
    }
}
