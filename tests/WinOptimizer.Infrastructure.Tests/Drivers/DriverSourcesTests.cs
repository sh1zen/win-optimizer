using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Drivers.Models;
using WinOptimizer.Infrastructure.Features.Drivers;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Drivers;

public sealed class DriverSourcesTests
{
    private readonly Mock<IProcessExecutor> _process = new(MockBehavior.Strict);
    private void Output(string json, int exit = 0) => _process.Setup(x => x.ExecuteWithStreamingAsync(It.IsAny<string>(), It.IsAny<string>(), null, null, It.IsAny<CancellationToken>()))
        .ReturnsAsync(new ProcessExecutionResult { StandardOutput = json, ExitCode = exit, StandardError = exit == 0 ? "" : "access denied" });
    private static string DecodeScript(string args) => Encoding.Unicode.GetString(Convert.FromBase64String(args.Split(' ').Last()));
    private const string Id = "d4335ba6-fd4e-4081-9bb5-379f7fa346bc";
    private static string Offers(string error = "") => JsonSerializer.Serialize(new { Updates = new[] { new {
        Id, Title = "Official update", Provider = "OEM", ClassName = "Net", HardwareId = "PCI\\TEST", Date = "2026-01-01", SizeBytes = 4096, PayloadError = error } } });

