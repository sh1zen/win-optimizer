using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Optimize.Models;
using WinOptimizer.Core.Features.SoftwareApps.Utilities;
using WinOptimizer.Infrastructure.Features.AdvancedTools.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.AdvancedTools;

/// <summary>Parses real generated scripts with Windows PowerShell 5.1 without executing their operations.</summary>
public class StorageScriptsSyntaxTests
{
    [Fact]
    public async Task ExportedScriptsParseWithTheSupportedPowerShellRuntime()
    {
        var power = new Mock<IPowerSettingsQueryService>();
        power.Setup(service => service.GetActivePowerPlanAsync()).ReturnsAsync(new PowerPlan { Guid = "SCHEME_CURRENT", Name = "Balanced" });
        power.Setup(service => service.GetAllPowerSettingsACDCAsync(It.IsAny<string>()))
            .ReturnsAsync(new Dictionary<string, (int? acValue, int? dcValue)>());
        var runner = new Mock<IPowerShellRunner>();
        runner.Setup(service => service.ValidateScriptSyntaxAsync(It.IsAny<string>(), default)).Returns(Task.CompletedTask);
        var builder = new AutounattendScriptBuilder(power.Object, Mock.Of<IHardwareDetectionService>(),
            Mock.Of<ILogService>(), Mock.Of<IComboBoxResolver>(), runner.Object);
        var config = new UnifiedConfigurationFile
        {
            WindowsApps = new ConfigSection
            {
                Items = [new ConfigurationItem { Id = "example-app", AppxPackageName = ["Example.App"] }]
            }
        };
        var generated = await builder.BuildOptimizationsScriptAsync(config,
            new Dictionary<string, IEnumerable<SettingDefinition>>());
        var root = Path.Combine(Path.GetTempPath(), "WinOptimizerStorageTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var scripts = new[] { generated,
                BloatRemovalScriptGenerator.GenerateScript(["Example.App"], ["Example.Capability"], ["ExampleFeature"]) };
            var parser = new StringBuilder("$ErrorActionPreference='Stop';");
            for (var index = 0; index < scripts.Length; index++)
            {
                var path = Path.Combine(root, $"script-{index}.ps1");
                await File.WriteAllTextAsync(path, scripts[index], new UTF8Encoding(false));
                var encodedPath = Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
                parser.Append($"$p=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{encodedPath}'));");
                parser.Append("$t=$null;$e=$null;[void][Management.Automation.Language.Parser]::ParseFile($p,[ref]$t,[ref]$e);if($e.Count){$e|Out-String|Write-Error;exit 1};");
            }
            await RunWindowsPowerShellAsync(parser.ToString());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServicingScriptReportsEachCompletedTargetIncludingAfterCancellation(bool cancelAfterFirst)
    {
        var root = Directory.CreateTempSubdirectory("windows-package-removal-");
        try
        {
            var path = Path.Combine(root.FullName, "removal.ps1");
            await File.WriteAllTextAsync(path, BloatRemovalScriptGenerator.GenerateScript([], ["Example.One", "Example.Two"],
                ["ExampleFeature"], reportResults: true), new UTF8Encoding(false));
            var encodedPath = Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
            var output = await RunWindowsPowerShellAsync($$"""
                $ErrorActionPreference = 'Stop'
                $p = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{encodedPath}}'))
                . $p
                $cancelAfterFirst = ${{cancelAfterFirst.ToString().ToLowerInvariant()}}
                $cancelSignal = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset)
                function Get-WindowsCapability {
                    [pscustomobject]@{Name='Example.One~~~~0.0.1.0';State='Installed'}
                    [pscustomobject]@{Name='Example.Two~~~~0.0.1.0';State='Installed'}
                    [pscustomobject]@{Name='Example.TwoOther~~~~0.0.1.0';State='Installed'}
                }
                function Remove-WindowsCapability {
                    [CmdletBinding()] param([switch]$Online, [string]$Name)
                    if ($Name -eq 'Example.One~~~~0.0.1.0') {
                        if (!$cancelAfterFirst) { throw 'Simulated DISM rejection' }
                        [void]$cancelSignal.Set()
                        return [pscustomobject]@{RestartNeeded=$true}
                    }
                    if ($Name -ne 'Example.Two~~~~0.0.1.0') { throw 'Unexpected native identity' }
                    [pscustomobject]@{RestartNeeded=$false}
                }
                function Get-WindowsOptionalFeature { [pscustomobject]@{FeatureName='ExampleFeature';State='Enabled'} }
                function Disable-WindowsOptionalFeature {
                    [CmdletBinding()] param([switch]$Online, [string]$FeatureName, [switch]$NoRestart)
                    if ($FeatureName -ne 'ExampleFeature' -or !$NoRestart) { throw 'Unexpected native feature request' }
                    [pscustomobject]@{RestartNeeded=$true}
                }
                try {
                    Invoke-WindowsPackageRemoval -Packages $packages -Capabilities $capabilities -OptionalFeatures $optionalFeatures -LogFolder ([IO.Path]::GetDirectoryName($p)) -ReportResults $reportResults -CancelSignal $cancelSignal
                } finally { $cancelSignal.Dispose() }
                """);
            var line = Assert.Single(output.Split('\n'), line => line.StartsWith(BloatRemovalScriptGenerator.ResultPrefix, StringComparison.Ordinal));
            using var report = JsonDocument.Parse(line[BloatRemovalScriptGenerator.ResultPrefix.Length..].TrimEnd());
            var results = report.RootElement.EnumerateArray().ToDictionary(row => row.GetProperty("Key").GetString()!);
            if (cancelAfterFirst)
            {
                var completed = Assert.Single(results);
                Assert.Equal("capability:Example.One", completed.Key);
                Assert.Equal(JsonValueKind.Null, completed.Value.GetProperty("Error").ValueKind);
                Assert.True(completed.Value.GetProperty("RestartRequired").GetBoolean());
                return;
            }
            Assert.Equal(3, results.Count);
            Assert.Equal("Simulated DISM rejection", results["capability:Example.One"].GetProperty("Error").GetString());
            Assert.Equal(JsonValueKind.Null, results["capability:Example.Two"].GetProperty("Error").ValueKind);
            Assert.True(results["feature:ExampleFeature"].GetProperty("RestartRequired").GetBoolean());
        }
        finally { root.Delete(recursive: true); }
    }

    private static async Task<string> RunWindowsPowerShellAsync(string command)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Windows PowerShell did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        var text = await output;
        Assert.True(process.ExitCode == 0, text + await errors);
        return text;
    }
}
