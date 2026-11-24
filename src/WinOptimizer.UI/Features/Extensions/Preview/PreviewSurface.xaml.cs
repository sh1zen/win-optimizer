using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Extensions;
using WinOptimizer.Infrastructure.Features.Extensions;
using Windows.Data.Pdf;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using Windows.System;

namespace WinOptimizer.UI.Features.Extensions.Preview;

/// <summary>Presents a local, read-only snapshot and owns PDF, image and playback resources for the window.</summary>
public sealed partial class PreviewSurface : UserControl
{
    private const double ImageHeight = 450;
    internal const int InitialHeight = 238;
    private const double FolderHeight = 348;
    private const double ExpandedHeight = 550;
    private const double ExpandedContentHeight = 202;
    private readonly ILocalizationService _localization;
    private readonly IProcessExecutor _processExecutor;
    private readonly Action _close;
    private readonly Action<PreviewSurface> _resize;
    private CancellationTokenSource? _lifetime;
    private QuickPreview? _item;
    private PdfDocument? _pdf;
    private MediaPlayer? _mediaPlayer;
    private MediaSource? _mediaSource;
    private uint _pageIndex;
    private bool _rendering;

    internal PreviewSurface(ILocalizationService localization, IProcessExecutor processExecutor, Action close, Action<PreviewSurface> resize)
    {
        _localization = localization;
        _processExecutor = processExecutor;
        _close = close;
        _resize = resize;
        InitializeComponent();
        FlowDirection = localization.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        OpenLabel.Text = L("Open");
        LabelAction(OpenButton, "Open");
        LabelAction(CloseButton, "Close");
        LabelAction(Reveal, "Reveal");
        LabelAction(FolderContents, "FolderContents");
        LabelAction(PreviousPage, "Previous");
        LabelAction(NextPage, "Next");
        SizeLabel.Text = L("Size");
        DiskLabel.Text = L("SizeOnDisk");
        FilesLabel.Text = L("Files") + "  ";
        SubfoldersLabel.Text = L("Subfolders") + "  ";
        ModifiedLabel.Text = L("Modified");
        CreatedLabel.Text = L("Created");
        PathLabel.Text = L("FolderPath");
        ImageView.MaxHeight = ImageHeight;
        Loaded += (_, _) => FocusClose();
    }

    internal FrameworkElement DragRegion => DragArea;
    internal void FocusClose() => CloseButton.Focus(FocusState.Programmatic);
    internal void FinishLoading() => LoadingRing.IsActive = false;
    internal void RevealBackdrop() => PreviewRoot.Background = null;

    /// <summary>Measures the whole summary at the available client width and reserves space for expanded content.</summary>
    internal double MeasureClientHeight(double width)
    {
        SummaryLayout.Measure(new Size(width, double.PositiveInfinity));
        double headerHeight = ToolbarRow.Height.Value + SummaryLayout.DesiredSize.Height;
        if (BodyScroll.Visibility == Visibility.Visible)
            return Math.Max(ExpandedHeight, headerHeight + ExpandedContentHeight);
        return Math.Max(_item?.Kind == PreviewKind.Folder ? FolderHeight : InitialHeight, headerHeight);
    }

    /// <summary>Begins a fresh content lifetime without rebuilding the window controls.</summary>
    internal CancellationToken Begin(string name)
    {
        Clear();
        _lifetime = new();
        NameLabel.Text = name;
        ToolTipService.SetToolTip(NameLabel, name);
        LoadingRing.IsActive = true;
        return _lifetime.Token;
    }

