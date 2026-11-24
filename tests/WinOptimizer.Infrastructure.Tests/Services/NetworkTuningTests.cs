using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Win32;
using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Optimize.Models;
using WinOptimizer.Infrastructure.Features.Common.Services;
using WinOptimizer.Infrastructure.Features.Optimize.Services;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

/// <summary>Exercises the production network catalogue and service without reading or changing the host network.</summary>
[Collection(nameof(NetworkTuningProcessCollection))]
public sealed class NetworkTuningTests : IDisposable
{
    private readonly Mock<IPowerShellRunner> _scripts = new();
    private readonly Mock<IWindowsRegistryService> _registry = new();
    private readonly Mock<IApplicationModeService> _mode = new();
    private readonly DirectoryInfo _storage = Directory.CreateTempSubdirectory("win-optimizer-network-originals-");

    private OptimizationToolsService Create()
    {
        _mode.Setup(mode => mode.BeginSystemOperation()).Returns(Mock.Of<IDisposable>());
        return new(_scripts.Object, Mock.Of<ISettingApplicationService>(), Mock.Of<ICompatibleSettingsRegistry>(),
            _registry.Object, _mode.Object, Mock.Of<IAppxDeployment>(), Mock.Of<IWindowsVersionService>(), Mock.Of<ISystemSettingsDiscoveryService>(),
            new NetworkTuningOriginalStore(new FileSystemService(), _storage.FullName));
    }

    public void Dispose() => _storage.Delete(recursive: true);

    private static NetworkTuningState[] States(string? changedId = null, string? changedValue = null) => NetworkTuningCatalog.All
        .Select(item => new NetworkTuningState(item.Id, item.Id == changedId ? changedValue : item.Input switch
        {
            NetworkTuningInput.Toggle => "Disabled",
            NetworkTuningInput.Selection => item.Options[0],
            _ => item.HasRegistryOverride ? null : item.Minimum.ToString(CultureInfo.InvariantCulture)
        }, true)).ToArray();

    private void ReadReturns(NetworkTuningState[] states) => _scripts.Setup(scripts => scripts.RunScriptInMemoryAsync(
        OptimizationToolsService.ReadNetworkTuningsScript, null, default)).ReturnsAsync(JsonSerializer.Serialize(states));

    [Fact]
    public void CatalogueContainsDistinctControlsAndFourLegacyPriorities()
    {
        Assert.Equal(26, NetworkTuningCatalog.All.Count);
        Assert.Equal(26, NetworkTuningCatalog.All.Select(item => item.Id).Distinct().Count());
        Assert.Equal(26, NetworkTuningCatalog.All.Select(item => (item.Owner, item.Property)).Distinct().Count());
        Assert.Equal(new[] { "LocalPriority", "HostsPriority", "DnsPriority", "NetbtPriority" },
            NetworkTuningCatalog.All.Where(item => item.Owner == NetworkTuningOwner.NameResolution).Select(item => item.Property));
        Assert.Equal("InitialRto", NetworkTuningCatalog.Get("tcp-initial-rto").Property);
        Assert.Equal("InitialRtoMs", NetworkTuningCatalog.Get("tcp-initial-rto").Parameter);
        Assert.Equal("NeighborCacheLimit", NetworkTuningCatalog.Get("ipv4-neighbor-cache").Property);
        Assert.Equal("NeighborCacheLimitEntries", NetworkTuningCatalog.Get("ipv4-neighbor-cache").Parameter);
        foreach (var definition in NetworkTuningCatalog.All)
            if (definition.Suggestion is { } suggestion) definition.Validate(suggestion.Value);
    }

