using System.IO.Compression;
using WinOptimizer.Core.Features.Drivers.Models;

namespace WinOptimizer.Infrastructure.Features.Drivers;

/// <summary>Inspects all INF/catalog pairs in a package, preserving components even when only one drives the target device.</summary>
internal sealed class DriverPackageInspector(IDriverInfInspector inspector)
{
    internal sealed record Evidence(DriverComponent[] Components, bool Unsupported, bool TrustedExecutable);

    internal async Task<Evidence> InspectAsync(string[] originals, string root, CancellationToken ct,
        IProgress<DriverScanProgress>? progress = null)
    {
        var expandedCount = 0;
        var budget = new DriverArchive.Budget();
        var unsupported = false;
        var trustedExecutable = false;
        var extracting = false;
        foreach (var path in originals)
        {
            var extension = Path.GetExtension(path);
            if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) || extension.Equals(".msi", StringComparison.OrdinalIgnoreCase))
            {
                trustedExecutable = await Task.Run(() => DriverPayloadIntegrity.VerifyExecutableSignature(path), ct).ConfigureAwait(false);
                if (!trustedExecutable) throw new DriverScanException("The installer does not have a valid Authenticode signature.");
                // Some signed executables carry a ZIP central directory. Opening it reads bytes only.
                if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    BeginExtraction();
                    if (IsZip(path)) await ExpandAsync(path, ".zip", 0, ct).ConfigureAwait(false);
                    else
                    {
                        var stream = Path.Combine(root, $"embedded-{Guid.NewGuid():N}.7z");
                        if (await DriverArchive.ExtractSevenZipOverlayAsync(path, stream, ct).ConfigureAwait(false))
                            await ExpandAsync(stream, ".7z", 0, ct).ConfigureAwait(false);
                        else unsupported = true;
                    }
                }
                else unsupported = true;
            }
            else if (IsArchive(extension))
            {
                BeginExtraction();
                await ExpandAsync(path, extension, 0, ct).ConfigureAwait(false);
            }
        }

        var paths = DriverScanWorkspace.EnumerateFiles(root, ".inf").ToArray();
        if (paths.Length > 0) progress?.Report(new("Drivers_VerifyingInfs"));
        var components = await inspector.InspectAsync(root, paths, ct).ConfigureAwait(false);
        return new(components, unsupported, trustedExecutable);

        void BeginExtraction()
        {
            if (extracting) return;
            extracting = true;
            progress?.Report(new("Drivers_ExtractingPackage"));
        }

        async Task ExpandAsync(string path, string extension, int depth, CancellationToken token)
        {
            if (++expandedCount > 12 || depth > 1)
            {
                unsupported = true;
                return;
            }
            var target = DriverScanWorkspace.ValidateChild(root, Path.Combine(root, $"archive-{expandedCount}"));
            Directory.CreateDirectory(target);
            if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                await DriverArchive.ExtractZipAsync(path, target, token, budget).ConfigureAwait(false);
            else if (extension.Equals(".7z", StringComparison.OrdinalIgnoreCase))
                await DriverArchive.ExtractSevenZipAsync(path, target, token, budget).ConfigureAwait(false);
            else await DriverArchive.ExtractCabAsync(path, target, token, budget).ConfigureAwait(false);
            foreach (var nested in DriverScanWorkspace.EnumerateFiles(target, null).Where(file => IsArchive(Path.GetExtension(file))).ToArray())
                await ExpandAsync(nested, Path.GetExtension(nested), depth + 1, token).ConfigureAwait(false);
        }
    }

    private static bool IsArchive(string extension) => extension.Equals(".cab", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) || extension.Equals(".7z", StringComparison.OrdinalIgnoreCase);

    private static bool IsZip(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            return archive.Entries.Count > 0;
        }
        catch (InvalidDataException) { return false; }
    }
}
