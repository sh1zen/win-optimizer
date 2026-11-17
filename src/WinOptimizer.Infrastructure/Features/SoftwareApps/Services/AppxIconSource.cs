using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.Foundation;
using Windows.Management.Deployment;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;

namespace WinOptimizer.Infrastructure.Features.SoftwareApps.Services;

// Note: this file does not `using Windows.ApplicationModel;` because that namespace's
// `Package` type would clash with NuGet/MSBuild concepts elsewhere in the project. The
// `Package` references below are fully qualified for that reason.

public class AppxIconSource : IAppxIconSource
{
    private readonly ILogService logService;
    private readonly Func<PackageScope, IEnumerable<IconPackage>> _readScope;
    private readonly SemaphoreSlim _inventoryGate = new(1, 1);
    private volatile Inventory _inventory = new(new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), 0);

    internal enum PackageScope { CurrentUser, AllUsers, Provisioned }
    internal sealed record IconPackage(string Name, string FullName, Windows.ApplicationModel.Package? Package = null);
    private sealed record Inventory(Dictionary<string, string> Names,
        Dictionary<string, Windows.ApplicationModel.Package> Packages, int ScopesRead);

    private static readonly XNamespace UapNs = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
    private const int ScopeCount = 3;

    public AppxIconSource(ILogService logService) : this(logService, scope => ReadWindowsPackages(scope, logService)) { }

    internal AppxIconSource(ILogService logService, Func<PackageScope, IEnumerable<IconPackage>> readScope)
    {
        this.logService = logService;
        _readScope = readScope;
    }

    public Task<IReadOnlyDictionary<string, string>> GetInstalledPackageMapAsync(CancellationToken ct = default) =>
        ReadInventoryAsync(null, ct);

    public Task<IReadOnlyDictionary<string, string>> GetInstalledPackageMapAsync(IReadOnlySet<string> requested,
        CancellationToken ct = default) => ReadInventoryAsync(requested, ct);

    /// <summary>Fills the account and provisioned layers only as far as the requested package names require.</summary>
    private async Task<IReadOnlyDictionary<string, string>> ReadInventoryAsync(IReadOnlySet<string>? requested, CancellationToken ct)
    {
        static bool Covers(Inventory inventory, IReadOnlySet<string>? names) =>
            inventory.ScopesRead == ScopeCount || names is not null && names.All(inventory.Names.ContainsKey);

        var snapshot = _inventory;
        if (Covers(snapshot, requested)) return snapshot.Names;

        await _inventoryGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            snapshot = _inventory;
            if (Covers(snapshot, requested)) return snapshot.Names;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var names = new Dictionary<string, string>(snapshot.Names, StringComparer.OrdinalIgnoreCase);
            var packages = new Dictionary<string, Windows.ApplicationModel.Package>(snapshot.Packages, StringComparer.OrdinalIgnoreCase);
            var scope = snapshot.ScopesRead;
            try
            {
                await Task.Run(() =>
                {
                    while (scope < ScopeCount && (requested is null || !requested.All(names.ContainsKey)))
                    {
                        timeout.Token.ThrowIfCancellationRequested();
                        try
                        {
                            foreach (var package in _readScope((PackageScope)scope))
                            {
                                timeout.Token.ThrowIfCancellationRequested();
                                names.TryAdd(package.Name, package.FullName);
                                if (package.Package is not null) packages.TryAdd(package.FullName, package.Package);
                            }
                        }
                        catch (Exception ex) when (scope != 0 && ex is not OperationCanceledException)
                        { logService.LogWarning($"AppxIconSource: {(PackageScope)scope} inventory unavailable, skipping: {ex.Message}"); }
                        scope++;
                    }
                }, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                scope = ScopeCount;
                logService.LogWarning("AppxIconSource enumeration timed out after 20s — returning what was collected");
            }
            catch (Exception ex)
            {
                scope = ScopeCount;
                logService.LogWarning($"AppxIconSource enumeration failed: {ex.Message}");
            }

            _inventory = new(names, packages, scope);
            return names;
        }
        finally { _inventoryGate.Release(); }
    }

    private static IEnumerable<IconPackage> ReadWindowsPackages(PackageScope scope, ILogService log)
    {
        var manager = new PackageManager();
        var packages = scope switch
        {
            PackageScope.CurrentUser => manager.FindPackagesForUser(""),
            PackageScope.AllUsers => manager.FindPackages(),
            _ => manager.FindProvisionedPackages()
        };
        foreach (var package in packages)
        {
            IconPackage item;
            try
            {
                var id = package.Id;
                item = new(id.Name, id.FullName, package);
            }
            catch (Exception ex)
            {
                log.LogWarning($"Skipped package during icon-source enumeration: {ex.Message}");
                continue;
            }
            yield return item;
        }
    }

    public async Task<Stream?> GetLogoStreamAsync(
        string packageFullName,
        Size size,
        CancellationToken ct = default)
    {
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(TimeSpan.FromSeconds(5));

            return await Task.Run(async () =>
            {
                var pkg = ResolvePackage(packageFullName);
                if (pkg is null) return null;

                // Path 1: AppListEntry + DisplayInfo.GetLogo. Works for any package
                // with at least one registered user. Returns the locale-appropriate
                // logo at the requested size.
                var entries = await pkg.GetAppListEntriesAsync().AsTask(linkedCts.Token).ConfigureAwait(false);
                if (entries is { Count: > 0 })
                {
                    var streamRef = entries[0].DisplayInfo.GetLogo(size);
                    if (streamRef is not null)
                    {
                        return await CopyStreamRefToMemory(streamRef, linkedCts.Token).ConfigureAwait(false);
                    }
                }

                // Path 2: manifest fallback. For provisioned-only packages,
                // GetAppListEntriesAsync returns empty because no user has the
                // package registered. Read the AppxManifest.xml directly to find
                // the Square44x44Logo asset and open it as a file stream.
                return ReadLogoFromManifest(pkg);
            }, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logService.LogWarning($"AppxIconSource.GetLogoStreamAsync failed for {packageFullName}: {ex.Message}");
            return null;
        }
    }

    private Windows.ApplicationModel.Package? ResolvePackage(string packageFullName)
    {
        var snapshot = _inventory.Packages;
        if (snapshot.TryGetValue(packageFullName, out var cachedPkg))
        {
            return cachedPkg;
        }

        // Cold-path fallback when GetLogoStreamAsync is called without prior enumeration.
        try
        {
            var packageManager = new PackageManager();
            foreach (var p in packageManager.FindPackagesForUser(""))
            {
                if (string.Equals(p.Id.FullName, packageFullName, StringComparison.OrdinalIgnoreCase))
                    return p;
            }
        }
        catch { /* best-effort */ }

        return null;
    }

    private static async Task<Stream?> CopyStreamRefToMemory(
        Windows.Storage.Streams.RandomAccessStreamReference streamRef,
        CancellationToken ct)
    {
        using var randomStream = await streamRef.OpenReadAsync().AsTask(ct).ConfigureAwait(false);
        var ms = new MemoryStream();
        using (var input = randomStream.AsStreamForRead())
        {
            await input.CopyToAsync(ms, ct).ConfigureAwait(false);
        }
        ms.Position = 0;
        return ms;
    }

    private Stream? ReadLogoFromManifest(Windows.ApplicationModel.Package pkg)
    {
        try
        {
            var installedPath = pkg.InstalledPath;
            if (string.IsNullOrEmpty(installedPath) || !Directory.Exists(installedPath))
                return null;

            var manifestPath = Path.Combine(installedPath, "AppxManifest.xml");
            if (!File.Exists(manifestPath))
                return null;

            var doc = XDocument.Load(manifestPath);

            // Look for the first <uap:VisualElements> inside any <Application>.
            // Square44x44Logo is the app-list logo (smallest, least decorative padding);
            // fall back to Square150x150Logo, then Properties/Logo.
            var visual = doc.Descendants(UapNs + "VisualElements").FirstOrDefault();
            string? relativeLogo =
                (string?)visual?.Attribute("Square44x44Logo") ??
                (string?)visual?.Attribute("Square150x150Logo");

            if (string.IsNullOrEmpty(relativeLogo))
            {
                relativeLogo = doc.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "Logo")?.Value;
            }

            if (string.IsNullOrEmpty(relativeLogo))
                return null;

            var resolvedPath = ResolveScaledLogo(installedPath, relativeLogo);
            if (resolvedPath is null)
                return null;

            return new FileStream(resolvedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex)
        {
            logService.LogWarning($"AppxIconSource: manifest fallback failed for {pkg.Id.FullName}: {ex.Message}");
            return null;
        }
    }

    private static string? ResolveScaledLogo(string installedPath, string relativeLogo)
    {
        // The manifest lists a base name (e.g. "Assets\Square44x44Logo.png"). On disk
        // the actual files have scale or targetsize qualifiers in their names. Pick
        // the highest-quality variant available.
        var dir = Path.GetDirectoryName(Path.Combine(installedPath, relativeLogo));
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;

        var baseName = Path.GetFileNameWithoutExtension(relativeLogo);
        var ext = Path.GetExtension(relativeLogo);

        var candidates = new[]
        {
            $"{baseName}.scale-400{ext}",
            $"{baseName}.scale-200{ext}",
            $"{baseName}.scale-150{ext}",
            $"{baseName}.scale-125{ext}",
            $"{baseName}.scale-100{ext}",
            $"{baseName}.targetsize-256{ext}",
            $"{baseName}.targetsize-96{ext}",
            $"{baseName}.targetsize-48{ext}",
            $"{baseName}{ext}",
        };

        foreach (var candidate in candidates)
        {
            var path = Path.Combine(dir, candidate);
            if (File.Exists(path)) return path;
        }
        return null;
    }
}
