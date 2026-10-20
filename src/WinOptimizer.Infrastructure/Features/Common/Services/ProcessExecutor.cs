using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Infrastructure.Features.Common.Services;

/// <summary>
/// Default implementation of <see cref="IProcessExecutor"/> that wraps <see cref="System.Diagnostics.Process"/>.
/// </summary>
public class ProcessExecutor : IProcessExecutor
{
    public async Task<ProcessExecutionResult> ExecuteAsync(
        string fileName,
        string arguments,
        CancellationToken ct = default)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        process.Start();

        // Read stdout and stderr concurrently to avoid deadlocks
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        return new ProcessExecutionResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = stdoutTask.Result,
            StandardError = stderrTask.Result
        };
    }

    public async Task<ProcessExecutionResult> ExecuteWithStreamingAsync(
        string fileName,
        string arguments,
        Action<string>? onOutputLine = null,
        Action<string>? onErrorLine = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        process.Start();

        var readOutput = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                stdout.AppendLine(line);
                onOutputLine?.Invoke(line);
            }
        }, CancellationToken.None);

        var readError = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                stderr.AppendLine(line);
                onErrorLine?.Invoke(line);
            }
        }, CancellationToken.None);

        var completion = Task.WhenAll(readOutput, readError, process.WaitForExitAsync(CancellationToken.None));
        try
        {
            await completion.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stop owned children too (for example SFC under PowerShell), then drain output before releasing the task.
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { /* The process exited during cancellation. */ }
            await completion.ConfigureAwait(false);
            throw;
        }
        ct.ThrowIfCancellationRequested();

        return new ProcessExecutionResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = stdout.ToString(),
            StandardError = stderr.ToString()
        };
    }

    public void KillProcessesByName(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        foreach (var process in processes)
        {
            try
            {
                process.Kill();
                process.WaitForExit(5000);
            }
            catch
            {
                // Best-effort kill — process may have already exited
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    public async Task<int?> ShellExecuteAsync(
        string fileName,
        string? arguments = null,
        bool waitForExit = false,
        CancellationToken ct = default)
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments ?? string.Empty,
            UseShellExecute = true
        });

        if (process == null)
            return null;

        if (waitForExit)
        {
            try
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                return process.ExitCode;
            }
            finally
            {
                process.Dispose();
            }
        }

        // Fire-and-forget: dispose immediately to release the native handle.
        // The child process continues running independently.
        process.Dispose();
        return 0;
    }
}
