using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Infrastructure.Features.Management;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Utilities;

/// <summary>Checks the actual composed script and transport without launching PowerShell or downloading tools.</summary>
public sealed class ManagementPowerShellRunnerTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(Path.GetTempPath(), "WinOptimizerStorageTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory)) Directory.Delete(_temporaryDirectory, recursive: true);
    }

    [Theory]
    [InlineData("Scan-Autoruns", "Autoruns")]
    [InlineData("Process-Handles", "Handle")]
    public async Task OptionalToolsExecuteOneAuthenticatedScriptWithTheSharedCache(string resource, string tool)
    {
        var executor = new Mock<IProcessExecutor>(); string? temporary = null;
        executor.Setup(value => value.ExecuteAsync(It.IsAny<string>(), It.IsAny<string>(), CancellationToken.None))
            .ReturnsAsync((string executable, string arguments, CancellationToken _) =>
            {
                var encoded = arguments.Split("-EncodedCommand ")[1];
                var loader = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
                var path = Regex.Match(loader, "FromBase64String\\('([A-Za-z0-9+/=]+)'\\)");
                temporary = Encoding.UTF8.GetString(Convert.FromBase64String(path.Groups[1].Value));
                var bytes = File.ReadAllBytes(temporary); var script = Encoding.UTF8.GetString(bytes);
                Assert.StartsWith("param(", script);
                Assert.Single(Regex.Matches(script, "function Open-VerifiedSysinternalsTool"));
                Assert.Contains("Open-VerifiedSysinternalsTool -Tool " + tool, script);
                Assert.Contains(Convert.ToBase64String(SHA256.HashData(bytes)), loader);
                var data = Regex.Match(loader, "-RequestBase64 '([A-Za-z0-9+/=]+)'$");
                Assert.Contains("$(literal)", Encoding.UTF8.GetString(Convert.FromBase64String(data.Groups[1].Value)));
                return new ProcessExecutionResult { StandardOutput = "verified" };
            });
        Assert.Equal("verified", await new ManagementPowerShellRunner(executor.Object, _temporaryDirectory).RunAsync(resource, new { Value = "$(literal)" }, default));
        Assert.NotNull(temporary); Assert.False(File.Exists(temporary));
    }
}
