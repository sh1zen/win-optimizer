using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Disks;

namespace WinOptimizer.Infrastructure.Features.Disks;

/// <summary>Owns Windows disk operations and moves native queries and filesystem scans off the UI thread.</summary>
internal sealed class WindowsDiskToolsService(
    IProcessExecutor processes, ShadowVersions shadowVersions, DiskFileDeletion files) : IDiskToolsService
{
    public Task<IReadOnlyList<string>> GetVolumesAsync(bool fixedOnly, CancellationToken ct) =>
        Task.Run<IReadOnlyList<string>>(() => DriveInfo.GetDrives()
            .Where(drive => (fixedOnly ? drive.DriveType == DriveType.Fixed : DiskPaths.IsLocal(drive)) && drive.IsReady)
            .Select(drive => drive.Name).ToArray(), ct);

    public Task<IReadOnlyList<DiskProcess>> GetFileLocksAsync(string path, CancellationToken ct) =>
        Task.Run(() => files.GetLocks(path), ct);

    public Task<bool> UnlockFileAsync(string path, CancellationToken ct) =>
        Task.Run(() => files.Unlock(path, ct), ct);

    public Task DeleteFileAsync(string path, CancellationToken ct) =>
        Task.Run(() => files.Delete(path, ct), ct);

    public Task RepairPermissionsAsync(string input, CancellationToken ct) => Task.Run(async () =>
    {
        var path = DiskPaths.LocalPath(input);
        var root = DiskPaths.Drive(Path.GetPathRoot(path)!).Name;
        DiskPaths.CheckTree(path, root);
        var result = await processes.ExecuteAsync(Path.Combine(Environment.SystemDirectory, "icacls.exe"),
            $"\"{path}\" /reset /L /Q", ct).ConfigureAwait(false);
        if (!result.Succeeded)
            throw new IOException($"Windows could not reset inherited permissions ({result.ExitCode}). {result.StandardError} {result.StandardOutput}".Trim());
    }, ct);

    public Task<IReadOnlyList<DiskShadowVersion>> GetShadowVersionsAsync(string path, CancellationToken ct) =>
        Task.Run(() => shadowVersions.Find(path, ct), ct);

    public Task<string> RestoreShadowVersionAsync(string path, DiskShadowVersion version, string destinationFolder, CancellationToken ct) =>
        Task.Run(() => shadowVersions.Restore(path, version, destinationFolder, ct), ct);

    public Task<DiskHash> HashAsync(string path, string? expected, CancellationToken ct) =>
        Task.Run(() => DiskHashes.ComputeAsync(path, expected, ct), ct);

    public Task<IReadOnlyList<UsbDisk>> GetUsbDisksAsync(CancellationToken ct) =>
        Task.Run(() => UsbDiskEjection.List(ct), ct);

    public Task<UsbEjectResult> EjectUsbAsync(UsbDisk disk, CancellationToken ct) =>
        Task.Run(() => UsbDiskEjection.EjectAsync(disk, ct), ct);

    public Task<IReadOnlyList<string>> RecoverDeletedFilesAsync(string source, string destination, DiskRecoveryMode mode,
        string? filter, CancellationToken ct) =>
        Task.Run(() => DiskFileRecovery.RunAsync(processes, source, destination, mode, filter, ct), ct);

    public Task<string> OptimizeAsync(string root, bool analyzeOnly, CancellationToken ct) => Task.Run(async () =>
    {
        var result = await VolumeOptimization.RunAsync(processes, root, analyzeOnly, ct).ConfigureAwait(false);
        if (!result.Succeeded)
            throw new IOException($"Windows disk optimization exited with code {result.ExitCode}. {result.StandardError} {result.StandardOutput}".Trim());
        return result.StandardOutput;
    }, ct);
}
