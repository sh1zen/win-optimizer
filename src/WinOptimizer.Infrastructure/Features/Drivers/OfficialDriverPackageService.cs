using System.ComponentModel;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Drivers.Interfaces;
using WinOptimizer.Core.Features.Drivers.Models;
using WinOptimizer.Core.Features.Drivers.Services;

namespace WinOptimizer.Infrastructure.Features.Drivers;

/// <summary>Acquires official packages, verifies their bytes, and publishes a complete selection without installation.</summary>
internal sealed class OfficialDriverPackageService(OfficialDriverHttp http, WindowsDriverUpdateSource windowsUpdate, IDriverInfInspector inspector,
    string? cacheDirectory = null, string? downloadDirectory = null, string? temporaryDirectory = null) : IDriverPackageService
{
    private const long MaxPackageBytes = 2L * 1024 * 1024 * 1024;
    private const long MaxAutomaticBytes = 256L * 1024 * 1024;
    private readonly string _cache = cacheDirectory ?? AppDataPaths.DriverPayloadCache;
    private readonly string _downloads = downloadDirectory ?? AppDataPaths.DriverDownloads;
    private readonly DriverPackageInspector _inspector = new(inspector);
    private readonly SemaphoreSlim _operations = new(1);
    private static readonly TimeSpan CacheAge = TimeSpan.FromDays(7);
    private DateTimeOffset _lastPrune;

    private sealed record FileEvidence(string Path, string Hash, string Key, DriverPayload Payload);

    public async Task<DriverPackageResult> AcquireAsync(DriverUpdate update, DriverInventory inventory, bool download,
        IProgress<DriverScanProgress>? progress, CancellationToken ct, bool licenseAccepted = false)
    {
        var catalog = update.Catalog ?? throw new DriverScanException("The selected record has no official package identity.");
        var reference = catalog.References.First();
        if (catalog.LicenseRequired && (!licenseAccepted || reference.Source == DriverSources.WindowsUpdate))
            throw new DriverScanException("Review the license on the official support page before acquiring this package.");
        if (!DriverCatalogMatching.Targets(catalog.Target, inventory.System))
            throw new DriverScanException("The package is outside its declared Windows or OEM target.");

        await _operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromMinutes(12));
            using var workspace = new DriverScanWorkspace(temporaryDirectory);
            var token = deadline.Token;
            if (reference.Source == DriverSources.WindowsUpdate)
            {
                progress?.Report(new("Drivers_Downloading"));
                var native = await windowsUpdate.AcquireAsync(update, inventory, workspace, token).ConfigureAwait(false);
                if (native.Update.Catalog?.Verification == DriverVerification.Failed && download)
                    throw new DriverScanException("The Windows Update package could not be inspected.");
                return new(native.Update, download ? await PublishAsync(native.Root, native.Files, reference.Source, token).ConfigureAwait(false) : null);
            }

            if (catalog.Payloads.Length is 0 or > 16)
                throw new DriverScanException("This listing has no supported automatic acquisition path. Use the official support page.");
            EnsureCache();
            var files = new List<FileEvidence>(catalog.Payloads.Length);
            var payloadRoot = Path.Combine(workspace.Root, "payloads");
            Directory.CreateDirectory(payloadRoot);
            var remaining = download || progress is not null ? MaxPackageBytes : MaxAutomaticBytes;
            for (var index = 0; index < catalog.Payloads.Length; index++)
            {
                progress?.Report(new("Drivers_Downloading", index, catalog.Payloads.Length));
                var payload = catalog.Payloads[index];
                var part = Path.Combine(payloadRoot, index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Directory.CreateDirectory(part);
                var file = await FetchAsync(reference, payload, part, remaining, token).ConfigureAwait(false);
                remaining -= new FileInfo(file.Path).Length;
                files.Add(file);
            }

            progress?.Report(new("Drivers_VerifyingPackages"));
            var originals = files.Select(file => file.Path).ToArray();
            var evidence = await _inspector.InspectAsync(originals, workspace.Root, token).ConfigureAwait(false);
            var signature = evidence.TrustedExecutable || evidence.Components.Any(component => component.SignatureValid && component.CatalogSha256.Length > 0);
            var verification = evidence.Unsupported ? DriverVerification.Unsupported :
                evidence.Components.Length > 0 && signature ? DriverVerification.Verified : DriverVerification.Failed;
            var hashes = files.Select(file => file.Hash).ToArray();
            var inspected = update with { Catalog = catalog with
            {
                Verification = verification,
                VerificationDetail = verification switch
                {
                    DriverVerification.Unsupported => "At least one installer or nested archive cannot be inspected without executing it.",
                    DriverVerification.Failed => "No locally verifiable INF and accompanying driver catalog were found.",
                    _ => ""
                },
                ContentSha256 = hashes,
                PayloadSignatureValid = evidence.TrustedExecutable,
                Components = evidence.Components
            } };
            var match = DriverCatalogMatching.Evaluate(inspected, inventory);

            // The matching INF signature does not authenticate other files in an unsigned archive.
            var authenticated = files.All(file => file.Payload.Hashes.Length > 0 ||
                Path.GetExtension(file.Path).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(file.Path).Equals(".msi", StringComparison.OrdinalIgnoreCase));
            if (authenticated && (verification is DriverVerification.Verified or DriverVerification.Unsupported))
                foreach (var file in files) await CacheAsync(file, token).ConfigureAwait(false);
            if (!download) return new(match.Update);
            if (!authenticated || verification == DriverVerification.Failed)
                throw new DriverScanException(inspected.Catalog!.VerificationDetail.Length > 0 ? inspected.Catalog.VerificationDetail :
                    "The source did not supply enough integrity evidence to publish this download.");
            if (match.Applicability == DriverApplicability.Incompatible)
                throw new DriverScanException("The downloaded package does not match the declared target or detected devices.");
            return new(match.Update, await PublishAsync(payloadRoot, originals, reference.Source, token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new DriverScanException("The driver package operation timed out."); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or Win32Exception or InvalidDataException)
        { throw new DriverScanException($"{reference.Source}: {ex.Message}", ex); }
        finally { _operations.Release(); }
    }

    private async Task<FileEvidence> FetchAsync(DriverSourceReference reference, DriverPayload payload, string destination, long limit, CancellationToken ct)
    {
        if (!OfficialDriverHttp.Approved(reference.Source, payload.Url))
            throw new DriverScanException("The package distribution host is outside the official source boundary.");
        var name = Uri.UnescapeDataString(payload.Url.Segments[^1]);
        var path = DriverArchive.EntryPath(destination, name);
        if (!Path.GetDirectoryName(path)!.Equals(destination, StringComparison.OrdinalIgnoreCase))
            throw new DriverScanException("The official payload URL has an unsafe filename.");
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".cab" or ".zip" or ".7z" or ".exe" or ".msi"))
            throw new DriverScanException($"Unsupported driver package format: {extension}.");
        if (limit <= 0) throw new DriverScanException("The driver package exceeds the operation's download limit.");

        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{reference.Source}\\n{reference.Id}\\n{reference.Revision}\\n{payload.Url}")));
        var alias = CachePath(key + ".ref");
        string? cachedHash = null;
        if (File.Exists(alias) && new FileInfo(alias) is { Length: 64 } aliasInfo && DateTime.UtcNow - aliasInfo.LastWriteTimeUtc <= CacheAge)
            cachedHash = await File.ReadAllTextAsync(alias, ct).ConfigureAwait(false);
        if (cachedHash is not null && cachedHash.Length == 64 && cachedHash.All(Uri.IsHexDigit))
        {
            var cacheFile = CachePath(cachedHash + ".pkg");
            if (File.Exists(cacheFile) && new FileInfo(cacheFile).Length <= limit)
            {
                try
                {
                    await CopyFileAsync(cacheFile, path, ct).ConfigureAwait(false);
                    var expected = payload.Hashes.Append(new("SHA256", cachedHash)).ToArray();
                    return new(path, await CheckFileAsync(path, expected, extension, ct).ConfigureAwait(false), key, payload);
                }
                catch (DriverScanException)
                {
                    File.Delete(path);
                    File.Delete(alias);
                    File.Delete(cacheFile);
                }
            }
        }
        using var response = await http.SendAsync(reference.Source, payload.Url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType is MediaTypeHeaderValue type &&
            (type.MediaType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true || type.MediaType == "application/json"))
            throw new DriverScanException("The source returned an interactive page instead of the package. Open its official support page.");
        await OfficialDriverHttp.WriteAtomicAsync(path, (stream, token) => OfficialDriverHttp.CopyAsync(response, stream, limit, token), ct)
            .ConfigureAwait(false);
        return new(path, await CheckFileAsync(path, payload.Hashes, extension, ct).ConfigureAwait(false), key, payload);
    }

    private static async Task<string> CheckFileAsync(string path, DriverHash[] hashes, string extension, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var header = new byte[8];
        if (await stream.ReadAtLeastAsync(header, header.Length, false, ct).ConfigureAwait(false) != header.Length ||
            !ValidHeader(header, extension))
            throw new DriverScanException("The downloaded file does not have the expected driver package format.");
        stream.Position = 0;
        return await DriverPayloadIntegrity.CheckAsync(stream, hashes, ct).ConfigureAwait(false);
    }

    private static bool ValidHeader(ReadOnlySpan<byte> bytes, string extension) => extension switch
    {
        ".cab" => bytes.StartsWith("MSCF"u8),
        ".zip" => bytes.StartsWith<byte>([0x50, 0x4B, 0x03, 0x04]),
        ".7z" => bytes.StartsWith<byte>([0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]),
        ".exe" => bytes.StartsWith("MZ"u8),
        ".msi" => bytes.SequenceEqual<byte>([0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]),
        _ => false
    };

    private void EnsureCache()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastPrune < TimeSpan.FromHours(1)) return;
        DriverScanWorkspace.ValidateChild(Path.GetDirectoryName(_cache)!, _cache);
        Directory.CreateDirectory(_cache);
        long held = 0;
        foreach (var file in new DirectoryInfo(_cache).EnumerateFiles().OrderByDescending(file => file.LastWriteTimeUtc))
        {
            if (file.Extension is not (".pkg" or ".ref") || (file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            held += file.Length;
            if (now - file.LastWriteTimeUtc > CacheAge || held > 4L * 1024 * 1024 * 1024) file.Delete();
        }
        _lastPrune = now;
    }

    private string CachePath(string name) => DriverScanWorkspace.ValidateChild(_cache, Path.Combine(_cache, name));

    private async Task CacheAsync(FileEvidence file, CancellationToken ct)
    {
        var path = CachePath(file.Hash + ".pkg");
        if (!File.Exists(path)) await CopyFileAsync(file.Path, path, ct).ConfigureAwait(false);
        var alias = CachePath(file.Key + ".ref");
        await OfficialDriverHttp.WriteAtomicAsync(alias, (output, token) => output.WriteAsync(Encoding.ASCII.GetBytes(file.Hash), token).AsTask(), ct)
            .ConfigureAwait(false);
    }

    private async Task<string> PublishAsync(string root, string[] originals, DriverSources source, CancellationToken ct)
    {
        if (originals.Length == 0) throw new DriverScanException("No complete driver package files are available.");
        DriverScanWorkspace.ValidateChild(Path.GetDirectoryName(_downloads)!, _downloads);
        Directory.CreateDirectory(_downloads);
        var name = $"{source}-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid():N}";
        var target = DriverScanWorkspace.ValidateChild(_downloads, Path.Combine(_downloads, name));
        var staging = DriverScanWorkspace.ValidateChild(_downloads, target + ".part");
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var file in originals)
            {
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(root, DriverScanWorkspace.ValidateChild(root, file));
                var path = DriverArchive.EntryPath(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await CopyFileAsync(file, path, ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            Directory.Move(staging, target);
            return target;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(DriverScanWorkspace.ValidateChild(_downloads, staging), recursive: true);
        }
    }

    private static Task CopyFileAsync(string input, string destination, CancellationToken ct) =>
        OfficialDriverHttp.WriteAtomicAsync(destination, async (output, token) =>
        {
            await using var source = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            await source.CopyToAsync(output, token).ConfigureAwait(false);
        }, ct);
}
