using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Drivers.Models;

namespace WinOptimizer.Infrastructure.Features.Drivers;

/// <summary>Confines metadata and payload traffic to a source's distribution boundary.</summary>
internal sealed class OfficialDriverHttp(HttpClient client, string? cacheDirectory = null, TimeProvider? time = null) : IDisposable
{
    private readonly string _cache = cacheDirectory ?? AppDataPaths.DriverCatalogCache;
    private readonly TimeProvider _clock = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _maintenance = new(1);
    private DateTimeOffset _lastPrune;
    private static readonly TimeSpan Freshness = TimeSpan.FromHours(12);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(14);
    private sealed record CacheEntry(DateTimeOffset Retrieved, string? Tag, DateTimeOffset? Modified, byte[] Body);
    private sealed record CatalogEntry(DateTimeOffset Saved, DriverUpdateSearchResult Result);
    internal sealed record Metadata(byte[] Body, DateTimeOffset Retrieved, string? StaleReason = null)
    {
        public string Text => Encoding.UTF8.GetString(Body);
    }

    internal static bool Approved(DriverSources source, Uri url)
    {
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps || !url.IsDefaultPort ||
            url.UserInfo.Length != 0 || url.HostNameType != UriHostNameType.Dns) return false;
        var host = url.IdnHost;
        return source switch
        {
            DriverSources.MicrosoftCatalog => host is "www.catalog.update.microsoft.com" or "catalog.update.microsoft.com" or
                "download.windowsupdate.com" or "download.microsoft.com" || host.EndsWith(".download.windowsupdate.com", StringComparison.Ordinal) ||
                host.EndsWith(".dl.delivery.mp.microsoft.com", StringComparison.Ordinal),
            DriverSources.Nvidia => host is "www.nvidia.com" or "gfwsl.geforce.com" or "us.download.nvidia.com" or
                "international.download.nvidia.com" or "download.nvidia.com" or "www.geforce.com",
            DriverSources.Amd => host is "www.amd.com" or "drivers.amd.com" or "download.amd.com",
            DriverSources.Intel => host is "www.intel.com" or "downloadmirror.intel.com" or "downloadcenter.intel.com",
            DriverSources.Realtek => host is "www.realtek.com" or "www.realtek.com.tw",
            DriverSources.Asus => host is "www.asus.com" or "rog.asus.com" or "dlcdnets.asus.com" or "dlcdn.asus.com",
            DriverSources.Msi => host is "www.msi.com" or "download.msi.com",
            DriverSources.Gigabyte => host is "www.gigabyte.com" or "download.gigabyte.com" or
                "download1.gigabyte.com" or "download2.gigabyte.com",
            DriverSources.AsRock => host is "www.asrock.com" or "pg.asrock.com" or "download.asrock.com" or "download.asrock.com.cn",
            DriverSources.Dell => host is "www.dell.com" or "downloads.dell.com" or "dl.dell.com",
            DriverSources.Hp => host is "support.hp.com" or "hpia.hpcloud.hp.com" or "ftp.hp.com" or "ftp.ext.hp.com",
            DriverSources.Lenovo => host is "pcsupport.lenovo.com" or "download.lenovo.com" or "support.lenovo.com",
            _ => false
        };
    }

    internal async Task<HttpResponseMessage> SendAsync(DriverSources source, Uri url, CancellationToken ct,
        string? form = null, string? tag = null, DateTimeOffset? modified = null)
    {
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            if (!Approved(source, url)) throw new DriverScanException($"Unapproved {source} distribution destination: {url.Host}.");
            using var request = new HttpRequestMessage(form is null ? HttpMethod.Get : HttpMethod.Post, url);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
            request.Headers.AcceptLanguage.ParseAdd("en-US");
            if (source == DriverSources.Amd && url.Host == "drivers.amd.com") request.Headers.Referrer = new("https://www.amd.com/");
            if (source == DriverSources.MicrosoftCatalog) request.Headers.Referrer = new("https://www.catalog.update.microsoft.com/");
            if (tag is not null && EntityTagHeaderValue.TryParse(tag, out var etag)) request.Headers.IfNoneMatch.Add(etag);
            request.Headers.IfModifiedSince = modified;
            if (form is not null) request.Content = new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded");
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            using (response)
            {
                if (response.Headers.Location is not { } location)
                    throw new DriverScanException($"{source} returned a redirect without a destination.");
                if (form is not null && location.IsAbsoluteUri && !location.Host.Equals(url.Host, StringComparison.OrdinalIgnoreCase))
                    throw new DriverScanException($"{source} redirected a catalog form to a different host.");
                if ((int)response.StatusCode is 301 or 302 or 303) form = null;
                url = new Uri(url, location);
            }
        }
        throw new DriverScanException($"{source} exceeded the redirect limit.");
    }

    /// <summary>Revalidates cached request bodies and keeps bounded offline metadata.</summary>
    internal async Task<Metadata> ReadAsync(DriverSources source, Uri url, CancellationToken ct, string? form = null)
    {
        if (!Approved(source, url)) throw new DriverScanException($"Unapproved metadata URL for {source}.");
        var now = _clock.GetUtcNow();
        await PruneAsync(now, ct).ConfigureAwait(false);
        var path = CachePath($"http\n{source}\n{url}\n{form}");
        CacheEntry? entry = null;
        if (File.Exists(path) && new FileInfo(path).Length <= 48L * 1024 * 1024)
        {
            await using var input = File.OpenRead(path);
            try { entry = await JsonSerializer.DeserializeAsync<CacheEntry>(input, cancellationToken: ct).ConfigureAwait(false); }
            catch (JsonException) { /* Refresh an unreadable cache entry from its source. */ }
        }
        if (entry is not null && now - entry.Retrieved < Freshness) return new(entry.Body, entry.Retrieved);
        CacheEntry refreshed;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(35));
        try
        {
            using var response = await SendAsync(source, url, deadline.Token, form, entry?.Tag, entry?.Modified).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified && entry is not null) refreshed = entry with { Retrieved = now };
            else
            {
                response.EnsureSuccessStatusCode();
                using var buffer = new MemoryStream();
                await CopyAsync(response, buffer, 32L * 1024 * 1024, deadline.Token).ConfigureAwait(false);
                refreshed = new(now, response.Headers.ETag?.ToString(), response.Content.Headers.LastModified, buffer.ToArray());
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or OperationCanceledException)
        {
            var reason = ex is OperationCanceledException ? "The source request timed out." : ex.Message;
            if (entry is not null && now - entry.Retrieved <= Retention) return new(entry.Body, entry.Retrieved, reason);
            throw new DriverScanException($"{source}: {reason}", ex);
        }
        await WriteAtomicAsync(path, (stream, token) => JsonSerializer.SerializeAsync(stream, refreshed, cancellationToken: token), ct)
            .ConfigureAwait(false);
        return new(refreshed.Body, refreshed.Retrieved);
    }

    private string CachePath(string identity)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return DriverScanWorkspace.ValidateChild(_cache, Path.Combine(_cache, key + ".json"));
    }

    /// <summary>Keeps the most recent usable normalized snapshot for this source and machine target.</summary>
    internal async Task<DriverUpdateSearchResult?> UseCatalogAsync(DriverSources source, DriverDiscoveryContext context,
        DriverUpdateSearchResult? fresh, CancellationToken ct)
    {
        var present = context.Inventory.Devices.Where(device => device.Present)
            .Select(device => new { device.Name, device.ClassName, device.HardwareIds, device.CompatibleIds })
            .OrderBy(device => device.Name, StringComparer.Ordinal).ThenBy(device => device.HardwareIds.FirstOrDefault(), StringComparer.Ordinal);
        var target = JsonSerializer.Serialize(new { context.Inventory.System, Devices = present, Channels = source == DriverSources.Nvidia
            ? context.Options.NvidiaChannels : NvidiaChannels.Both });
        var now = _clock.GetUtcNow();
        await PruneAsync(now, ct).ConfigureAwait(false);
        var path = CachePath($"catalog-v1\n{source}\n{target}");
        if (fresh is not null)
        {
            await WriteAtomicAsync(path, (stream, token) => JsonSerializer.SerializeAsync(stream, new CatalogEntry(now, fresh), cancellationToken: token), ct)
                .ConfigureAwait(false);
            return null;
        }
        if (!File.Exists(path) || new FileInfo(path).Length > 16L * 1024 * 1024) return null;
        await using var input = File.OpenRead(path);
        var cached = await JsonSerializer.DeserializeAsync<CatalogEntry>(input, cancellationToken: ct).ConfigureAwait(false);
        return cached is not null && now - cached.Saved <= Retention ? cached.Result : null;
    }

    private async Task PruneAsync(DateTimeOffset now, CancellationToken ct)
    {
        await _maintenance.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (now - _lastPrune < TimeSpan.FromHours(1)) return;
            DriverScanWorkspace.ValidateChild(Path.GetDirectoryName(_cache)!, _cache);
            Directory.CreateDirectory(_cache);
            long held = 0;
            foreach (var candidate in new DirectoryInfo(_cache).EnumerateFiles("*.json").OrderByDescending(file => file.LastWriteTimeUtc))
            {
                ct.ThrowIfCancellationRequested();
                held += candidate.Length;
                if ((candidate.Attributes & FileAttributes.ReparsePoint) == 0 &&
                    (now - candidate.LastWriteTimeUtc > Retention || held > 256L * 1024 * 1024))
                    candidate.Delete();
            }
            _lastPrune = now;
        }
        finally { _maintenance.Release(); }
    }

    internal static async Task CopyAsync(HttpResponseMessage response, Stream output, long limit, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > limit) throw new DriverScanException("The driver response exceeds its size limit.");
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[128 * 1024];
        long size = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            size += count;
            if (size > limit) throw new DriverScanException("The driver response exceeds its size limit.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
        }
        if (response.Content.Headers.ContentLength is long expected && size != expected)
            throw new IOException("The driver response ended before its published content length.");
    }

    internal static async Task WriteAtomicAsync(string path, Func<Stream, CancellationToken, Task> write, CancellationToken ct)
    {
        var partial = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await write(output, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            File.Move(partial, path, overwrite: true);
        }
        finally { File.Delete(partial); }
    }

    public void Dispose() { client.Dispose(); _maintenance.Dispose(); }
}
