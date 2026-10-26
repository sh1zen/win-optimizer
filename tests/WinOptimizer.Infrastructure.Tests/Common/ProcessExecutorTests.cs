using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using WinOptimizer.Infrastructure.Features.Common.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Common;

/// <summary>Verifies the real process boundary using isolated PowerShell workers, without running system tools.</summary>
public sealed class ProcessExecutorTests
{
    private static readonly string PowerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    [Fact]
    public async Task PrecancelledExecutionDoesNotStartAProcess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessExecutor()
            .ExecuteWithStreamingAsync("nonexistent-test-worker.exe", "", ct: cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessExecutor()
            .ExecuteAsync("nonexistent-test-worker.exe", "", cancellation.Token));
    }

    [Fact]
    public async Task BufferedExecutionPreservesUnterminatedOutputAndStopsItsWorkerOnCancellation()
    {
        var executor = new ProcessExecutor();
        var result = await executor.ExecuteAsync(PowerShell, Arguments("[Console]::Out.Write('output'); [Console]::Error.Write('error'); exit 7"));
        Assert.Equal("output", result.StandardOutput);
        Assert.Equal("error", result.StandardError);
        Assert.Equal(7, result.ExitCode);

        var root = Path.Combine(Path.GetTempPath(), "WinOptimizer-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var marker = Path.Combine(root, "pid.txt");
        using var cancellation = new CancellationTokenSource();
        // Publish readiness only after the PID writer has closed the file.
        var pending = executor.ExecuteAsync(PowerShell, Arguments(
            $"$marker = '{marker.Replace("'", "''")}'; [IO.File]::WriteAllText($marker + '.tmp', [string]$PID); " +
            "[IO.File]::Move($marker + '.tmp', $marker); while ($true) { Start-Sleep -Seconds 60 }"), cancellation.Token);
        Process? worker = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!File.Exists(marker)) await Task.Delay(25, timeout.Token);
            worker = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(marker), CultureInfo.InvariantCulture));
            _ = worker.Handle;
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(worker.HasExited);
        }
        finally
        {
            cancellation.Cancel();
            if (worker is not null)
            {
                if (!worker.HasExited) worker.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync();
                worker.Dispose();
            }
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StreamingPreservesOutputCallbacksAndExitCode()
    {
        var output = new List<string>();
        var errors = new List<string>();
        var result = await new ProcessExecutor().ExecuteWithStreamingAsync(PowerShell,
            Arguments("[Console]::Out.WriteLine('output'); [Console]::Error.WriteLine('error'); exit 7"), output.Add, errors.Add);
        Assert.Equal(7, result.ExitCode);
        Assert.False(result.Succeeded);
        Assert.Equal("output" + Environment.NewLine, result.StandardOutput);
        Assert.Equal("error" + Environment.NewLine, result.StandardError);
        Assert.Equal(new[] { "output" }, output);
        Assert.Equal(new[] { "error" }, errors);
    }

    [Fact]
    public async Task CancellationStopsTheOwnedParentAndChildPromptly()
    {
        using var cancellation = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = new ConcurrentDictionary<string, Process>();
        // The parent waits on a child with redirected output, like the servicing SFC wrapper.
        var childArguments = Arguments("[Console]::Out.WriteLine('READY'); while ($true) { Start-Sleep -Seconds 60 }");
        var script = """
            [Console]::Out.WriteLine('PARENT:' + $PID)
            $start = [Diagnostics.ProcessStartInfo]::new()
            $start.FileName = (Get-Process -Id $PID).Path
            $start.Arguments = '__CHILD_ARGUMENTS__'
            $start.UseShellExecute = $false
            $start.CreateNoWindow = $true
            $start.RedirectStandardOutput = $true
            $start.RedirectStandardError = $true
            $child = [Diagnostics.Process]::Start($start)
            [Console]::Out.WriteLine('CHILD:' + $child.Id)
            [Console]::Out.WriteLine($child.StandardOutput.ReadLine())
            $child.WaitForExit()
            """.Replace("__CHILD_ARGUMENTS__", childArguments);
        var pending = new ProcessExecutor().ExecuteWithStreamingAsync(PowerShell, Arguments(script), line =>
        {
            if (line.StartsWith("PARENT:", StringComparison.Ordinal) || line.StartsWith("CHILD:", StringComparison.Ordinal))
            {
                var parts = line.Split(':');
                var worker = Process.GetProcessById(int.Parse(parts[1], CultureInfo.InvariantCulture));
                _ = worker.Handle; // Retain a handle before cancellation, so exit can be observed after termination.
                workers[parts[0]] = worker;
            }
            else if (line == "READY") ready.TrySetResult();
        }, ct: cancellation.Token);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(2, workers.Count);
            var elapsed = Stopwatch.StartNew();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));
            foreach (var worker in workers.Values)
                await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(3), $"Cancellation took {elapsed.Elapsed}.");
        }
        finally
        {
            cancellation.Cancel();
            // Only terminate processes started by this fixture; never use process-name cleanup.
            foreach (var worker in workers.Values)
            {
                try
                {
                    if (!worker.HasExited) worker.Kill(entireProcessTree: true);
                    await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally { worker.Dispose(); }
            }
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
    }

    /// <summary>Encodes fixed fixture scripts as Unicode, matching Windows PowerShell's command contract.</summary>
    private static string Arguments(string script) => "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
}
