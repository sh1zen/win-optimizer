using System.IO.Compression;
using System.Runtime.InteropServices;
using WinOptimizer.Core.Features.Extensions;
using WinOptimizer.Infrastructure.Features.Extensions;
using Xunit;

namespace WinOptimizer.IntegrationTests.Extensions;

/// <summary>Verifies the data presented by Preview without requiring an interactive desktop.</summary>
public sealed class FilePreviewTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("win-optimizer-preview-");
    private readonly FilePreviewLoader _loader = new();

    [Fact]
    public async Task FolderTotalsIncludeDescendantsAndKeepTheListingAtOneLevel()
    {
        var directory = _root.CreateSubdirectory("Cartella 日本語");
        var nested = directory.CreateSubdirectory("Folder");
        var deep = nested.CreateSubdirectory("Deep");
        var data = new byte[16 * 1024];
        new Random(17).NextBytes(data);
        await File.WriteAllBytesAsync(Path.Combine(deep.FullName, "nested.bin"), data);
        await File.WriteAllBytesAsync(Path.Combine(directory.FullName, "Résumé.txt"), [1, 2, 3]);
        directory.Attributes |= FileAttributes.Hidden;
        directory.CreationTime = new DateTime(2020, 2, 5, 12, 34, 56);
        directory.LastWriteTime = new DateTime(2021, 2, 5, 12, 34, 56);

        var preview = await _loader.ReadAsync(directory.FullName, default);

        Assert.Equal(PreviewKind.Folder, preview.Kind);
        Assert.Equal("Cartella 日本語", preview.Name);
        Assert.Equal(data.Length + 3, preview.Bytes);
        Assert.Equal(directory.LastWriteTime, preview.Modified);
        var details = Assert.IsType<PreviewFolderDetails>(preview.FolderDetails);
        Assert.Equal(directory.CreationTime, details.Created);
        Assert.True(details.Attributes.HasFlag(FileAttributes.Hidden));
        Assert.Equal(2, details.FileCount);
        Assert.Equal(2, details.FolderCount);
        Assert.True(details.AllocatedBytes >= data.Length);
        Assert.False(details.IsListingTruncated);
        Assert.Equal([new PreviewEntry("Folder", true, null), new PreviewEntry("Résumé.txt", false, 3)],
            preview.Entries);
        Assert.False(preview.IsPartial);
    }

    [Fact]
    public async Task FolderTotalsIncludeFilesBeyondTheListingLimit()
    {
        for (int i = 0; i < 405; i++)
            await File.WriteAllBytesAsync(Path.Combine(_root.FullName, $"file-{i:D3}.bin"), [1]);

        var preview = await _loader.ReadAsync(_root.FullName, default);

        var details = Assert.IsType<PreviewFolderDetails>(preview.FolderDetails);
        Assert.Equal(405, details.FileCount);
        Assert.Equal(0, details.FolderCount);
        Assert.Equal(405, preview.Bytes);
        Assert.Equal(400, preview.Entries!.Count);
        Assert.True(details.IsListingTruncated);
        Assert.False(preview.IsPartial);
    }

    [Fact]
    public async Task UnicodeTextPreviewIsBoundedAndReportsItsExcerpt()
    {
        var sample = Path.Combine(_root.FullName, "Unicode_日本語.txt");
        var text = "Città 日本語\n" + new string('x', 100_000);
        await File.WriteAllTextAsync(sample, text);

        var preview = await _loader.ReadAsync(sample, default);

        Assert.Equal(PreviewKind.Text, preview.Kind);
        Assert.StartsWith("Città 日本語\n", preview.Text);
        Assert.Equal(96 * 1024, preview.Text!.Length);
        Assert.True(preview.IsPartial);
        Assert.Equal(text, await File.ReadAllTextAsync(sample));
    }

    [Theory]
    [InlineData("MARKDOWN")]
    [InlineData("HTM")]
    public async Task PreviewReadsAlternateTextExtensions(string extension)
    {
        var sample = Path.Combine(_root.FullName, $"example.{extension}");
        const string source = "Città 日本語";
        await File.WriteAllTextAsync(sample, source);

        var preview = await _loader.ReadAsync(sample, default);

        Assert.Equal(PreviewKind.Text, preview.Kind);
        Assert.Equal(source, preview.Text);
        Assert.False(preview.IsPartial);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4097)]
    [InlineData(96 * 1024)]
    public async Task TextPreviewReadsTheWholeFileAtOrBelowTheExcerptLimit(int length)
    {
        var path = Path.Combine(_root.FullName, "preview.txt");
        var text = new string('à', length);
        await File.WriteAllTextAsync(path, text);

        var preview = await _loader.ReadAsync(path, default);

        Assert.Equal(text, preview.Text);
        Assert.False(preview.IsPartial);
    }

    [Fact]
    public async Task OfficePreviewReadsRealParagraphContent()
    {
        var sample = Path.Combine(_root.FullName, "sample.docx");
        using (var archive = ZipFile.Open(sample, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("word/document.xml").Open()))
            writer.Write("""
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                  <w:body><w:p><w:r><w:t>Résumé &amp; 日本語</w:t></w:r></w:p></w:body>
                </w:document>
                """);

        var preview = await _loader.ReadAsync(sample, default);

        Assert.Equal(PreviewKind.Text, preview.Kind);
        Assert.Equal("Résumé & 日本語" + Environment.NewLine, preview.Text);
        Assert.False(preview.IsPartial);
    }

    [Fact]
    public async Task ArchiveListsEntriesWithoutExtractingThem()
    {
        var sample = Path.Combine(_root.FullName, "sample.apk");
        using (var archive = ZipFile.Open(sample, ZipArchiveMode.Create))
        using (var entry = archive.CreateEntry("res/demo.bin").Open())
            entry.Write(new byte[11]);

        var preview = await _loader.ReadAsync(sample, default);

        Assert.Equal(PreviewKind.Package, preview.Kind);
        Assert.Equal(new PreviewEntry("res/demo.bin", false, 11), Assert.Single(preview.Entries!));
        Assert.Single(_root.GetFileSystemInfos());
    }

    [Fact]
    public async Task ExecutablePreviewUsesPortableExecutableMetadata()
    {
        var preview = await _loader.ReadAsync(typeof(FilePreviewLoader).Assembly.Location, default);

        Assert.Equal(PreviewKind.Executable, preview.Kind);
        Assert.Contains(preview.Details!, detail => detail.Label == "Architecture" && detail.Value.Length > 0);
    }

    [Fact]
    public async Task ShellShortcutPreviewsItsTargetAndKeepsTheOriginalSelection()
    {
        var target = Path.Combine(_root.FullName, "Résumé.txt");
        await File.WriteAllTextAsync(target, "Shortcut content 日本語");
        var shortcut = Path.Combine(_root.FullName, "Desktop shortcut.lnk");
        CreateShortcut(shortcut, target);

        var preview = await _loader.ReadAsync(shortcut, default);

        Assert.Equal(shortcut, preview.Path);
        Assert.Equal("Desktop shortcut.lnk", preview.Name);
        Assert.Equal(target, preview.ContentPath, ignoreCase: true);
        Assert.Equal(PreviewKind.Text, preview.Kind);
        Assert.Equal("Shortcut content 日本語", preview.Text);
        var targetDetail = Assert.Single(preview.Details!);
        Assert.Equal("Target", targetDetail.Label);
        Assert.Equal(target, targetDetail.Value, ignoreCase: true);
        Assert.False(preview.IsPartial);
    }

    [Fact]
    public async Task FolderShortcutShowsEntriesAndIdentifiesTheOriginalAsAFile()
    {
        var folder = _root.CreateSubdirectory("Documents");
        await File.WriteAllTextAsync(Path.Combine(folder.FullName, "item.txt"), "one file");
        var shortcut = Path.Combine(_root.FullName, "Folder.lnk");
        CreateShortcut(shortcut, folder.FullName);

        var preview = await _loader.ReadAsync(shortcut, default);

        Assert.Equal(PreviewKind.Folder, preview.Kind);
        Assert.Equal(shortcut, preview.Path);
        Assert.False(preview.SourceIsFolder);
        Assert.Equal(folder.FullName, preview.ContentPath, ignoreCase: true);
        Assert.Equal(1, preview.FolderDetails?.FileCount);
        Assert.Equal("item.txt", Assert.Single(preview.Entries!).Name);
    }

    [Fact]
    public async Task BrokenShortcutStillShowsItsStoredTarget()
    {
        var missing = Path.Combine(_root.FullName, "Missing.txt");
        var shortcut = Path.Combine(_root.FullName, "Broken.lnk");
        CreateShortcut(shortcut, missing);

        var preview = await _loader.ReadAsync(shortcut, default);

        Assert.Equal(PreviewKind.Link, preview.Kind);
        Assert.Null(preview.ContentPath);
        Assert.Equal(new PreviewDetail("Target", missing), Assert.Single(preview.Details!));
        Assert.True(preview.IsPartial);
    }

    [Theory]
    [InlineData("url")]
    [InlineData("website")]
    public async Task InternetShortcutDisplaysTheAddressFromItsOwnSection(string extension)
    {
        const string address = "https://preview.invalid/path?a=b";
        var shortcut = Path.Combine(_root.FullName, $"Website.{extension}");
        await File.WriteAllTextAsync(shortcut, $"[Other]\r\nURL=ignored\r\n[InternetShortcut]\r\nURL={address}\r\n");

        var preview = await _loader.ReadAsync(shortcut, default);

        Assert.Equal(PreviewKind.Link, preview.Kind);
        Assert.Equal(new PreviewDetail("Target", address), Assert.Single(preview.Details!));
        Assert.Null(preview.ContentPath);
        Assert.False(preview.IsPartial);
    }

    [Theory]
    [InlineData("sample.unrecognized", false)]
    [InlineData("broken.zip", true)]
    public async Task FilesWithoutReadableContentStillShowMetadata(string name, bool partial)
    {
        var path = Path.Combine(_root.FullName, name);
        await File.WriteAllBytesAsync(path, [1, 2, 3]);

        var preview = await _loader.ReadAsync(path, default);

        Assert.Equal(PreviewKind.File, preview.Kind);
        Assert.Equal(name, preview.Name);
        Assert.Equal(3, preview.Bytes);
        Assert.Equal(partial, preview.IsPartial);
        Assert.Equal(partial, preview.ContentError is not null);
    }

    [Fact]
    public async Task NetworkSelectionShowsItsNameWithoutReadingAnUnavailableShare()
    {
        const string path = @"\\preview.invalid\share\sample.txt";
        var preview = await _loader.ReadAsync(path, default);

        Assert.Equal(path, preview.Path);
        Assert.Equal("sample.txt", preview.Name);
        Assert.Null(preview.Bytes);
        Assert.True(preview.IsPartial);
    }

    [Fact]
    public async Task RejectsDeviceAndAlternateStreamPathsBeforeReading()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _loader.ReadAsync(@"\\.\C:\preview.txt", default));
        await Assert.ThrowsAsync<ArgumentException>(() => _loader.ReadAsync(Path.Combine(_root.FullName, "sample.txt:other"), default));
    }

    [Fact]
    public void PreviewSettingsKeepSignInAndLiveShortcutIndependent()
    {
        bool startup = false;
        bool shortcut = true;
        PreviewSettingsSnapshot Apply(PreviewExtensionSettings.Request request, bool running = true) =>
            PreviewExtensionSettings.Apply(request, () => startup, value => startup = value,
                running ? () => shortcut : null, running ? value => shortcut = value : null,
                () => PreviewExtensionSettings.DefaultOpacity, _ => throw new InvalidOperationException("Unexpected opacity write."));

        Assert.Equal(new PreviewSettingsSnapshot(false, false), Apply(new(), running: false));
        Assert.Equal(new PreviewSettingsSnapshot(false, true), Apply(new(StartsWithWindows: true), running: false));
        Assert.Equal(new PreviewSettingsSnapshot(true, true), Apply(new()));
        Assert.Equal(new PreviewSettingsSnapshot(false, true), Apply(new(ShortcutEnabled: false)));
        Assert.Equal(new PreviewSettingsSnapshot(true, true), Apply(new(ShortcutEnabled: true)));
        Assert.Equal(new PreviewSettingsSnapshot(false, true), Apply(new(Stop: true)));
        Assert.Equal(new PreviewSettingsSnapshot(true, true), Apply(new(ShortcutEnabled: true)));
        Assert.Equal(new PreviewSettingsSnapshot(true, false), Apply(new(StartsWithWindows: false)));
        Assert.Throws<InvalidOperationException>(() => Apply(new(ShortcutEnabled: true), running: false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public void PreviewOpacityPersistsWithoutAResidentAndPublishesConfirmedChanges(int opacity)
    {
        int saved = PreviewExtensionSettings.DefaultOpacity;
        int? applied = null;
        PreviewSettingsSnapshot Apply(PreviewExtensionSettings.Request request, bool resident = false) =>
            PreviewExtensionSettings.Apply(request, () => true, _ => throw new InvalidOperationException("Unexpected sign-in write."),
                resident ? () => true : null, null, () => saved, value => saved = value,
                resident ? value => applied = value : null);

        Assert.Equal(new PreviewSettingsSnapshot(false, true, 8), Apply(new()));
        Assert.Equal(new PreviewSettingsSnapshot(false, true, opacity), Apply(new(BackgroundOpacity: opacity)));
        Assert.Equal(new PreviewSettingsSnapshot(true, true, opacity), Apply(new(), resident: true));
        Assert.Null(applied);
        Assert.Equal(new PreviewSettingsSnapshot(true, true, 6), Apply(new(BackgroundOpacity: 6), resident: true));
        Assert.Equal(6, applied);
        Assert.Throws<ArgumentOutOfRangeException>(() => Apply(new(BackgroundOpacity: 11), resident: true));
        Assert.Equal(6, saved);
    }

    [Fact]
    public void RedirectedSettingsWorkerRecognizesTheConnectionWithOrWithoutStartLanguage()
    {
        var connection = Guid.NewGuid();
        const string executable = "\"C:\\Portable App\\win-optimizer-preview.exe\"";
        Assert.True(DesktopExtensionSettingsChannel.TryParseRedirectedWorker($"{executable} --settings-worker {connection:N}", out var first));
        Assert.Equal(connection, first);
        Assert.True(DesktopExtensionSettingsChannel.TryParseRedirectedWorker($"{executable} --settings-worker {connection:N} \"it\"", out var second));
        Assert.Equal(connection, second);
        Assert.False(DesktopExtensionSettingsChannel.TryParseRedirectedWorker($"{executable} --open it", out _));
        Assert.False(DesktopExtensionSettingsChannel.TryParseRedirectedWorker($"{executable} --settings-worker invalid", out _));
    }

    [Theory]
    [InlineData("Progman", "SysListView32", true)]
    [InlineData("WorkerW", "SysListView32", true)]
    [InlineData("CabinetWClass", "DirectUIHWND", false)]
    [InlineData("ExploreWClass", "SysListView32", false)]
    public void CapturesTheFocusedDesktopOrExplorerFileView(string windowClass, string focusClass, bool desktop)
    {
        nint Parent(nint handle) => handle switch { 4 => 3, 3 => desktop ? 1 : 2, 2 => 1, _ => 0 };
        string Class(nint handle) => handle switch
        {
            1 => windowClass,
            2 => "ShellTabWindowClass",
            3 => "SHELLDLL_DefView",
            4 => focusClass,
            _ => ""
        };

        var view = ExplorerPreviewSelection.FindFileView(1, 4, Parent, Class);
        Assert.Equal(new ExplorerPreviewSelection.FocusedView(1, 3, desktop ? 0 : 2, desktop), view);
    }

    [Theory]
    [InlineData("CabinetWClass", "Edit", "SHELLDLL_DefView")]
    [InlineData("CabinetWClass", "DirectUIHWND", "NamespaceTreeControl")]
    [InlineData("WorkerW", "SysListView32", "OtherView")]
    [InlineData("FileManager", "SysListView32", "SHELLDLL_DefView")]
    public void IgnoresFocusOutsideTheDesktopOrExplorerFileView(string windowClass, string focusClass, string parentClass)
    {
        nint Parent(nint handle) => handle - 1;
        string Class(nint handle) => handle switch { 1 => windowClass, 2 => parentClass, 3 => focusClass, _ => "" };

        Assert.Null(ExplorerPreviewSelection.FindFileView(1, 3, Parent, Class));
    }

    private static void CreateShortcut(string path, string target)
    {
        var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!)!;
        object? shortcut = null;
        try
        {
            shortcut = ((dynamic)shell).CreateShortcut(path);
            ((dynamic)shortcut!).TargetPath = target;
            ((dynamic)shortcut).Save();
        }
        finally
        {
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
        }
    }

    public void Dispose() => _root.Delete(recursive: true);
}
