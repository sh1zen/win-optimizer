namespace WinOptimizer.Core.Features.Disks;

public enum DiskRecoveryMode { Regular, Extensive }

public sealed record DiskProcess(int Id, string Name);
public sealed record DiskShadowVersion(Guid Id, DateTime Created, bool IsDirectory);
public sealed record UsbDisk(string InstanceId, string Name, string Drives)
{
    public override string ToString() => $"{Name} ({Drives})";
}
public sealed record UsbEjectResult(bool Ejected, IReadOnlyList<DiskProcess> Blockers, string? VetoName, int VetoType, uint WindowsCode);
public sealed record DiskHash(string Value, bool? Matches);

/// <summary>Provides operations on explicitly selected local files, folders, volumes and USB disks.</summary>
public interface IDiskToolsService
{
    Task<IReadOnlyList<string>> GetVolumesAsync(bool fixedOnly, CancellationToken ct);
    Task<IReadOnlyList<DiskProcess>> GetFileLocksAsync(string path, CancellationToken ct);
    Task<bool> UnlockFileAsync(string path, CancellationToken ct);
    Task DeleteFileAsync(string path, CancellationToken ct);
    Task RepairPermissionsAsync(string path, CancellationToken ct);
    Task<IReadOnlyList<DiskShadowVersion>> GetShadowVersionsAsync(string path, CancellationToken ct);
    Task<string> RestoreShadowVersionAsync(string path, DiskShadowVersion version, string destinationFolder, CancellationToken ct);
    Task<DiskHash> HashAsync(string path, string? expected, CancellationToken ct);
    Task<IReadOnlyList<UsbDisk>> GetUsbDisksAsync(CancellationToken ct);
    Task<UsbEjectResult> EjectUsbAsync(UsbDisk disk, CancellationToken ct);
    Task<IReadOnlyList<string>> RecoverDeletedFilesAsync(string source, string destination, DiskRecoveryMode mode, string? filter, CancellationToken ct);
    Task<string> OptimizeAsync(string root, bool analyzeOnly, CancellationToken ct);
}
