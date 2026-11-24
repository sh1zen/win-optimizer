using Moq;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Utilities;
using WinOptimizer.Infrastructure.Features.Common.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

public sealed class RemovalScriptUpdateServiceTests
{
    private readonly Mock<IFileSystemService> _files = new();
    private RemovalScriptUpdateService Service => new(Mock.Of<ILogService>(), _files.Object);

    [Fact]
    public async Task RefreshesTheSharedScriptAndPreservesSavedPackageSelections()
    {
        _files.Setup(service => service.FileExists(It.IsAny<string>())).Returns(true);
        _files.Setup(service => service.ReadAllText(It.IsAny<string>())).Returns(
            "# Script Version: 2.4\n$packages = @('Example.App')\n$capabilities = @('Example.Cap')\n$optionalFeatures = @('Example.Feature')");

        await Service.CheckAndUpdateScriptsAsync();

        _files.Verify(service => service.WriteAllTextAsync(It.Is<string>(path => path.EndsWith(".tmp")),
            It.Is<string>(content => content.Contains("Script Version: " + BloatRemovalScriptGenerator.ScriptVersion) &&
                content.Contains("Example.App") && content.Contains("Example.Cap") && content.Contains("Example.Feature")), default), Times.Once);
        _files.Verify(service => service.ReplaceFile(It.Is<string>(path => path.EndsWith(".tmp")),
            Path.Combine(ScriptPaths.ScriptsDirectory, "BloatRemoval.ps1")), Times.Once);
    }

    [Fact]
    public async Task CurrentSharedScriptIsLeftInPlaceDuringAnUpdateCheck()
    {
        _files.Setup(service => service.FileExists(It.IsAny<string>())).Returns(true);
        _files.Setup(service => service.ReadAllText(It.IsAny<string>()))
            .Returns("# Script Version: " + BloatRemovalScriptGenerator.ScriptVersion);

        await Service.CheckAndUpdateScriptsAsync();

        _files.Verify(service => service.ReplaceFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }
}
