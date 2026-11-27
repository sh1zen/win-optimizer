using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Drivers.Models;
using WinOptimizer.Infrastructure.Features.Drivers;
using WinOptimizer.Infrastructure.Features.Common.Services;
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
    private static DriverDiscoveryContext Context() => new(new DriverInventory([], [], true, []), new());
    private static DriverComponent Component(DriverCertification certification) =>
        new("driver.inf", "Net", "OEM", "2.0", "2026-01-01", [new("PCI\\TEST", DriverIdentifierKind.Hardware, 0)],
            ["Models.NTamd64"], "", "", "", certification == DriverCertification.Whql, certification);

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
        inspector.Setup(x => x.InspectAsync(It.IsAny<string>(),
            It.Is<string[]>(paths => paths.Length == 1 && paths[0].EndsWith("driver.inf")), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Component(certification)]);
        var result = await new WindowsDriverUpdateSource(new(_process.Object), inspector.Object, Path.GetTempPath()).SearchAsync(Context(), null, default);
        var update = Assert.Single(result.Updates);
        Assert.Equal(certification, update.Certification);
        Assert.Equal("2.0", update.Version);
        Assert.Equal(4096, update.SizeBytes);
        Assert.False(Directory.Exists(root));
        Assert.Contains("$searcher.ServerSelection = 2", source);
        Assert.Contains("IsInstalled=0 and IsHidden=0 and Type='Driver'", source);
        Assert.Contains("DeploymentAction=*", source);
        Assert.DoesNotContain("CreateUpdateInstaller", source);
        Assert.DoesNotContain("AcceptEula(", source);
        inspector.VerifyAll();
    }
    [Fact]
    public Task UpdateScript_SearchesOptionalOffers_AndExportsIndividualBundledPayloads() =>
        RunScriptFixtureAsync("DriverUpdates", "driver-updates-scripts.tests.ps1");

    [Fact]
    public Task InventoryScript_MeasuresOnlySelectedPackagesDuringRemoval() =>
        RunScriptFixtureAsync("DriverInventory", "driver-inventory-scripts.tests.ps1");

    [Theory]
    [InlineData(2, true)]
    [InlineData(10000, false)]
    public async Task RemovalInventoryFitsWindowsProcessArguments(int packageCount, bool usesSelection)
    {
        Output(JsonSerializer.Serialize(new DriverInventory([], [], true, [])));
        var selected = Enumerable.Range(1, packageCount).Select(index => $"oem{index}.inf").ToHashSet();

        await new WindowsDriverInventorySource(new(_process.Object)).ReadForRemovalAsync(selected, default);

        _process.Verify(x => x.ExecuteWithStreamingAsync(It.IsAny<string>(), It.Is<string>(args =>
            args.Length < 32000 && DecodeScript(args).Contains("$measureSelectedOnly=$true") == usesSelection),
            null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static async Task RunScriptFixtureAsync(string script, string fixture)
    {
        using var workspace = new DriverScanWorkspace(Path.GetTempPath());
        var scriptPath = Path.Combine(workspace.Root, script + ".ps1");
        using (var resource = typeof(DriverPowerShell).Assembly.GetManifestResourceStream($"WinOptimizer.Drivers.{script}.ps1")!)
        using (var reader = new StreamReader(resource))
            await File.WriteAllTextAsync(scriptPath, await reader.ReadToEndAsync());
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "tests", fixture))) root = root.Parent;
        Assert.NotNull(root);
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(root.FullName, "tests", fixture), "-ScriptPath", scriptPath }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException($"Driver script fixture timed out.\n{await output}\n{await error}");
        }
        Assert.True(process.ExitCode == 0, await output + await error);
    }
    [Fact]
    public async Task UnavailablePayload_IsExplicitlyUnverified_AndPreservesTheOffer()
    {
        Output(Offers("license needs review"));
        var result = await new WindowsDriverUpdateSource(new(_process.Object), Mock.Of<IDriverInfInspector>(), Path.GetTempPath()).SearchAsync(Context(), null, default);
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
        inspector.Setup(x => x.InspectAsync(It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { cts.Cancel(); return [Component(DriverCertification.Whql)]; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WindowsDriverUpdateSource(new(_process.Object), inspector.Object, Path.GetTempPath()).SearchAsync(Context(), null, cts.Token));
        Assert.False(Directory.Exists(root));
    }
    [Theory]
    [InlineData(0x0D000001u, true)] [InlineData(0x0D000002u, true)] [InlineData(0x0D000005u, true)]
    [InlineData(0x0D000003u, false)] [InlineData(0x0D000004u, false)] [InlineData(0x0F000000u, false)] [InlineData(0u, false)]
    public void Certification_RequiresWhqlClassification_NotJustASignature(uint score, bool expected) => Assert.Equal(expected, WindowsDriverInfInspector.IsWhqlScore(score));

    [Theory]
    [InlineData(true, 0u, true)]
    [InlineData(false, 0xE0000241u, true)]
    [InlineData(false, 0xE0000242u, false)]
    [InlineData(false, 0xE0000243u, false)]
    [InlineData(false, 0x80096010u, false)]
    public void InfSignatureAcceptsAuthenticodeFromTheTrustedPublisherStore(bool verified, uint error, bool trusted) =>
        Assert.Equal(trusted, WindowsDriverInfInspector.HasTrustedSignature(verified, error));

    [Fact]
    public async Task NativeInfInspection_MatchesArchitectureAndHardware_WithoutCertifyingUnsignedFixtures()
    {
        using var workspace = new DriverScanWorkspace(Path.GetTempPath());
        var path = Path.Combine(workspace.Root, "test.inf");
        var architecture = RuntimeInformation.OSArchitecture switch
        { Architecture.X64 => "NTamd64", Architecture.Arm64 => "NTarm64", _ => "NTx86" };
        var futureBuild = Environment.OSVersion.Version.Build + 1;
        File.WriteAllText(path, $$"""
            [Version]
            Signature="$Windows NT$"
            Class=Net
            ClassGuid={4d36e972-e325-11ce-bfc1-08002be10318}
            Provider=Test
            DriverVer=01/01/2026,2.0.0.0
            [Manufacturer]
            Test=Models,NTamd64,NTarm64,NTx86,{{architecture}}.10.0...{{futureBuild}}
            [Models.NTamd64]
            Test=Install,PCI\TEST
            Second=DefaultInstall,PCI\DEFAULT
            [Models.NTarm64]
            Test=Install,PCI\TEST
            Second=DefaultInstall,PCI\DEFAULT
            [Models.NTx86]
            Test=Install,PCI\TEST
            Second=DefaultInstall,PCI\DEFAULT
            [Models.{{architecture}}.10.0...{{futureBuild}}]
            Test=Install,PCI\FUTURE_ONLY
            [Install.{{architecture}}]
            DriverVer=02/02/2026,3.0.0.0
            [Install.NT]
            DriverVer=03/03/2026,9.0.0.0
            [DefaultInstall]
            """);
        var inspector = new WindowsDriverInfInspector(new(new ProcessExecutor()));
        var info = Assert.Single(await inspector.InspectAsync(workspace.Root, [path], default));
        Assert.Equal("2.0.0.0", info.Version);
        Assert.Equal(DriverCertification.Unverified, info.Certification);
        Assert.Equal(["PCI\\TEST", "PCI\\DEFAULT"], info.Identifiers.Select(id => id.Value));
        Assert.Contains("Models." + architecture, info.ModelsSections);
        var overridden = Assert.Single(info.InstallSections, section => section.Name == "Install." + architecture);
        Assert.Equal("PCI\\TEST", Assert.Single(overridden.Identifiers).Value);
        Assert.Equal(("3.0.0.0", "2026-02-02"), (overridden.Version, overridden.Date));
        var inherited = Assert.Single(info.InstallSections, section => section.Name == "DefaultInstall");
        Assert.Equal(("2.0.0.0", "2026-01-01"), (inherited.Version, inherited.Date));
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
