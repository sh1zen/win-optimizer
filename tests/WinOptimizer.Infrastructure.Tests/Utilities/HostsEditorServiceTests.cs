using System.Text;
using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.Infrastructure.Features.Utilities;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Utilities;

public sealed class HostsEditorServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "win-optimizer-hosts-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IApplicationModeService> _mode = new();
    private string HostsPath => Path.Combine(_folder, "hosts");
    private string Backups => Path.Combine(_folder, "backups");
    public HostsEditorServiceTests()
    {
        Directory.CreateDirectory(_folder);
        _mode.Setup(service => service.BeginSystemOperation()).Returns(Mock.Of<IDisposable>());
    }
    private HostsEditorService Create() => new(_mode.Object, HostsPath, Backups);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavesAtomicallyWithExactBackupAndPreservedEncoding(bool unicode)
    {
        const string original = "# café\r\n127.0.0.1 localhost\r\n";
        Encoding encoding = unicode ? new UnicodeEncoding(false, true, true) : new UTF8Encoding(true, true);
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(original)).ToArray();
        await File.WriteAllBytesAsync(HostsPath, bytes);
        var service = Create();
        Assert.False(Directory.Exists(Backups));
        var document = await service.ReadAsync();
        Assert.Equal(original, document.Content);
        var updated = original + "::1 localhost\r\n0.0.0.0 blocked.test\r\n";
        var result = await service.SaveAsync(document, updated);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(result.BackupPath));
        Assert.Equal(encoding.GetPreamble().Concat(encoding.GetBytes(updated)), await File.ReadAllBytesAsync(HostsPath));
        Assert.Equal(updated, result.Document.Content);
        Assert.NotEqual(document.Revision, result.Document.Revision);
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
        var restored = await service.SaveAsync(result.Document, original);
        Assert.Equal(original, restored.Document.Content);
        Assert.Equal(2, Directory.GetFiles(Backups).Length);
    }

    [Fact]
    public async Task RefusesStaleSnapshotAndKeepsExternalChanges()
    {
        await File.WriteAllTextAsync(HostsPath, "127.0.0.1 localhost\n");
        var service = Create();
        var snapshot = await service.ReadAsync();
        const string external = "127.0.0.1 localhost\n127.0.0.2 external.test\n";
        await File.WriteAllTextAsync(HostsPath, external);
        await Assert.ThrowsAsync<UtilityOperationException>(() => service.SaveAsync(snapshot, "0.0.0.0 edit.test\n"));
        Assert.Equal(external, await File.ReadAllTextAsync(HostsPath));
        Assert.False(Directory.Exists(Backups));
    }

    [Fact]
    public async Task RejectsInvalidContentAndForeignDocumentPathsWithoutWriting()
    {
        await File.WriteAllTextAsync(HostsPath, "127.0.0.1 localhost\n");
        var service = Create();
        var snapshot = await service.ReadAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(snapshot, "not-an-ip host"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(snapshot with { FilePath = Path.Combine(_folder, "other") }, "127.0.0.1 localhost"));
        Assert.False(Directory.Exists(Backups));
    }

    [Theory]
    [InlineData(WinOptimizerMode.Builder)]
    [InlineData(WinOptimizerMode.ConfigReview)]
    public async Task PreventsWritesOutsideNormalMode(WinOptimizerMode mode)
    {
        await File.WriteAllTextAsync(HostsPath, "127.0.0.1 localhost\n");
        var service = Create(); var snapshot = await service.ReadAsync();
        _mode.SetupGet(service => service.CurrentMode).Returns(mode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(snapshot, "127.0.0.2 edited.test\n"));
        Assert.Equal(snapshot.Content, await File.ReadAllTextAsync(HostsPath));
    }

    public void Dispose() { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }
}
