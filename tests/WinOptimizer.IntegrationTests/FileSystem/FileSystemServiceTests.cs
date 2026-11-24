using WinOptimizer.Infrastructure.Features.Common.Services;
using WinOptimizer.IntegrationTests.Fixtures;
using Xunit;

namespace WinOptimizer.IntegrationTests.FileSystem;

[Trait("Category", "Integration")]
public class FileSystemServiceTests : IClassFixture<TempDirectoryFixture>
{
    private readonly TempDirectoryFixture _fixture;
    private readonly FileSystemService _service;

    public FileSystemServiceTests(TempDirectoryFixture fixture)
    {
        _fixture = fixture;
        _service = new FileSystemService();
    }

    private string TempFile(string name) => Path.Combine(_fixture.TempPath, name);

    [Fact]
    public void DirectoryContentsClassifyImmediateFilesAndDirectoriesInEnumerationOrder()
    {
        var source = Directory.CreateDirectory(TempFile("directory-contents"));
        var child = source.CreateSubdirectory("boot");
        source.CreateSubdirectory("sources");
        File.WriteAllText(Path.Combine(child.FullName, "nested"), "not an immediate entry");
        File.WriteAllText(Path.Combine(source.FullName, "setup.exe"), "fixture");
        File.WriteAllText(Path.Combine(source.FullName, "autorun.inf"), "fixture");

        var contents = _service.ReadDirectoryContents(source.FullName);

        Assert.Equal(Directory.GetDirectories(source.FullName), contents.Directories);
        Assert.Equal(Directory.GetFiles(source.FullName), contents.Files);
    }

    [Fact]
    public void DirectoryFileSizeIncludesNestedAndHiddenFiles()
    {
        var source = Directory.CreateDirectory(TempFile("directory-size"));
        var child = source.CreateSubdirectory("sources");
        File.WriteAllBytes(Path.Combine(source.FullName, "setup.exe"), [1, 2, 3]);
        var hidden = Path.Combine(child.FullName, "media");
        File.WriteAllBytes(hidden, [4, 5, 6, 7]);
        File.SetAttributes(hidden, FileAttributes.Hidden);

        Assert.Equal(7L, _service.GetDirectoryFileSize(source.FullName));
    }

    [Fact]
    public void ReplaceFilePublishesTheNewBytesAndConsumesTheTemporaryFile()
    {
        var source = TempFile("replacement-source.iso");
        var destination = TempFile("replacement-destination.iso");
        File.WriteAllText(source, "New ISO");
        File.WriteAllText(destination, "Previous ISO");
        _service.ReplaceFile(source, destination);
        Assert.Equal("New ISO", File.ReadAllText(destination));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public void FailedReplacementPreservesBothFilesWhenTheDestinationIsLocked()
    {
        var source = TempFile("locked-source.iso");
        var destination = TempFile("locked-destination.iso");
        File.WriteAllText(source, "New ISO");
        File.WriteAllText(destination, "Previous ISO");
        using (var locked = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.ThrowsAny<IOException>(() => _service.ReplaceFile(source, destination));
        Assert.Equal("Previous ISO", File.ReadAllText(destination));
        Assert.Equal("New ISO", File.ReadAllText(source));
    }
}
