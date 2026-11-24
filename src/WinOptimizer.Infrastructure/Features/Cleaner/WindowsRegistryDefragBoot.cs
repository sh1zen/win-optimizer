using System.Security.AccessControl;
using System.Text;
using System.Text.Json;
using WinOptimizer.Core.Features.Common.Interfaces;

namespace WinOptimizer.Infrastructure.Features.Cleaner;

/// <summary>Schedules one offline maintenance boot for the reviewed hive files.</summary>
public interface IRegistryDefragBoot
{
    string? GetStatusNotice();
    Task ScheduleAsync(IReadOnlyList<string> paths, CancellationToken ct);
}

/// <summary>Passes the selected files to the elevated WinPE image and boot-sequence preparer.</summary>
public sealed class WindowsRegistryDefragBoot(ICleanerBackupVault vault, IProcessExecutor processes) : IRegistryDefragBoot
{
    private const string JobPrefix = "WinOptimizer.RegistryDefrag-";
    private static readonly JsonSerializerOptions ReceiptJson = new()
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };

    /// <summary>Reports the newest offline receipt or an interrupted maintenance run.</summary>
    public string? GetStatusNotice() => ReadStatus(Path.GetPathRoot(Environment.SystemDirectory)!, job =>
        WindowsCleanerBackupVault.ValidateAcl(
            job.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), container: false));

    /// <summary>Chooses the newest scheduled or started job, including jobs the offline worker never reached.</summary>
    internal static string? ReadStatus(string root, Action<DirectoryInfo> validateJob)
    {
        try
        {
            var job = new DirectoryInfo(root).EnumerateDirectories(JobPrefix + "*")
                .Where(directory => directory.Name.StartsWith(JobPrefix, StringComparison.OrdinalIgnoreCase) &&
                    (directory.Attributes & FileAttributes.ReparsePoint) == 0 &&
                    Guid.TryParseExact(directory.Name[JobPrefix.Length..], "N", out _) &&
                    (File.Exists(Path.Combine(directory.FullName, "scheduled")) ||
                     File.Exists(Path.Combine(directory.FullName, "started"))))
                .MaxBy(directory => directory.CreationTimeUtc);
            if (job is null) return null;
            validateJob(job);
            var started = Path.Combine(job.FullName, "started");
            if (!File.Exists(started))
            {
                CleanerPaths.ValidateDescendant(job.FullName, Path.Combine(job.FullName, "scheduled"));
                return $"Registry maintenance was scheduled, and Windows PE has not reported starting it. Job: {job.FullName}.";
            }
            CleanerPaths.ValidateDescendant(job.FullName, started);
            var receipt = Path.Combine(job.FullName, "result.json");
            if (!File.Exists(receipt))
                return $"Registry maintenance ended without a result. Review the backups in {job.FullName}.";
            CleanerPaths.ValidateDescendant(job.FullName, receipt);
            var result = JsonSerializer.Deserialize<OfflineResult>(File.ReadAllText(receipt), ReceiptJson)
                ?? throw new IOException("The maintenance result is empty.");
            if (result.Compacted < 0) throw new IOException("The maintenance result contains an invalid hive count.");
            var notice = result.State switch
            {
                "Completed" => $"Offline registry compaction complete. Hives compacted: {result.Compacted}. Backups: {job.FullName}.",
                "Failed" => $"Offline registry maintenance stopped before replacing any hives. {string.Join(" ", result.Errors)}",
                "Restored" => $"Offline registry maintenance restored the original hives after an error. {string.Join(" ", result.Errors)}",
                "RestoreFailed" => $"Offline registry recovery needs attention. Backups: {job.FullName}. {string.Join(" ", result.Errors)}",
                _ => throw new IOException("The maintenance result contains an unknown state.")
            };
            if (result.CleanupPending) return $"{notice} Windows PE did not confirm cleanup of the temporary boot entries.";
            return result.CleanupErrors is { Length: > 0 }
                ? $"{notice} Temporary boot cleanup needs attention. {string.Join(" ", result.CleanupErrors)}"
                : notice;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return $"Unable to read the offline registry maintenance result. {ex.Message}";
        }
    }

    public async Task ScheduleAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (paths.Count == 0) throw new ArgumentException("Select at least one registry hive.", nameof(paths));
        var directory = Path.Combine(vault.RootDirectory, $"prepare-registry-defrag-{Guid.NewGuid():N}");
        vault.EnsureDirectory(directory);
        var scheduler = Path.Combine(directory, "Schedule-RegistryDefrag.ps1");
        var worker = Path.Combine(directory, "Invoke-RegistryDefrag.ps1");
        var request = Path.Combine(directory, "targets.json");
        try
        {
            await File.WriteAllTextAsync(worker, ReadScript("Invoke-RegistryDefrag"), ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(scheduler, ReadScript("Schedule-RegistryDefrag"), ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(request, JsonSerializer.Serialize(paths), ct).ConfigureAwait(false);
            var eventName = $@"Local\WinOptimizer.RegistryDefrag.{Guid.NewGuid():N}";
            using var cancelEvent = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
            using var cancellation = ct.Register(() => cancelEvent.Set());
            var command = $"$ErrorActionPreference = 'Stop'; & '{Quote(scheduler)}' '{Quote(request)}' '{Quote(worker)}' '{eventName}'; exit $LASTEXITCODE";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
            var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            ct.ThrowIfCancellationRequested();
            // Allow DISM and BCDEdit to finish their cleanup if cancellation arrives during preparation.
            var result = await processes.ExecuteAsync(powershell,
                $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}").ConfigureAwait(false);
            if (result.ExitCode == 2) ct.ThrowIfCancellationRequested();
            if (!result.Succeeded)
                throw new IOException($"Unable to schedule offline registry compaction. {result.StandardOutput.Trim()} {result.StandardError.Trim()}".Trim());
        }
        finally
        {
            File.Delete(worker);
            File.Delete(scheduler);
            File.Delete(request);
            Directory.Delete(directory);
        }
    }

    private static string Quote(string value) => value.Replace("'", "''");

    private static string ReadScript(string name)
    {
        using var stream = typeof(WindowsRegistryDefragBoot).Assembly
            .GetManifestResourceStream($"WinOptimizer.Cleaner.{name}.ps1")
            ?? throw new IOException($"The registry maintenance script {name} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed record OfflineResult(string State, int Compacted, string[] Errors, bool CleanupPending = false,
        string[]? CleanupErrors = null);
}