    internal async Task PresentAsync(QuickPreview item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _item = item;
        NameLabel.Text = item.Name;
        ToolTipService.SetToolTip(NameLabel, item.Name);
        var contentPath = item.ContentPath ?? item.Path;
        OpenButton.IsEnabled = true;
        Reveal.IsEnabled = true;
        FileIcon.Icon = item.Kind switch
        {
            PreviewKind.Folder => Icon.Folder,
            PreviewKind.Image => Icon.Image,
            PreviewKind.Audio or PreviewKind.Video => Icon.PlayCircle,
            PreviewKind.Executable => Icon.AppsList,
            PreviewKind.Link => Icon.Link,
            _ => Icon.Document
        };
        bool folder = item.Kind == PreviewKind.Folder;
        PresentSummary(item, contentPath);
        if (folder && item.Entries is not null)
            FolderContents.Visibility = Visibility.Visible;
        else if (item.Entries is { } entries)
            ShowEntries(entries);
        if (item.Text is { } text)
            SetLine(Excerpt, text.Length > 0 ? text : L("Empty"));
        ShowContent(Excerpt.Visibility == Visibility.Visible);

        try
        {
            await ShowAppIconAsync(item, ct);
            if ((item.Kind == PreviewKind.Pdf && item.Bytes > 128L * 1024 * 1024) ||
                (item.Kind == PreviewKind.Image && item.Bytes > 96L * 1024 * 1024))
                return;
            if (item.Kind is PreviewKind.Image or PreviewKind.Pdf or PreviewKind.Audio or PreviewKind.Video)
            {
                var file = await StorageFile.GetFileFromPathAsync(contentPath).AsTask(ct);
                switch (item.Kind)
                {
                    case PreviewKind.Image:
                        await ShowImageAsync(file, ct);
                        break;
                    case PreviewKind.Pdf:
                        var document = await PdfDocument.LoadFromFileAsync(file).AsTask(ct);
                        ct.ThrowIfCancellationRequested();
                        if (document.PageCount == 0 || document.IsPasswordProtected) return;
                        _pdf = document;
                        await RenderPageAsync(0, ct);
                        ct.ThrowIfCancellationRequested();
                        PageControls.Visibility = Visibility.Visible;
                        break;
                    default:
                        await ShowMediaAsync(file, item.Kind == PreviewKind.Audio, ct);
                        break;
                }
            }
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                if (ImageView.Visibility == Visibility.Visible || MediaHost.Visibility == Visibility.Visible) ShowContent(true);
                FinishLoading();
            }
        }
    }

    /// <summary>Separates size and content counts from timestamps, location and other properties.</summary>
    private void PresentSummary(QuickPreview item, string contentPath)
    {
        var culture = CultureInfo.CurrentUICulture;
        var folder = item.FolderDetails;
        SetLine(ItemType, item.Kind == PreviewKind.Folder ? L("Folder") : Path.GetExtension(contentPath).TrimStart('.').ToUpperInvariant());
        Statistics.Visibility = item.Bytes.HasValue || folder is not null ? Visibility.Visible : Visibility.Collapsed;
        SetSizeMetric(SizeMetric, SizeValue, SizeExact, item.Bytes, culture);
        SetSizeMetric(DiskMetric, DiskValue, DiskExact, folder?.AllocatedBytes, culture, showUnavailable: folder is not null);
        SetCountMetric(FilesMetric, FilesValue, folder?.FileCount, culture);
        SetCountMetric(SubfoldersMetric, SubfoldersValue, folder?.FolderCount, culture);
        DetailsDivider.Visibility = Statistics.Visibility;
        SetProperty(ModifiedLabel, Modified, item.Modified == default ? "" : item.Modified.ToString("g", culture));
        SetProperty(CreatedLabel, Created, folder is null ? "" : folder.Created.ToString("g", culture));
        SetProperty(PathLabel, FolderPath, folder is null ? "" : contentPath);

        var notes = new List<string>();
        if (folder is not null)
        {
            if (item.IsPartial) notes.Add(L("FolderPartial"));
            if (folder.Attributes.HasFlag(System.IO.FileAttributes.Hidden)) notes.Add(L("Hidden"));
            if (folder.Attributes.HasFlag(System.IO.FileAttributes.ReadOnly)) notes.Add(L("ReadOnly"));
        }
        if (item.Details is { } details)
            notes.AddRange(details.Select(detail => $"{L(detail.Label)}  ·  {detail.Value}"));
        SetLine(Metadata, string.Join(Environment.NewLine, notes));
        UpdateDetailsVisibility();
    }

    private void SetSizeMetric(FrameworkElement metric, TextBlock value, TextBlock exact, long? bytes,
        CultureInfo culture, bool showUnavailable = false)
    {
        metric.Visibility = bytes.HasValue || showUnavailable ? Visibility.Visible : Visibility.Collapsed;
        string byteCount = bytes is { } count ? L("ByteCount", count.ToString("N0", culture)) : "";
        value.Text = bytes switch
        {
            >= 1024 => FormatBytes(bytes.Value, culture),
            not null => byteCount,
            _ => showUnavailable ? L("SizeUnavailable") : ""
        };
        SetLine(exact, bytes >= 1024 ? byteCount : "");
    }

    private static void SetCountMetric(TextBlock metric, Microsoft.UI.Xaml.Documents.Run value, int? count, CultureInfo culture)
    {
        metric.Visibility = count.HasValue ? Visibility.Visible : Visibility.Collapsed;
        value.Text = count?.ToString("N0", culture) ?? "";
    }

    private static void SetProperty(TextBlock label, TextBlock value, string text)
    {
        SetLine(value, text);
        label.Visibility = value.Visibility;
    }

    private void UpdateDetailsVisibility()
    {
        bool hasDetails = Modified.Visibility == Visibility.Visible || Created.Visibility == Visibility.Visible ||
            FolderPath.Visibility == Visibility.Visible || Metadata.Visibility == Visibility.Visible ||
            MediaDetails.Visibility == Visibility.Visible;
        DetailsGroup.Visibility = hasDetails ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Displays association artwork independently of the selected content renderer.</summary>
    private async Task ShowAppIconAsync(QuickPreview item, CancellationToken ct)
    {
        using var icon = await WindowsPreviewIcon.ReadAsync(item, ct);
        ct.ThrowIfCancellationRequested();
        if (icon is null) return;
        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(icon);
        ct.ThrowIfCancellationRequested();
        AppIcon.Source = source;
        AppIcon.Visibility = Visibility.Visible;
        FileIcon.Visibility = Visibility.Collapsed;
    }

    private async Task ShowImageAsync(StorageFile file, CancellationToken ct)
    {
        using var stream = await file.OpenReadAsync().AsTask(ct);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct);
        ct.ThrowIfCancellationRequested();
        if ((ulong)decoder.PixelWidth * decoder.PixelHeight > 80_000_000) return;
        SetLine(MediaDetails, $"{decoder.PixelWidth} × {decoder.PixelHeight} px");
        UpdateDetailsVisibility();
        var scale = Math.Min(1, 1400.0 / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale))
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(ct);
        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(bitmap).AsTask(ct);
        ct.ThrowIfCancellationRequested();
        ImageView.Source = source;
        ImageView.Visibility = Visibility.Visible;
    }

    private async Task ShowMediaAsync(StorageFile file, bool audio, CancellationToken ct)
    {
        if (audio)
        {
            var tags = await file.Properties.GetMusicPropertiesAsync().AsTask(ct);
            ct.ThrowIfCancellationRequested();
            SetLine(MediaDetails, string.Join(Environment.NewLine, new[] { tags.Title, tags.Artist, tags.Album,
                tags.Duration > TimeSpan.Zero ? tags.Duration.ToString(@"hh\:mm\:ss") : "" }
                .Where(value => !string.IsNullOrWhiteSpace(value))));
            UpdateDetailsVisibility();
        }
        ct.ThrowIfCancellationRequested();
        var player = new MediaPlayerElement { AreTransportControlsEnabled = true, AutoPlay = false, MaxHeight = 330 };
        if (audio) player.Height = 72;
        _mediaSource = MediaSource.CreateFromStorageFile(file);
        _mediaPlayer = new MediaPlayer { AutoPlay = false, Source = _mediaSource };
        player.SetMediaPlayer(_mediaPlayer);
        MediaHost.Content = player;
        MediaHost.Visibility = Visibility.Visible;
        if (!audio) return;
        var image = await GetAlbumArtAsync(file, ct);
        if (image is null) return;
        ImageView.MaxHeight = 190;
        ImageView.Source = image;
        ImageView.Visibility = Visibility.Visible;
    }

    private static async Task<BitmapImage?> GetAlbumArtAsync(StorageFile file, CancellationToken ct)
    {
        const uint pixels = 320;
        using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.MusicView, pixels, ThumbnailOptions.UseCurrentScale).AsTask(ct);
        if (thumbnail is not { Size: > 0, Type: ThumbnailType.Image }) return null;
        var image = new BitmapImage { DecodePixelWidth = (int)pixels };
        await image.SetSourceAsync(thumbnail).AsTask(ct);
        ct.ThrowIfCancellationRequested();
        return image;
    }

    private async Task RenderPageAsync(uint index, CancellationToken ct)
    {
        if (_pdf is not { } document || _rendering || ct.IsCancellationRequested) return;
        _rendering = true;
        PreviousPage.IsEnabled = NextPage.IsEnabled = false;
        try
        {
            using var page = document.GetPage(index);
            using var output = new InMemoryRandomAccessStream();
            var width = page.Size.Width;
            var height = page.Size.Height;
            var scale = Math.Min(1.5, 1350.0 / Math.Max(width, height));
            if (!double.IsFinite(scale) || scale <= 0) throw new InvalidDataException(L("Unavailable"));
            await page.RenderToStreamAsync(output, new PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, width * scale),
                DestinationHeight = (uint)Math.Max(1, height * scale)
            }).AsTask(ct);
            output.Seek(0);
            var image = new BitmapImage();
            await image.SetSourceAsync(output).AsTask(ct);
            ct.ThrowIfCancellationRequested();
            ImageView.Source = image;
            ImageView.Visibility = Visibility.Visible;
            _pageIndex = index;
            PageNumber.Text = L("Page", index + 1, document.PageCount);
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                _rendering = false;
                PreviousPage.IsEnabled = _pageIndex > 0;
                NextPage.IsEnabled = _pageIndex + 1 < document.PageCount;
            }
        }
    }

    private async void PreviousPageClicked(object sender, RoutedEventArgs args)
    {
        if (_pageIndex > 0) await RunActionAsync(ct => RenderPageAsync(_pageIndex - 1, ct));
    }
    private async void NextPageClicked(object sender, RoutedEventArgs args)
    {
        if (_pdf is { } document && _pageIndex + 1 < document.PageCount)
            await RunActionAsync(ct => RenderPageAsync(_pageIndex + 1, ct));
    }

    /// <summary>Shows the folder's existing entry list only when requested from the toolbar.</summary>
    private void FolderContentsToggled(object sender, RoutedEventArgs args)
    {
        if (_item is not { Kind: PreviewKind.Folder, Entries: { } entries } folder) return;
        bool expanded = FolderContents.IsChecked == true;
        if (expanded && Excerpt.Text.Length == 0) ShowEntries(entries, folder.FolderDetails?.IsListingTruncated == true);
        ShowContent(expanded);
    }

    private void ShowEntries(IReadOnlyList<PreviewEntry> entries, bool truncated = false)
    {
        var lines = entries.Select(entry => entry.IsFolder
            ? $"{entry.Name}/" : entry.Bytes is { } size ? $"{entry.Name}  ·  {FormatBytes(size)}" : entry.Name);
        if (truncated) lines = lines.Prepend(L("FolderListLimited", entries.Count));
        SetLine(Excerpt, entries.Count == 0 ? L("Empty") : string.Join(Environment.NewLine, lines));
    }

    private void ShowContent(bool expanded)
    {
        var visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (BodyScroll.Visibility != visibility)
        {
            var fill = new GridLength(1, GridUnitType.Star);
            SummaryRow.Height = expanded ? GridLength.Auto : fill;
            ContentRow.Height = expanded ? fill : GridLength.Auto;
            BodyScroll.Visibility = visibility;
        }
        if (_item is not null) _resize(this);
    }

    /// <summary>Opens the selected path with its Windows association, including shortcuts.</summary>
    private async void OpenClicked(object sender, RoutedEventArgs args) => await RunActionAsync(async ct =>
    {
        if (_item is not { } item) return;
        // Shell launch may reuse an existing window without returning a process handle.
        await _processExecutor.ShellExecuteAsync(item.Path, ct: ct);
        if (!ct.IsCancellationRequested) _close();
    });

    private async void RevealClicked(object sender, RoutedEventArgs args) => await RunActionAsync(async ct =>
    {
        if (_item is not { } item) return;
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(item.Path) ?? item.Path).AsTask(ct);
        var options = new FolderLauncherOptions();
        if (Path.GetDirectoryName(item.Path) is not null)
        {
            IStorageItem selected = (item.SourceIsFolder ?? (item.Kind == PreviewKind.Folder))
                ? await StorageFolder.GetFolderFromPathAsync(item.Path).AsTask(ct)
                : await StorageFile.GetFileFromPathAsync(item.Path).AsTask(ct);
            options.ItemsToSelect.Add(selected);
        }
        ct.ThrowIfCancellationRequested();
        if (!await Launcher.LaunchFolderAsync(folder, options) && !ct.IsCancellationRequested) ShowActionError(L("Unavailable"));
    });

    private async Task RunActionAsync(Func<CancellationToken, Task> action)
    {
        if (_lifetime is not { } lifetime) return;
        var ct = lifetime.Token;
        try { await action(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or COMException or ArgumentException or InvalidDataException or UnauthorizedAccessException or Win32Exception)
        { if (!ct.IsCancellationRequested) ShowActionError(ex.Message); }
    }

    private void ShowActionError(string message)
    {
        if (_lifetime is null) return;
        Message.Message = message;
        Message.Visibility = Visibility.Visible;
        Message.IsOpen = true;
        _resize(this);
    }

    /// <summary>Releases the old file data and resets the controls before another preview can use them.</summary>
    internal void Clear()
    {
        if (_lifetime is not { } lifetime) return;
        _lifetime = null;
        lifetime.Cancel();
        lifetime.Dispose();
        if (_mediaPlayer is { } mediaPlayer)
        {
            _mediaPlayer = null;
            if (MediaHost.Content is MediaPlayerElement player) player.SetMediaPlayer(null);
            mediaPlayer.Dispose();
        }
        _mediaSource?.Dispose();
        _mediaSource = null;
        MediaHost.Content = null;
        MediaHost.Visibility = Visibility.Collapsed;
        _item = null;
        _pdf = null;
        _pageIndex = 0;
        _rendering = false;
        PageControls.Visibility = Visibility.Collapsed;
        LoadingRing.IsActive = false;
        Message.IsOpen = false;
        Message.Visibility = Visibility.Collapsed;
        Message.Message = "";
        OpenButton.IsEnabled = false;
        Reveal.IsEnabled = false;
        FolderContents.IsChecked = false;
        FolderContents.Visibility = Visibility.Collapsed;
        ShowContent(false);
        NameLabel.Text = PageNumber.Text = "";
        SetLine(ItemType, "");
        Statistics.Visibility = DetailsGroup.Visibility = Visibility.Collapsed;
        SetLine(MediaDetails, "");
        SetLine(Excerpt, "");
        ToolTipService.SetToolTip(NameLabel, null);
        FileIcon.Icon = Icon.Document;
        FileIcon.Visibility = Visibility.Visible;
        AppIcon.Visibility = Visibility.Collapsed;
        AppIcon.Source = null;
        ImageView.Source = null;
        ImageView.MaxHeight = ImageHeight;
        ImageView.Visibility = Visibility.Collapsed;
        BodyScroll.ChangeView(0, 0, null, disableAnimation: true);
    }

    private void CloseClicked(object sender, RoutedEventArgs args) => _close();

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Escape) return;
        args.Handled = true;
        _close();
    }

    private string L(string key, params object[] args) => _localization.GetString("QuickPreview_" + key, args);

    private void LabelAction(Control action, string key)
    {
        var label = L(key);
        AutomationProperties.SetName(action, label);
        ToolTipService.SetToolTip(action, label);
    }

    private static void SetLine(TextBlock line, string text)
    {
        line.Text = text;
        line.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string FormatBytes(long bytes, CultureInfo? culture = null) => bytes switch
    {
        >= 1024L * 1024 * 1024 => (bytes / (1024.0 * 1024 * 1024)).ToString("N1", culture) + " GiB",
        >= 1024 * 1024 => (bytes / (1024.0 * 1024)).ToString("N1", culture) + " MiB",
        >= 1024 => (bytes / 1024.0).ToString("N1", culture) + " KiB",
        _ => bytes.ToString("N0", culture) + " B"
    };
}