    [Theory]
    [InlineData("tcp-ecn", "Enabled'; throw 'injected")]
    [InlineData("tcp-auto-tuning", "Unknown")]
    [InlineData("tcp-timestamps", "Normal")]
    [InlineData("tcp-initial-rto", "301")]
    [InlineData("tcp-initial-rto", "299")]
    [InlineData("tcp-syn-retries", "9")]
    [InlineData("ipv4-hop-limit", "256")]
    [InlineData("ipv6-neighbor-cache", "-1")]
    [InlineData("priority-dns", "65536")]
    [InlineData("priority-dns", "6.5")]
    [InlineData("tcp-ecn", null)]
    public async Task InvalidValuesCannotReachWindows(string id, string? value)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Create().SetNetworkTuningAsync(id, value, null));
        _scripts.VerifyNoOtherCalls(); _registry.VerifyNoOtherCalls();
        _mode.Verify(mode => mode.BeginSystemOperation(), Times.Never);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("not json")]
    public async Task MalformedOrIncompleteReadsAreErrors(string output)
    {
        _scripts.Setup(scripts => scripts.RunScriptInMemoryAsync(OptimizationToolsService.ReadNetworkTuningsScript, null, default)).ReturnsAsync(output);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create().ReadNetworkTuningsAsync());
    }

    [Fact]
    public async Task ReadsPreserveUnavailableControlsAndAbsentOverrides()
    {
        var states = States();
        states[0] = states[0] with { IsAvailable = false, Value = null, ErrorMessage = "Not supported" };
        ReadReturns(states);
        var actual = await Create().ReadNetworkTuningsAsync();
        Assert.Equal(states, actual.Select(item => item with { Original = null }));
        Assert.Null(actual.Single(item => item.Id == "priority-dns").Value);
        Assert.False(actual[0].IsAvailable);
        Assert.Null(actual[0].Original);
        _registry.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FirstAvailableValuesSurviveEditsReloadAndNewServiceInstances()
    {
        const string lateId = "ipv6-route-cache";
        const string overrideId = "priority-dns";
        var initial = States().Select(state => state.Id == lateId
            ? state with { IsAvailable = false, Value = null, ErrorMessage = "Read denied" } : state).ToArray();
        ReadReturns(initial);
        var service = Create();
        var firstRead = await service.ReadNetworkTuningsAsync();
        Assert.Null(firstRead.Single(item => item.Id == lateId).Original);
        Assert.Equal(new NetworkTuningValue(null), firstRead.Single(item => item.Id == overrideId).Original);

        _registry.Setup(registry => registry.SetValue(OptimizationToolsService.NetworkPriorityPath, "DnsPriority", 6, RegistryValueKind.DWord))
            .Callback(() => ReadReturns(States(overrideId, "6"))).Returns(true);
        var edited = await service.SetNetworkTuningAsync(overrideId, "6", null);
        Assert.Equal(new NetworkTuningValue(null), edited.Single(item => item.Id == overrideId).Original);
        Assert.Equal(new NetworkTuningValue("1"), edited.Single(item => item.Id == lateId).Original);

        ReadReturns(States(overrideId, "12").Select(state => state.Id == lateId
            ? state with { IsAvailable = false, Value = null, ErrorMessage = "Read denied again" } : state).ToArray());
        var reopened = await Create().ReadNetworkTuningsAsync();
        var saved = reopened.Single(item => item.Id == overrideId);
        Assert.Equal("12", saved.Value);
        Assert.Equal(new NetworkTuningValue(null), saved.Original);
        Assert.Equal(new NetworkTuningValue("1"), reopened.Single(item => item.Id == lateId).Original);
    }

    [Fact]
    public async Task AnUnreadableOriginalPreventsAnUnprotectedWrite()
    {
        ReadReturns(States());
        await File.WriteAllTextAsync(Path.Combine(_storage.FullName, "tcp-ecn.json"), "{invalid");
        var write = OptimizationToolsService.NetworkWriteScript(NetworkTuningCatalog.Get("tcp-ecn"), "Enabled");
        var error = await Assert.ThrowsAsync<IOException>(() => Create().SetNetworkTuningAsync("tcp-ecn", "Enabled", "Disabled"));
        Assert.Contains("tcp-ecn.json", error.Message);
        _scripts.Verify(scripts => scripts.RunScriptInMemoryAsync(write, null, default), Times.Never);
    }

    [Fact]
    public async Task DuplicateAndUnknownIdsCannotHideMissingControls()
    {
        var states = States();
        states[0] = states[1];
        ReadReturns(states);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create().ReadNetworkTuningsAsync());
        states[0] = new("foreign-setting", "Enabled", true);
        ReadReturns(states);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create().ReadNetworkTuningsAsync());
    }

    public static IEnumerable<object[]> NativeControls() => NetworkTuningCatalog.All
        .Where(item => !item.HasRegistryOverride).Select(item => new object[] { item.Id });

    [Theory]
    [MemberData(nameof(NativeControls))]
    public async Task EachNativeControlChangesOnlyItsAllowlistedParameterAndRequiresMatchingReadback(string id)
    {
        var definition = NetworkTuningCatalog.Get(id);
        var value = definition.Input switch
        {
            NetworkTuningInput.Toggle => "Enabled",
            NetworkTuningInput.Selection => definition.Options[^1],
            _ => (definition.Minimum + definition.Step).ToString(CultureInfo.InvariantCulture)
        };
        var before = States();
        var after = States(id, value);
        _scripts.SetupSequence(scripts => scripts.RunScriptInMemoryAsync(OptimizationToolsService.ReadNetworkTuningsScript, null, default))
            .ReturnsAsync(JsonSerializer.Serialize(before)).ReturnsAsync(JsonSerializer.Serialize(after));
        var write = OptimizationToolsService.NetworkWriteScript(definition, value);
        _scripts.Setup(scripts => scripts.RunScriptInMemoryAsync(write, null, default)).ReturnsAsync("");
        var actual = await Create().SetNetworkTuningAsync(id, value, before.Single(item => item.Id == id).Value);
        Assert.Equal(after, actual.Select(item => item with { Original = null }));
        Assert.Equal(new NetworkTuningValue(before.Single(item => item.Id == id).Value), actual.Single(item => item.Id == id).Original);
        _scripts.Verify(scripts => scripts.RunScriptInMemoryAsync(write, null, default), Times.Once);
        _scripts.Verify(scripts => scripts.RunScriptInMemoryAsync(OptimizationToolsService.ReadNetworkTuningsScript, null, default), Times.Exactly(2));
        Assert.Contains("-" + definition.Parameter + " '" + value + "'", write);
        _registry.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("priority-local", "4")]
    [InlineData("priority-hosts", "5")]
    [InlineData("priority-dns", "6")]
    [InlineData("priority-netbt", "7")]
    public async Task PriorityEditsUseTheRequestedDwordValueAndCanClearTheOverride(string id, string value)
    {
        var definition = NetworkTuningCatalog.Get(id);
        ReadReturns(States());
        _registry.Setup(registry => registry.SetValue(OptimizationToolsService.NetworkPriorityPath, definition.Property,
            int.Parse(value, CultureInfo.InvariantCulture), RegistryValueKind.DWord))
            .Callback(() => ReadReturns(States(id, value))).Returns(true);
        await Create().SetNetworkTuningAsync(id, value, null);
        _registry.Setup(registry => registry.ValueExists(OptimizationToolsService.NetworkPriorityPath, definition.Property)).Returns(true);
        _registry.Setup(registry => registry.DeleteValue(OptimizationToolsService.NetworkPriorityPath, definition.Property))
            .Callback(() => ReadReturns(States())).Returns(true);
        await Create().SetNetworkTuningAsync(id, null, value);
        _registry.Verify(registry => registry.DeleteValue(OptimizationToolsService.NetworkPriorityPath, definition.Property), Times.Once);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("5000", 5000)]
    [InlineData("4294967295", -1)]
    public async Task DeliveryLimitWritesAndClearsOnlyTheMachinePolicy(string value, int rawValue)
    {
        const string id = "delivery-background-limit";
        const string name = "DOMaxBackgroundDownloadBandwidth";
        const string path = OptimizationToolsService.DeliveryOptimizationPath;
        ReadReturns(States());
        _registry.Setup(registry => registry.SetValue(path, name, rawValue, RegistryValueKind.DWord))
            .Callback(() => ReadReturns(States(id, value))).Returns(true);
        var service = Create();
        var applied = await service.SetNetworkTuningAsync(id, value, null);
        Assert.Equal(value, applied.Single(state => state.Id == id).Value);
        _registry.Setup(registry => registry.ValueExists(path, name)).Returns(true);
        _registry.Setup(registry => registry.DeleteValue(path, name)).Callback(() => ReadReturns(States())).Returns(true);
        var cleared = await service.SetNetworkTuningAsync(id, null, value);
        Assert.Null(cleared.Single(state => state.Id == id).Value);
        _registry.VerifyAll();
        _registry.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task StaleUnavailableAndUnverifiedSettingsCannotReportSuccess()
    {
        ReadReturns(States());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create().SetNetworkTuningAsync("tcp-ecn", "Enabled", "Enabled"));
        _scripts.Verify(scripts => scripts.RunScriptInMemoryAsync(It.Is<string>(script => script.Contains("Set-NetTCPSetting -SettingName")), null, default), Times.Never);
        var states = States();
        states[1] = states[1] with { IsAvailable = false, ErrorMessage = "Read denied" };
        ReadReturns(states);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create().SetNetworkTuningAsync("tcp-ecn", "Enabled", "Disabled"));
        ReadReturns(States());
        _scripts.Setup(scripts => scripts.RunScriptInMemoryAsync(OptimizationToolsService.NetworkWriteScript(NetworkTuningCatalog.Get("tcp-ecn"), "Enabled"), null, default)).ReturnsAsync("");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Create().SetNetworkTuningAsync("tcp-ecn", "Enabled", "Disabled"));
        Assert.Contains("did not confirm", error.Message);
    }

    [Theory]
    [InlineData(WinOptimizerMode.Builder)]
    [InlineData(WinOptimizerMode.ConfigReview)]
    public async Task InspectionRemainsAvailableButBothWriteOwnersAreBlockedInTestAndReview(WinOptimizerMode mode)
    {
        _mode.SetupGet(source => source.CurrentMode).Returns(mode);
        ReadReturns(States());
        var service = Create();
        await service.ReadNetworkTuningsAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetNetworkTuningAsync("tcp-ecn", "Enabled", "Disabled"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetNetworkTuningAsync("priority-local", "4", null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetNetworkTuningAsync("delivery-background-limit", "5000", null));
        _registry.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProductionScriptsRunAgainstFakeNativeCmdletsAndRegistryOnly()
    {
        var folder = Path.Combine(Path.GetTempPath(), "win-optimizer-network-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var payloadPath = Path.Combine(folder, "scripts.json");
            await File.WriteAllTextAsync(payloadPath, JsonSerializer.Serialize(new
            {
                Read = OptimizationToolsService.ReadNetworkTuningsScript,
                Writes = NetworkTuningCatalog.All.Where(item => !item.HasRegistryOverride).Select(item =>
                {
                    var value = item.Input == NetworkTuningInput.Number
                        ? item.Minimum.ToString(CultureInfo.InvariantCulture) : item.Options[^1];
                    return new
                    {
                        item.Id, Owner = item.Owner.ToString(), item.Property, item.Parameter,
                        Value = value, Script = OptimizationToolsService.NetworkWriteScript(item, value)
                    };
                })
            }));
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "tests", "network-tuning-scripts.tests.ps1"))) root = root.Parent;
            Assert.NotNull(root);
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(root.FullName, "tests", "network-tuning-scripts.tests.ps1"), "-PayloadPath", payloadPath }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            // Cold Windows PowerShell startup can be slow on hosted runners.
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw new TimeoutException($"The PowerShell network fixture exceeded two minutes.\nStandard output:\n{await output}\nStandard error:\n{await error}", exception);
            }
            Assert.True(process.ExitCode == 0, await output + await error);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}

/// <summary>Runs the PowerShell fixture without competing with other test collections for process startup.</summary>
[CollectionDefinition(nameof(NetworkTuningProcessCollection), DisableParallelization = true)]
public sealed class NetworkTuningProcessCollection { }
