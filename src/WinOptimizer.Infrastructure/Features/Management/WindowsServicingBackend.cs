using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Management;
using WinOptimizer.Core.Features.Utilities;

namespace WinOptimizer.Infrastructure.Features.Management;

/// <summary>Uses structured DISM PowerShell results, the shared DISM CLI runner and Unicode-safe SFC output.</summary>
internal sealed class WindowsServicingBackend(IProcessExecutor processes, IDismProcessRunner dism, IFileSystemService files) : IWindowsServicingBackend
{
    private static readonly JsonSerializerOptions JsonOptions = new() { RespectRequiredConstructorParameters = true };

    public Task<WindowsServicingPreparation> PrepareAsync(WindowsServicingRequest request) => Task.Run(() =>
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Restart win-optimizer as administrator to use Windows servicing.");
        request = NormalizeSource(request, files);
        using var servicing = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
        using var updates = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        return new WindowsServicingPreparation(request, servicing != null || updates != null);
    });

    /// <summary>Resolves custom source paths once; no command fragments are accepted from the UI.</summary>
    internal static WindowsServicingRequest NormalizeSource(WindowsServicingRequest request, IFileSystemService files)
    {
        if (request.Action != WindowsServicingAction.Repair) return request with { SourcePath = "", LimitAccess = false };
        if (string.IsNullOrWhiteSpace(request.SourcePath)) return request with { SourcePath = "" };
        if (!Path.IsPathFullyQualified(request.SourcePath) || request.SourcePath.Any(char.IsControl) || request.SourcePath.IndexOfAny(['*', '?', '"']) >= 0)
            throw new ArgumentException("Choose an absolute WIM file path or a Windows repair-source directory.");
        var path = Path.GetFullPath(request.SourcePath);
        if (Path.GetExtension(path).Equals(".wim", StringComparison.OrdinalIgnoreCase))
        {
            if (request.WimIndex < 1) throw new ArgumentException("The WIM image index must be at least 1.");
            if (!files.FileExists(path)) throw new FileNotFoundException("The selected WIM repair source is unavailable.", path);
        }
        else if (!files.DirectoryExists(path)) throw new DirectoryNotFoundException("The Windows repair-source directory is unavailable: " + path);
        return request with { SourcePath = path };
    }

    public async Task<WindowsServicingStepResult> RunStepAsync(WindowsServicingStep step, WindowsServicingRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (step is WindowsServicingStep.AnalyzeStore or WindowsServicingStep.Cleanup)
        {
            var arguments = "/Online /English /NoRestart /Cleanup-Image " +
                (step == WindowsServicingStep.AnalyzeStore ? "/AnalyzeComponentStore" : "/StartComponentCleanup" + (request.ResetBase ? " /ResetBase" : ""));
            var (code, output) = await dism.RunProcessWithProgressAsync(Path.Combine(Environment.SystemDirectory, "dism.exe"),
                arguments, null, cancellationToken).ConfigureAwait(false);
            return new(step, code is 0 or 3010, code, WindowsImageHealth.Unknown, code == 3010, output);
        }
        var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
        { Step = step.ToString(), request.SourcePath, request.WimIndex, request.LimitAccess }));
        var script = "$request = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + payload + "')) | ConvertFrom-Json\n" + ToolScript;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var result = await processes.ExecuteWithStreamingAsync(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            "-NoProfile -NonInteractive -EncodedCommand " + encoded, ct: cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
            return new(step, false, result.ExitCode, WindowsImageHealth.Unknown, false, result.StandardOutput + Environment.NewLine + result.StandardError);
        try
        {
            var response = JsonSerializer.Deserialize<ToolResponse>(result.StandardOutput, JsonOptions)
                ?? throw new JsonException("The servicing response was empty.");
            if (response.Health == null || response.Output == null) throw new JsonException("Required servicing fields were null.");
            var health = response.Health switch
            {
                "Healthy" => WindowsImageHealth.Healthy,
                "Repairable" => WindowsImageHealth.Repairable,
                "NonRepairable" => WindowsImageHealth.NonRepairable,
                _ => WindowsImageHealth.Unknown
            };
            return new(step, response.Success, response.ExitCode, health, response.RestartNeeded, response.Output);
        }
        catch (JsonException ex) { throw new UtilityOperationException("Windows servicing returned invalid result data. " + result.StandardError, ex); }
    }

    private sealed record ToolResponse(bool Success, int ExitCode, string Health, bool RestartNeeded, string Output);

    // User parameters travel as JSON data. DISM switches and SFC arguments are selected from fixed operations.
    internal const string ToolScript = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
        try {
            if ($request.Step -in @('VerifyFiles', 'RepairFiles')) {
                $start = [Diagnostics.ProcessStartInfo]::new()
                $start.FileName = Join-Path ([Environment]::SystemDirectory) 'sfc.exe'
                $start.Arguments = if ($request.Step -eq 'VerifyFiles') { '/verifyonly' } else { '/scannow' }
                $start.UseShellExecute = $false; $start.CreateNoWindow = $true
                $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
                $start.StandardOutputEncoding = [Text.Encoding]::Unicode
                $start.StandardErrorEncoding = [Text.Encoding]::Unicode
                $process = [Diagnostics.Process]::new(); $process.StartInfo = $start
                try {
                    if (-not $process.Start()) { throw 'SFC did not start.' }
                    $stdout = $process.StandardOutput.ReadToEndAsync()
                    $stderr = $process.StandardError.ReadToEndAsync()
                    $process.WaitForExit()
                    $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
                    [pscustomobject]@{ Success=($process.ExitCode -eq 0); ExitCode=$process.ExitCode; Health='Unknown'; RestartNeeded=$false; Output=$output } | ConvertTo-Json -Compress
                } finally { $process.Dispose() }
            } else {
                Import-Module Dism -ErrorAction Stop
                $parameters = @{ Online=$true; NoRestart=$true; ErrorAction='Stop'; WarningVariable='warnings'; WarningAction='SilentlyContinue' }
                switch ($request.Step) {
                    'CheckHealth' { $parameters.CheckHealth=$true }
                    'ScanHealth' { $parameters.ScanHealth=$true }
                    'RestoreHealth' {
                        $parameters.RestoreHealth=$true
                        $parameters.LimitAccess=[bool]$request.LimitAccess
                        if ($request.SourcePath) {
                            if ([IO.Path]::GetExtension($request.SourcePath) -ieq '.wim') {
                                $image = Get-WindowsImage -ImagePath $request.SourcePath -Index $request.WimIndex -ErrorAction Stop
                                $parameters.Source='Wim:' + $request.SourcePath + ':' + $request.WimIndex
                            } else { $parameters.Source=[string]$request.SourcePath }
                        }
                    }
                    default { throw 'Unsupported Windows servicing step.' }
                }
                $result = Repair-WindowsImage @parameters
                if ($null -eq $result) { throw 'DISM returned no operation result.' }
                if ($null -eq $result.PSObject.Properties['RestartNeeded']) { throw 'DISM returned no restart status.' }
                $health = if ($null -ne $result.ImageHealthState) { [string]$result.ImageHealthState } else { 'Unknown' }
                $output = ($result | Format-List * | Out-String) + ($warnings | Out-String)
                [pscustomobject]@{ Success=$true; ExitCode=0; Health=$health; RestartNeeded=[bool]$result.RestartNeeded; Output=$output } | ConvertTo-Json -Compress
            }
        } catch {
            [pscustomobject]@{ Success=$false; ExitCode=[int]$_.Exception.HResult; Health='Unknown'; RestartNeeded=$false; Output=($_ | Out-String) } | ConvertTo-Json -Compress
        }
        """;
}
