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
        => await ExecuteCapturedAsync(new ProcessStartInfo { FileName = fileName, Arguments = arguments }, null, ct)
            .ConfigureAwait(false);

    /// <summary>Executes a local tool with structured arguments and bounded diagnostic capture.</summary>
    public Task<ProcessExecutionResult> ExecuteAsync(string fileName, IReadOnlyList<string> arguments,
        string workingDirectory, int captureLimit, CancellationToken ct)
    {
        var start = new ProcessStartInfo { FileName = fileName, WorkingDirectory = workingDirectory };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return ExecuteCapturedAsync(start, captureLimit, ct);
    }

    private static async Task<ProcessExecutionResult> ExecuteCapturedAsync(ProcessStartInfo start, int? captureLimit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;
        using var process = new Process { StartInfo = start };

        process.Start();

        // Read stdout and stderr concurrently to avoid deadlocks
        var stdoutTask = CaptureAsync(process.StandardOutput, captureLimit);
        var stderrTask = CaptureAsync(process.StandardError, captureLimit);

        await WaitForOwnedProcessAsync(process,
            Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync(CancellationToken.None)), ct).ConfigureAwait(false);

        return new ProcessExecutionResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = stdoutTask.Result,
            StandardError = stderrTask.Result
        };
    }

    private static async Task<string> CaptureAsync(System.IO.StreamReader reader, int? limit)
    {
        if (limit is null) return await reader.ReadToEndAsync(CancellationToken.None).ConfigureAwait(false);
        var captured = new StringBuilder();
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false) is var count && count != 0)
            captured.Append(buffer, 0, Math.Min(count, limit.Value - captured.Length));
        return captured.ToString();
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

        await WaitForOwnedProcessAsync(process,
            Task.WhenAll(readOutput, readError, process.WaitForExitAsync(CancellationToken.None)), ct).ConfigureAwait(false);

        return new ProcessExecutionResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = stdout.ToString(),
            StandardError = stderr.ToString()
        };
    }

    /// <summary>Stops only the owned process tree and drains its pipes before releasing a cancelled operation.</summary>
    private static async Task WaitForOwnedProcessAsync(Process process, Task completion, CancellationToken ct)
    {
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
