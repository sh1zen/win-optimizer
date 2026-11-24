using System.Security.Cryptography;
using System.Text.Json;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Infrastructure.Features.Common.Services;

namespace WinOptimizer.Infrastructure.Features.Drivers;

public sealed partial class WindowsDriverRemovalBackend
{
    private readonly SemaphoreSlim _driverGate = new(1, 1);
    private sealed record Evidence(string InfPath, string InfHash, Dictionary<string, string> Files);
    private static string WindowsInfDirectory => Path.Combine(Path.GetDirectoryName(Environment.SystemDirectory)!, "INF");
    private static string PnpUtil => Path.Combine(Environment.SystemDirectory, "pnputil.exe");

    /// <summary>Exports and verifies the exact package before a removal can reach PnPUtil.</summary>
    private async Task<TimeMachineUndo> ExportForRemovalAsync(string name, CancellationToken ct)
    {
        var store = timeMachine!;
        var id = Guid.NewGuid();
        var folder = store.DriverPayload(id);
        var original = Path.Combine(WindowsInfDirectory, name);
        var originalHash = Hash(original);
        AppDataStorage.ValidatePath(folder);
        Directory.CreateDirectory(folder);
        try
        {
            var result = await processes.ExecuteAsync(PnpUtil, $"/export-driver {name} \"{folder}\"", ct).ConfigureAwait(false);
            var package = ReadPackage(folder);
            var inf = package.FirstOrDefault(x => Path.GetExtension(x.Key).Equals(".inf", StringComparison.OrdinalIgnoreCase) &&
                x.Value == originalHash);
            if (!result.Succeeded || inf.Key is null || originalHash != Hash(original))
                throw new IOException($"Windows could not verify the driver export. {result.StandardError.Trim()}");
            ct.ThrowIfCancellationRequested();
            return store.PrepareUndo(new(new(TimeMachineArea.Driver, name), name,
                TimeMachineValue.From(new Evidence(inf.Key, originalHash, package))), TimeMachineValue.Missing, id);
        }
        catch
        {
            store.DiscardUndo(id);
            throw;
        }
    }

    /// <summary>Checks that the backup is intact and that Windows does not already have the package.</summary>
    internal bool CanRestoreDriver(TimeMachineUndo undo)
    {
        var evidence = DriverEvidence(undo);
        ValidateSavedPackage(undo.Id, evidence);
        return !DriverIsStaged(evidence.InfHash);
    }

    /// <summary>Re-stages one verified INF without replacing a currently bound device driver.</summary>
    internal async Task RestoreDriverAsync(TimeMachineUndo undo, CancellationToken ct)
    {
        await _driverGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var evidence = DriverEvidence(undo);
            if (DriverIsStaged(evidence.InfHash)) throw new IOException("The original driver package is already staged.");
            var inf = ValidateSavedPackage(undo.Id, evidence);
            ct.ThrowIfCancellationRequested();
            var result = await processes.ExecuteAsync(PnpUtil, $"/add-driver \"{inf}\"", CancellationToken.None).ConfigureAwait(false);
            if (result.ExitCode is not (0 or 3010) || !DriverIsStaged(evidence.InfHash))
                throw new IOException($"Windows did not verify the restored driver package. {result.StandardError.Trim()}");
            timeMachine!.Record(LogTopic.Drivers, undo.Before.Label, "Driver package restored");
        }
        finally { _driverGate.Release(); }
    }

    private string ValidateSavedPackage(Guid id, Evidence evidence)
    {
        var folder = timeMachine!.DriverPayload(id);
        var current = ReadPackage(folder);
        if (current.Count != evidence.Files.Count || evidence.Files.Any(x => current.GetValueOrDefault(x.Key) != x.Value) ||
            current.GetValueOrDefault(evidence.InfPath) != evidence.InfHash)
            throw new IOException("The saved driver package failed integrity verification.");
        return DriverScanWorkspace.ValidateChild(folder, Path.Combine(folder, evidence.InfPath));
    }

    private static Evidence DriverEvidence(TimeMachineUndo undo)
    {
        var evidence = JsonSerializer.Deserialize<Evidence>(undo.Before.Value.Json ?? "");
        if (evidence is null || string.IsNullOrWhiteSpace(evidence.InfPath) ||
            string.IsNullOrWhiteSpace(evidence.InfHash) || evidence.Files is not { Count: > 0 })
            throw new IOException("Missing driver evidence.");
        return evidence;
    }

    private static Dictionary<string, string> ReadPackage(string directory) =>
        DriverScanWorkspace.EnumerateFiles(directory, null)
            .ToDictionary(path => Path.GetRelativePath(directory, path), Hash, StringComparer.OrdinalIgnoreCase);

    private static bool DriverIsStaged(string hash) =>
        Directory.EnumerateFiles(WindowsInfDirectory, "oem*.inf").Any(path => Hash(path) == hash);

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
