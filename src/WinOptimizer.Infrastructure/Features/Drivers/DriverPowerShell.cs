using System.Text;
using System.Text.Json;
using System.ComponentModel;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Drivers.Models;

namespace WinOptimizer.Infrastructure.Features.Drivers;

/// <summary>Runs bundled driver queries in a cancellable, isolated Windows PowerShell process.</summary>
public sealed class DriverPowerShell(IProcessExecutor processes)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        RespectRequiredConstructorParameters = true
    };

    internal async Task<T> RunAsync<T>(string script, string prelude, CancellationToken ct) where T : class
    {
        using var resource = typeof(DriverPowerShell).Assembly.GetManifestResourceStream($"WinOptimizer.Drivers.{script}.ps1")
            ?? throw new DriverScanException($"Driver query resource is missing: {script}.");
        using var reader = new StreamReader(resource, Encoding.UTF8);
        var source = "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; " +
            "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);\n" + prelude + "\n" + await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(source));
        var executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        ProcessExecutionResult result;
        try
        {
            result = await processes.ExecuteWithStreamingAsync(executable, $"-NoProfile -NonInteractive -EncodedCommand {encoded}", ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        { throw new DriverScanException($"{script}: Unable to run the Windows driver query: {ex.Message}", ex); }
        ct.ThrowIfCancellationRequested();
        if (!result.Succeeded) throw new DriverScanException($"{script}: {result.StandardError.Trim()}");
        try
        {
            return JsonSerializer.Deserialize<T>(result.StandardOutput, JsonOptions)
                ?? throw new DriverScanException($"{script}: Windows returned no driver data.");
        }
        catch (JsonException ex) { throw new DriverScanException($"{script}: Windows returned invalid driver data.", ex); }
    }
}
