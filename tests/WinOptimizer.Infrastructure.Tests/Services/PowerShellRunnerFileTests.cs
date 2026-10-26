using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Infrastructure.Features.Common.Utilities;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

/// <summary>Parses fixture files through the production PowerShell validation path.</summary>
public sealed class PowerShellRunnerFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WinOptimizer-validation-" + Guid.NewGuid().ToString("N"));
    private readonly PowerShellRunner _runner;

    public PowerShellRunnerFileTests()
    {
        Directory.CreateDirectory(_root);
        var files = new Mock<IFileSystemService>();
        files.Setup(fs => fs.GetTempPath()).Returns(_root);
        files.Setup(fs => fs.CombinePath(It.IsAny<string[]>())).Returns((string[] parts) => Path.Combine(parts));
        files.Setup(fs => fs.FileExists(It.IsAny<string>())).Returns((string path) => File.Exists(path));
        files.Setup(fs => fs.WriteAllTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string path, string text, CancellationToken ct) => File.WriteAllTextAsync(path, text, ct));
        files.Setup(fs => fs.ReadAllTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string path, CancellationToken ct) => File.ReadAllTextAsync(path, ct));
        files.Setup(fs => fs.DeleteFile(It.IsAny<string>())).Callback((string path) => File.Delete(path));
        _runner = new(files.Object);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidInputIsRejectedAndValidInputPasses(bool xml)
    {
        if (xml)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => _runner.ValidateXmlSyntaxAsync("<broken>"));
            await _runner.ValidateXmlSyntaxAsync("<valid />");
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => _runner.ValidateScriptSyntaxAsync("function Broken {"));
            await _runner.ValidateScriptSyntaxAsync("Write-Output 'valid'");
        }
        Assert.Empty(Directory.GetFiles(_root));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
