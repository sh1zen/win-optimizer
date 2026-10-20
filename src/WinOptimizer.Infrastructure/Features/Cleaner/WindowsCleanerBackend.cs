using WinOptimizer.Core.Features.Cleaner.Interfaces;
using WinOptimizer.Core.Features.Cleaner.Models;
using WinOptimizer.Core.Features.Common.Interfaces;

namespace WinOptimizer.Infrastructure.Features.Cleaner;

/// <summary>Dispatches cleanup to the component that owns each Windows resource.</summary>
public sealed class WindowsCleanerBackend(CleanerFileScanner files, CleanerRegistryScanner registry,
    RegistryHiveCompactor compactor, IProcessExecutor processes) : ICleanerBackend
{
    public string BackupDirectory => registry.BackupDirectory;
    public Task<IReadOnlyList<CleanerApplication>> GetApplicationsAsync(CancellationToken ct) => files.GetApplicationsAsync(ct);
    public Task<CleanerApplicationInventory> GetApplicationsAsync(CleanerOptions options, CancellationToken ct) => files.GetApplicationsAsync(options, ct);
    public Task<IReadOnlyList<CleanerRegistryAccount>> GetRegistryAccountsAsync(CancellationToken ct) =>
        Task.Run(registry.GetAccounts, ct);
    public Task<IReadOnlyList<string>> GetDrivesAsync(CancellationToken ct) => Task.Run<IReadOnlyList<string>>(
        () => DriveInfo.GetDrives().Where(x => x.IsReady && x.DriveType == DriveType.Fixed).Select(x => x.RootDirectory.FullName).ToArray(), ct);

    public async Task<CleanerScanResult> ScanAsync(CleanerArea area, CleanerOptions options, IProgress<CleanerProgress>? progress, CancellationToken ct)
    {
        options.Validate();
        switch (area)
        {
            case CleanerArea.Privacy:
                var privacyFiles = await files.ScanAsync(area, options, ct,
                    progress is null ? null : new StageProgress(progress, 1)).ConfigureAwait(false);
                var privacyRegistry = await Task.Run(() => registry.ScanPrivacy(options, ct,
                    progress is null ? null : new StageProgress(progress, 2)), ct).ConfigureAwait(false);
                return new(privacyFiles.Findings.Concat(privacyRegistry.Findings).ToArray(),
                    privacyFiles.Notices.Concat(privacyRegistry.Notices).ToArray())
                    { CheckedItems = privacyFiles.CheckedItems + privacyRegistry.CheckedItems };
            case CleanerArea.JunkFiles:
            case CleanerArea.Shortcuts:
                return await files.ScanAsync(area, options, ct, progress).ConfigureAwait(false);
            case CleanerArea.Registry:
                return await Task.Run(() => registry.Scan(options, ct, progress), ct).ConfigureAwait(false);
            case CleanerArea.Disk:
                ValidateDrive(options.DiskRoot);
                var analysis = await processes.ExecuteAsync(SystemTool("defrag.exe"), $"{options.DiskRoot[..2]} /A /V", ct).ConfigureAwait(false);
                if (!analysis.Succeeded) return new([], [$"{options.DiskRoot}: {analysis.StandardError} {analysis.StandardOutput}"]);
                return new([new(Guid.NewGuid(), area, "Cleaner_Disk", analysis.StandardOutput,
                    0, true, new CleanerDiskTarget(options.DiskRoot))], []);
            case CleanerArea.RegistryDefrag:
                return await Task.Run(() => compactor.Scan(options, ct, progress), ct).ConfigureAwait(false);
            default:
                throw new ArgumentOutOfRangeException(nameof(area));
        }
    }

    public async Task CleanAsync(CleanerFinding finding, CleanerOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        switch (finding.Target)
        {
            case CleanerFileTarget file:
                await Task.Run(() => files.Clean(file), ct).ConfigureAwait(false);
                break;
            case CleanerRegistryTarget value:
                await registry.CleanAsync(value, ct).ConfigureAwait(false);
                break;
            case CleanerDiskTarget disk:
                ValidateDrive(disk.Root);
                var result = await processes.ExecuteAsync(SystemTool("defrag.exe"), $"{disk.Root[..2]} /O /U /V", ct).ConfigureAwait(false);
                if (!result.Succeeded) throw new IOException($"Disk optimization failed: {result.StandardError} {result.StandardOutput}");
                break;
            case CleanerHiveTarget hive:
                await Task.Run(() => compactor.Clean(hive, ct, options.BackupBeforeRegistryDefrag), ct).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException("Unsupported cleanup target.");
        }
    }

    internal static void ValidateDrive(string root)
    {
        if (root.Length != 3 || !char.IsAsciiLetter(root[0]) || root[1..] != ":\\")
            throw new ArgumentException("Select a local fixed drive.", nameof(root));
        var drive = new DriveInfo(root);
        if (!drive.IsReady || drive.DriveType != DriveType.Fixed) throw new IOException("The selected fixed drive is unavailable.");
    }

    private static string SystemTool(string name) => Path.Combine(Environment.SystemDirectory, name);

    /// <summary>Maps file and registry discovery into one privacy stage without posting asynchronous late callbacks.</summary>
    private sealed class StageProgress(IProgress<CleanerProgress> progress, int stage) : IProgress<CleanerProgress>
    {
        public void Report(CleanerProgress value) => progress.Report(value with { Stage = stage, Stages = 2 });
    }
}
