using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using WinOptimizer.Core.Features.Drivers.Interfaces;
using WinOptimizer.Core.Features.Drivers.Models;

namespace WinOptimizer.Infrastructure.Features.Drivers;

/// <summary>Collects one official source while tracking stale metadata and independently recoverable requests.</summary>
internal abstract class OfficialDriverSource(OfficialDriverHttp http, DriverSources source, string? temporaryDirectory = null) : IDriverUpdateSource
{
    public DriverSources Source => source;
    protected abstract Uri SupportPage { get; }
    protected abstract Task CollectAsync(Query query, DriverInventory inventory, DriverScanOptions options, CancellationToken ct);

    public async Task<DriverUpdateSearchResult> SearchAsync(DriverDiscoveryContext context, IProgress<DriverScanProgress>? progress,
        CancellationToken ct)
    {
        var query = new Query(http, Source, SupportPage, temporaryDirectory);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            try { await CollectAsync(query, context.Inventory, context.Options, deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { query.Failure("The source reached its discovery time limit."); }
        }
        catch (Exception ex) when (IsSourceError(ex))
        { query.Failure(ex.Message); }
        ct.ThrowIfCancellationRequested();
        var result = new DriverUpdateSearchResult(query.Updates, query.Notices) { State = query.State, SupportPage = query.Manual };
        try
        {
            if (result.State is DriverSourceState.Success or DriverSourceState.Partial && !query.HasFailure && result.Updates.Count > 0)
                await http.UseCatalogAsync(Source, context, result, ct).ConfigureAwait(false);
            else if (result.State == DriverSourceState.Unavailable &&
                await http.UseCatalogAsync(Source, context, null, ct).ConfigureAwait(false) is { } cached)
                result = new(cached.Updates, [.. query.Notices, "Showing the last usable cached catalog."])
                { State = DriverSourceState.Stale, SupportPage = cached.SupportPage };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            result = result with { Notices = [.. result.Notices, $"Catalog cache: {ex.Message}"] };
        }
        return result;
    }

    protected static bool IsSourceError(Exception ex) => ex is DriverScanException or HttpRequestException or IOException or
        UnauthorizedAccessException or JsonException or XmlException or InvalidDataException or FormatException or
        InvalidOperationException or KeyNotFoundException or RegexMatchTimeoutException;

    protected sealed class Query(OfficialDriverHttp transport, DriverSources source, Uri supportPage, string? temporaryDirectory)
    {
        private bool _stale;
        private bool _failed;
        private bool _partial;
        private bool _unsupported;
        public List<DriverUpdate> Updates { get; } = [];
        public List<string> Notices { get; } = [];
        public DateTimeOffset Retrieved { get; private set; }
        public bool HasFailure => _failed;
        public Uri Manual { get; set; } = supportPage;
        public DriverSourceState State => _stale ? DriverSourceState.Stale
            : _failed ? Updates.Count == 0 ? DriverSourceState.Unavailable : DriverSourceState.Partial
            : _unsupported && Updates.Count == 0 ? DriverSourceState.Unsupported
            : _partial || _unsupported ? DriverSourceState.Partial : DriverSourceState.Success;

        public void Unsupported(string reason) { _unsupported = true; Notices.Add(reason); }
        public void Partial(string reason) { _partial = true; Notices.Add(reason); }
        public void Failure(string reason) { _failed = true; Notices.Add(reason); }

        public async Task<OfficialDriverHttp.Metadata> GetAsync(Uri url, CancellationToken ct, string? form = null)
        {
            var result = await transport.ReadAsync(source, url, ct, form).ConfigureAwait(false);
            if (result.StaleReason is { } reason) { _stale = true; Notices.Add($"{url.Host}: {reason}"); }
            Retrieved = result.Retrieved;
            return result;
        }

        public async Task<string> HtmlAsync(Uri url, CancellationToken ct, string? form = null)
        {
            var body = (await GetAsync(url, ct, form).ConfigureAwait(false)).Text;
            if (Rx(@"<title[^>]*>\s*(Access Denied|Just a moment|Request Rejected|Attention Required)|cf-chl-|_Incapsula_Resource|verify you are human").IsMatch(body))
                throw new DriverScanException($"{url.Host} blocked automated access or requires a CAPTCHA.");
            return body;
        }

        public async Task<JsonDocument> JsonAsync(Uri url, CancellationToken ct) =>
            JsonDocument.Parse((await GetAsync(url, ct).ConfigureAwait(false)).Body);

        public async Task<XDocument> XmlAsync(Uri url, CancellationToken ct, DriverHash[]? hashes = null)
        {
            var bytes = (await GetAsync(url, ct).ConfigureAwait(false)).Body;
            if (hashes is not null) DriverPayloadIntegrity.Check(bytes, hashes);
            if (!url.AbsolutePath.EndsWith(".cab", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = new MemoryStream(bytes);
                return await ReadXmlAsync(stream, ct).ConfigureAwait(false);
            }
            using var workspace = new DriverScanWorkspace(temporaryDirectory);
            var cabinet = Path.Combine(workspace.Root, "metadata.cab");
            var expanded = Path.Combine(workspace.Root, "catalog");
            Directory.CreateDirectory(expanded);
            await File.WriteAllBytesAsync(cabinet, bytes, ct).ConfigureAwait(false);
            await DriverArchive.ExtractCabAsync(cabinet, expanded, ct).ConfigureAwait(false);
            var files = DriverScanWorkspace.EnumerateFiles(expanded, ".xml").Take(2).ToArray();
            if (files.Length != 1) throw new DriverScanException($"{source} catalog archive does not contain one XML manifest.");
            await using var xml = File.OpenRead(files[0]);
            return await ReadXmlAsync(xml, ct).ConfigureAwait(false);
        }

        private static async Task<XDocument> ReadXmlAsync(Stream stream, CancellationToken ct)
        {
            using var reader = XmlReader.Create(stream, new() { Async = true, DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null, MaxCharactersInDocument = 128L * 1024 * 1024 });
            return await XDocument.LoadAsync(reader, LoadOptions.None, ct).ConfigureAwait(false);
        }

        public void Add(string id, string title, string manufacturer, string version, string date, string family,
            DriverTarget target, Uri landing, DriverPayload[] payloads, long? size = null, string branch = "",
            Uri? descriptor = null, string[]? ids = null, bool whql = false, bool license = false,
            string[]? components = null, string? revision = null, DateTimeOffset? retrievedAt = null,
            bool oemRecommended = false, string[]? prerequisites = null)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
                throw new DriverScanException($"{source} returned a record without identity or title.");
            if (!OfficialDriverHttp.Approved(source, landing) ||
                descriptor is not null && !OfficialDriverHttp.Approved(source, descriptor))
                throw new DriverScanException($"{source} returned an unapproved support or descriptor URL.");
            var approved = payloads.Where(payload => OfficialDriverHttp.Approved(source, payload.Url)).ToArray();
            if (payloads.Length != approved.Length)
            {
                Partial($"{title}: the package includes an unapproved distribution URL.");
                approved = [];
            }
            if (!DateOnly.TryParse(date, CultureInfo.InvariantCulture, out var release) &&
                !DateOnly.TryParseExact(date, ["yyyyMMdd", "yyyy/MM/dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out release))
                release = default;
            var reference = new DriverSourceReference(source, id, revision ?? $"{version}|{date}",
                retrievedAt ?? Retrieved, landing, descriptor, approved.Select(payload => payload.Url).ToArray());
            Updates.Add(new($"{source}:{id}", title, manufacturer, "", ids?.FirstOrDefault() ?? "",
                release == default ? "" : release.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "", DriverCertification.Unverified)
            {
                SizeBytes = size,
                Catalog = new([reference], approved)
                {
                    PackageVersion = version, Family = family, Branch = branch, Target = target, ListedHardwareIds = ids ?? [],
                    ClaimsWhql = whql, LicenseRequired = license, ReleaseDate = release == default ? null : release,
                    Verification = license ? DriverVerification.LicenseRequired : DriverVerification.Metadata,
                    DeclaredComponents = components ?? [], OemRecommended = oemRecommended, DeclaredPrerequisites = prerequisites ?? []
                }
            });
        }
    }

    protected static Regex Rx(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline |
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    protected static string Plain(string html) => Rx(@"\s+").Replace(WebUtility.HtmlDecode(Rx("<[^>]+>").Replace(html, " ")), " ").Trim();
    protected static string Group(string body, string pattern, int group = 1) => Rx(pattern).Match(body).Groups[group].Value;
    protected static Uri Link(Uri origin, string path) => new(origin, WebUtility.HtmlDecode(path));
    protected static Uri? OptionalLink(Uri origin, string path) => string.IsNullOrWhiteSpace(path) ? null : Link(origin, path);
    protected static string Arg(string input) => Uri.EscapeDataString(input);
    protected static long? Size(string input)
    {
        var match = Rx(@"^\s*(\d+(?:[.,]\d+)?)\s*(bytes|[kmgt]?b)?\b").Match(input);
        if (!decimal.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            return null;
        value *= match.Groups[2].Value.ToUpperInvariant() switch
        { "KB" => 1024L, "MB" => 1L << 20, "GB" => 1L << 30, "TB" => 1L << 40, _ => 1L };
        return value <= long.MaxValue ? (long)value : null;
    }

    protected static string Text(JsonElement value, string key) => value.TryGetProperty(key, out var field) &&
        field.ValueKind is JsonValueKind.String or JsonValueKind.Number ? field.ToString() : "";
    protected static IEnumerable<XElement> Elements(XContainer value, string key) =>
        value.Elements().Where(element => element.Name.LocalName.Equals(key, StringComparison.OrdinalIgnoreCase));
    protected static IEnumerable<XElement> Descendants(XContainer value, string key) =>
        value.Descendants().Where(element => element.Name.LocalName.Equals(key, StringComparison.OrdinalIgnoreCase));
    protected static string Field(XContainer value, string key) => Elements(value, key).FirstOrDefault()?.Value.Trim() ?? "";
    protected static string Attr(XElement value, string key) =>
        value.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value ?? "";
    protected static DriverHash[] Hashes(params (string Algorithm, string Value)[] hashes) =>
        hashes.Where(hash => !string.IsNullOrWhiteSpace(hash.Value)).Select(hash => new DriverHash(hash.Algorithm, hash.Value.Trim())).ToArray();
    protected static string ModelKey(string value) => Rx(@"[^a-z0-9]").Replace(value, "").ToUpperInvariant();
    protected static bool Contains(string text, string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);
    protected static bool Windows11(DriverSystemContext system) => system.Build >= 22000;
    protected static DriverTarget ClientTarget(DriverSystemContext system, string oem = "", string model = "") =>
        new(system.Architecture, Windows11(system) ? 22000 : 10240, Windows11(system) ? null : 21999, oem, model,
            Windows11(system) ? "Windows 11" : "Windows 10");
    protected static IEnumerable<DriverDevice> Devices(DriverInventory inventory, string hardwareToken, string? className = null) =>
        inventory.Devices.Where(device => device.Present && (className is null || device.ClassName.Equals(className, StringComparison.OrdinalIgnoreCase)) &&
            device.HardwareIds.Any(id => Contains(id, hardwareToken)));
    protected static bool Client(DriverSystemContext system) => system.Major == 10 && system.Build >= 10240 && system.ProductType is 0 or 1;
}
