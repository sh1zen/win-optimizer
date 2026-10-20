using System.Diagnostics;
using System.Text;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Optimize.Models;
using WinOptimizer.Core.Features.SoftwareApps.Models;
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
        var generated = await builder.BuildOptimizationsScriptAsync(new UnifiedConfigurationFile(),
            new Dictionary<string, IEnumerable<SettingDefinition>>());
        var root = Path.Combine(Path.GetTempPath(), "WinOptimizerStorageTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var scripts = new[] { generated, OneDriveRemovalScript.GetScript(), EdgeRemovalScript.GetScript() };
            var parser = new StringBuilder("$ErrorActionPreference='Stop';");
            for (var index = 0; index < scripts.Length; index++)
            {
                var path = Path.Combine(root, $"script-{index}.ps1");
                await File.WriteAllTextAsync(path, scripts[index], new UTF8Encoding(false));
                var encodedPath = Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
                parser.Append($"$p=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{encodedPath}'));");
                parser.Append("$t=$null;$e=$null;[void][Management.Automation.Language.Parser]::ParseFile($p,[ref]$t,[ref]$e);if($e.Count){$e|Out-String|Write-Error;exit 1};");
            }
            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
                Convert.ToBase64String(Encoding.Unicode.GetBytes(parser.ToString())) })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("Windows PowerShell did not start.");
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            Assert.True(process.ExitCode == 0, await output + await errors);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
