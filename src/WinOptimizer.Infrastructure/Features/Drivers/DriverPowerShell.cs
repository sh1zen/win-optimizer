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

    internal async Task<T> RunAsync<T>(string script, string prelude, CancellationToken ct,
        bool preludeIsOptimization = false) where T : class
    {
        using var resource = typeof(DriverPowerShell).Assembly.GetManifestResourceStream($"WinOptimizer.Drivers.{script}.ps1")
            ?? throw new DriverScanException($"Driver query resource is missing: {script}.");
        using var reader = new StreamReader(resource, Encoding.UTF8);
        const string initialization = "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; " +
            "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);\n";
        var bundledScript = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        const string options = "-NoProfile -NonInteractive -EncodedCommand ";
        var executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var sourceLength = initialization.Length + prelude.Length + 1L + bundledScript.Length;
        var encodedLength = (sourceLength * 2 + 2) / 3 * 4;
        // CreateProcessW includes the quoted executable, space and terminator in its limit.
        if (preludeIsOptimization && executable.Length + 4L + options.Length + encodedLength > 32767)
            prelude = "";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(initialization + prelude + "\n" + bundledScript));
        ProcessExecutionResult result;
        try
        {
            result = await processes.ExecuteWithStreamingAsync(executable, options + encoded, ct: ct).ConfigureAwait(false);
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
