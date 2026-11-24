using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace WinOptimizer.UI.Features.Extensions.Notch;

/// <summary>Offers the selected local file to the share targets installed for this Windows user.</summary>
internal sealed class NotchFileShare(nint window) : IDisposable
{
    private DataTransferManager? _manager;
    private StorageFile? _file;
    private bool _disposed;

    internal async Task ShowAsync(string path)
    {
        if (!NotchContent.IsLocalPath(path)) throw new ArgumentException("Select a file on a local drive.", nameof(path));
        var file = await StorageFile.GetFileFromPathAsync(path);
        if (_disposed) return;
        if (_manager is null)
        {
            _manager = DataTransferManagerInterop.GetForWindow(window);
            _manager.DataRequested += OnDataRequested;
        }
        _file = file;
        try { DataTransferManagerInterop.ShowShareUIForWindow(window); }
        catch { _file = null; throw; }
    }

    private void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
    {
        var file = _file;
        _file = null;
        if (file is null) return;
        args.Request.Data.Properties.Title = file.Name;
        args.Request.Data.SetStorageItems(new IStorageItem[] { file }, readOnly: true);
    }

    public void Dispose()
    {
        _disposed = true;
        if (_manager is not null) _manager.DataRequested -= OnDataRequested;
        _manager = null;
        _file = null;
    }
}