    [Fact]
    public async Task Inventory_UsesCancellableBundledQuery_AndReturnsWindowsEvidence()
    {
        Output(JsonSerializer.Serialize(new DriverInventory(
            [new("device", "Network", "Net", "oem1.inf", true, "1.0", ["PCI\\TEST"]) { Date = "2026-01-02" }],
            [new("oem1.inf", "net.inf", "OEM", "Net", "1.0", false, false) { Date = "2026-01-02", SizeBytes = 2048 }], true, [])));
        var result = await new WindowsDriverInventorySource(new(_process.Object)).ReadAsync(default);
        Assert.Equal("oem1.inf", Assert.Single(result.Devices).InfName);
        Assert.Equal("2026-01-02", result.Devices[0].Date);
        Assert.Equal(2048, Assert.Single(result.Packages).SizeBytes);
        _process.Verify(x => x.ExecuteWithStreamingAsync(
            It.Is<string>(path => path == Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")),
            It.Is<string>(args => DecodeScript(args).Contains("Get-PnpDevice -PresentOnly") && DecodeScript(args).Contains("Get-WindowsDriver -Online")),
            null, null, It.IsAny<CancellationToken>()), Times.Once);
    }
    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"Devices\":null,\"Packages\":[],\"BindingsComplete\":true,\"Notices\":[]}")]
    [InlineData("{\"Devices\":[null],\"Packages\":[],\"BindingsComplete\":true,\"Notices\":[]}")]
    public async Task Inventory_RejectsMalformedOrIncompleteBoundaryData(string json)
    {
        Output(json);
        await Assert.ThrowsAsync<DriverScanException>(() => new WindowsDriverInventorySource(new(_process.Object)).ReadAsync(default));
    }
    [Fact]
    public async Task ProcessFailure_IsReportedWithWindowsContext()
    {
        Output("", 5);
        var error = await Assert.ThrowsAsync<DriverScanException>(() => new WindowsDriverInventorySource(new(_process.Object)).ReadAsync(default));
        Assert.Contains("access denied", error.Message);
    }
    [Fact]
    public async Task QueryLaunchFailure_IsTranslatedToADriverError()
    {
        _process.Setup(x => x.ExecuteWithStreamingAsync(It.IsAny<string>(), It.IsAny<string>(), null, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.ComponentModel.Win32Exception(5));
        var error = await Assert.ThrowsAsync<DriverScanException>(() => new WindowsDriverInventorySource(new(_process.Object)).ReadAsync(default));
        Assert.IsType<System.ComponentModel.Win32Exception>(error.InnerException);
        Assert.Contains("DriverInventory", error.Message);
    }
    [Theory]
    [InlineData(DriverCertification.Whql)] [InlineData(DriverCertification.Unverified)]
    public async Task UpdateSource_InspectsActualMatchingPackage_AndCleansItsWorkspace(DriverCertification certification)
    {
        string? root = null;
        string? source = null;
        _process.Setup(x => x.ExecuteWithStreamingAsync(It.IsAny<string>(), It.IsAny<string>(), null, null, It.IsAny<CancellationToken>()))
            .Returns((string _, string args, Action<string>? _, Action<string>? _, CancellationToken _) =>
            {
                source = DecodeScript(args);
                root = Encoding.UTF8.GetString(Convert.FromBase64String(Regex.Match(source, @"FromBase64String\('([^']+)'\)").Groups[1].Value));
                var directory = Directory.CreateDirectory(Path.Combine(root, Id)).FullName;
                File.WriteAllText(Path.Combine(directory, "driver.inf"), "isolated test payload");
                return Task.FromResult(new ProcessExecutionResult { StandardOutput = Offers() });
            });
        var inspector = new Mock<IDriverInfInspector>(MockBehavior.Strict);
        inspector.Setup(x => x.Inspect(It.Is<string>(path => path.EndsWith("driver.inf")), "PCI\\TEST")).Returns(new DriverInfInspection("2.0", certification));
        var result = await new WindowsDriverUpdateSource(new(_process.Object), _process.Object, inspector.Object, Path.GetTempPath()).SearchAsync(null, default);
        var update = Assert.Single(result.Updates);
        Assert.Equal(certification, update.Certification);
        Assert.Equal("2.0", update.Version);
        Assert.Equal(4096, update.SizeBytes);
        Assert.False(Directory.Exists(root));
        Assert.Contains("$searcher.ServerSelection = 2", source);
        Assert.Contains("IsInstalled=0 and IsHidden=0 and Type='Driver'", source);
        Assert.DoesNotContain("CreateUpdateInstaller", source);
        Assert.DoesNotContain("AcceptEula(", source);
        inspector.VerifyAll();
    }
    [Fact]
    public async Task UnavailablePayload_IsExplicitlyUnverified_AndPreservesTheOffer()
    {
        Output(Offers("license needs review"));
        var result = await new WindowsDriverUpdateSource(new(_process.Object), _process.Object, Mock.Of<IDriverInfInspector>(), Path.GetTempPath()).SearchAsync(null, default);
        Assert.Equal(DriverCertification.Unverified, Assert.Single(result.Updates).Certification);
        Assert.Contains(result.Notices, notice => notice.Contains("license needs review"));
    }
    [Fact]
    public async Task CancellationDuringInspection_CleansExports_AndDoesNotPublishResults()
    {
        using var cts = new CancellationTokenSource();
        string? root = null;
        _process.Setup(x => x.ExecuteWithStreamingAsync(It.IsAny<string>(), It.IsAny<string>(), null, null, It.IsAny<CancellationToken>()))
            .Returns((string _, string args, Action<string>? _, Action<string>? _, CancellationToken _) =>
            {
                var script = DecodeScript(args);
                root = Encoding.UTF8.GetString(Convert.FromBase64String(Regex.Match(script, @"FromBase64String\('([^']+)'\)").Groups[1].Value));
                var directory = Directory.CreateDirectory(Path.Combine(root, Id)).FullName;
                File.WriteAllText(Path.Combine(directory, "driver.inf"), "fixture");
                return Task.FromResult(new ProcessExecutionResult { StandardOutput = Offers() });
            });
        var inspector = new Mock<IDriverInfInspector>();
        inspector.Setup(x => x.Inspect(It.IsAny<string>(), "PCI\\TEST"))
            .Returns(() => { cts.Cancel(); return new DriverInfInspection("2.0", DriverCertification.Whql); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WindowsDriverUpdateSource(new(_process.Object), _process.Object, inspector.Object, Path.GetTempPath()).SearchAsync(null, cts.Token));
        Assert.False(Directory.Exists(root));
    }
    [Theory]
    [InlineData(0x0D000001u, true)] [InlineData(0x0D000002u, true)] [InlineData(0x0D000005u, true)]
    [InlineData(0x0D000003u, false)] [InlineData(0x0D000004u, false)] [InlineData(0x0F000000u, false)] [InlineData(0u, false)]
    public void Certification_RequiresWhqlClassification_NotJustASignature(uint score, bool expected) => Assert.Equal(expected, WindowsDriverInfInspector.IsWhqlScore(score));

    [Fact]
    public void NativeInfInspection_MatchesArchitectureAndHardware_WithoutCertifyingUnsignedFixtures()
    {
        using var workspace = new DriverScanWorkspace(Path.GetTempPath());
        var path = Path.Combine(workspace.Root, "test.inf");
        File.WriteAllText(path, """
            [Version]
            Signature="$Windows NT$"
            Class=Net
            ClassGuid={4d36e972-e325-11ce-bfc1-08002be10318}
            Provider=Test
            DriverVer=01/01/2026,2.0.0.0
            [Manufacturer]
            Test=Models,NTamd64,NTarm64,NTx86
            [Models.NTamd64]
            Test=Install,PCI\TEST
            [Models.NTarm64]
            Test=Install,PCI\TEST
            [Models.NTx86]
            Test=Install,PCI\TEST
            """);
        var inspector = new WindowsDriverInfInspector();
        var info = inspector.Inspect(path, "pci\\test");
        Assert.NotNull(info);
        Assert.Equal("2.0.0.0", info.Version);
        Assert.Equal(DriverCertification.Unverified, info.Certification);
        Assert.Null(inspector.Inspect(path, "PCI\\OTHER"));
    }
    [Fact]
    public void Workspace_RejectsEscapePaths_AndDisposesOnlyItsOwnedDirectory()
    {
        var workspace = new DriverScanWorkspace(Path.GetTempPath());
        var root = workspace.Root;
        Assert.Throws<IOException>(() => DriverScanWorkspace.ValidateChild(root, Path.Combine(root, "..", "outside")));
        Assert.Throws<IOException>(() => DriverScanWorkspace.ValidateChild(root, root));
        var child = Path.Combine(root, "payload.inf");
        File.WriteAllText(child, "fixture");
        Assert.Equal(child, DriverScanWorkspace.ValidateChild(root, child));
        workspace.Dispose();
        Assert.False(Directory.Exists(root));
    }
    [Fact]
    public async Task CancelledQuery_DoesNotReturnSuccessfulInventory()
    {
        using var cts = new CancellationTokenSource();
        Output(JsonSerializer.Serialize(new DriverInventory([], [], true, [])));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WindowsDriverInventorySource(new(_process.Object)).ReadAsync(cts.Token));
    }
}
