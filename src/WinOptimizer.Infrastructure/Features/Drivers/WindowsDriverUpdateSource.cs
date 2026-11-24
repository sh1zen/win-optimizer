using System.ComponentModel;
using System.Globalization;
using System.Text;
using WinOptimizer.Core.Features.Drivers.Interfaces;
using WinOptimizer.Core.Features.Drivers.Models;
using WinOptimizer.Core.Features.Drivers.Services;

namespace WinOptimizer.Infrastructure.Features.Drivers;

/// <summary>Retains Microsoft's native applicability evidence and acquires only the exact offered revision.</summary>
public sealed class WindowsDriverUpdateSource(DriverPowerShell shell, IDriverInfInspector inspector, string? temporaryDirectory = null)
    : IDriverUpdateSource
{
    private sealed record Offer(string Id, string Title, string Provider, string ClassName, string HardwareId, string Date, string PayloadError)
    {
        public long? SizeBytes { get; init; }
        public int Revision { get; init; }
        public string Manufacturer { get; init; } = "";
        public string ReleaseDate { get; init; } = "";
        public bool LicenseRequired { get; init; }
        public bool VerificationSkipped { get; init; }
    }
    private sealed record OfferList(IReadOnlyList<Offer> Updates)
    {
        public bool Partial { get; init; }
    }

    public DriverSources Source => DriverSources.WindowsUpdate;
    private static readonly Uri SupportPage = new("https://support.microsoft.com/windows/update-drivers-manually-in-windows-ec62f46c-ff14-c91d-eead-d7126dc1f7b6");
    private readonly DriverPackageInspector _packages = new(inspector);

    public async Task<DriverUpdateSearchResult> SearchAsync(DriverDiscoveryContext context, IProgress<DriverScanProgress>? progress, CancellationToken ct)
    {
        try
        {
            using var workspace = new DriverScanWorkspace(temporaryDirectory);
            var limit = Math.Clamp(context.Options.AutomaticVerificationLimit, 0, 8);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromMinutes(4));
            var result = await QueryAsync(workspace, $"$verificationLimit={limit};", deadline.Token).ConfigureAwait(false);
            List<DriverUpdate> updates = [];
            List<string> notices = [];
            if (result.Partial) notices.Add("Windows Update reported an incomplete search.");
            for (var index = 0; index < result.Updates.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var offer = Validate(result.Updates[index]);
                var update = ToUpdate(offer);
                if (offer.PayloadError.Length > 0) notices.Add($"{offer.Title}: {offer.PayloadError}");
                else if (!offer.VerificationSkipped)
                {
                    progress?.Report(new("Drivers_VerifyingPackages", index, result.Updates.Count));
                    try
                    {
                        var folder = OfferFolder(workspace, offer.Id);
                        var originals = DriverScanWorkspace.EnumerateFiles(folder, null).ToArray();
                        update = await InspectAsync(update, originals, folder, context.Inventory, ct).ConfigureAwait(false);
                        if (update.Certification != DriverCertification.Whql)
                            notices.Add($"{offer.Title}: WHQL could not be established for the matching driver INFs.");
                    }
                    catch (Exception ex) when (IsPackageError(ex))
                    {
                        notices.Add($"{offer.Title}: {ex.Message}");
                        update = update with { Catalog = update.Catalog! with
                        { Verification = DriverVerification.Failed, VerificationDetail = ex.Message } };
                    }
                }
                updates.Add(update);
            }
            if (result.Updates.Any(offer => offer.VerificationSkipped))
                notices.Add("Some Windows Update offers exceeded the automatic verification limit.");
            ct.ThrowIfCancellationRequested();
            return new(updates, notices)
            {
                State = notices.Count == 0 ? DriverSourceState.Success : DriverSourceState.Partial,
                SupportPage = SupportPage
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return new([], ["The Windows Update search reached its time limit."]) { State = DriverSourceState.Unavailable, SupportPage = SupportPage }; }
        catch (Exception ex) when (IsPackageError(ex))
        { return new([], [ex.Message]) { State = DriverSourceState.Unavailable, SupportPage = SupportPage }; }
    }

    internal async Task<(DriverUpdate Update, string Root, string[] Files)> AcquireAsync(DriverUpdate update, DriverInventory inventory,
        DriverScanWorkspace workspace, CancellationToken ct)
    {
        var reference = update.Catalog?.References.Single(item => item.Source == DriverSources.WindowsUpdate);
        if (reference is null || !Guid.TryParseExact(reference.Id, "D", out var id) ||
            !int.TryParse(reference.Revision, NumberStyles.None, CultureInfo.InvariantCulture, out var revision))
            throw new DriverScanException("The Windows Update offer identity is invalid.");
        var result = await QueryAsync(workspace, $"$requestedUpdate='{id:D}';$requestedRevision={revision};", ct).ConfigureAwait(false);
        if (result.Updates.Count != 1) throw new DriverScanException("This exact Windows Update revision is no longer offered.");
        var offer = Validate(result.Updates[0]);
        if (offer.PayloadError.Length > 0) throw new DriverScanException(offer.PayloadError);
        var root = OfferFolder(workspace, offer.Id);
        var originals = DriverScanWorkspace.EnumerateFiles(root, null).ToArray();
        var inspected = await InspectAsync(ToUpdate(offer), originals, root, inventory, ct).ConfigureAwait(false);
        return (inspected, root, originals);
    }

    private async Task<OfferList> QueryAsync(DriverScanWorkspace workspace, string options, CancellationToken ct)
    {
        var encodedPath = Convert.ToBase64String(Encoding.UTF8.GetBytes(workspace.Root));
        var prelude = $"$workspace=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{encodedPath}'));{options}";
        var result = await shell.RunAsync<OfferList>("DriverUpdates", prelude, ct).ConfigureAwait(false);
        if (result.Updates is null) throw new DriverScanException("Windows Update returned no offer collection.");
        return result;
    }

    private static Offer Validate(Offer offer)
    {
        if (offer is null || !Guid.TryParseExact(offer.Id, "D", out _) || string.IsNullOrWhiteSpace(offer.Title) ||
            offer.HardwareId is null || offer.Provider is null || offer.ClassName is null || offer.Date is null ||
            offer.PayloadError is null || offer.SizeBytes < 0)
            throw new DriverScanException("Windows Update returned an invalid driver identity.");
        return offer;
    }

    private DriverUpdate ToUpdate(Offer offer)
    {
        var source = new DriverSourceReference(Source, offer.Id, offer.Revision.ToString(CultureInfo.InvariantCulture),
            DateTimeOffset.UtcNow, SupportPage, null, []);
        return new(offer.Id, offer.Title, offer.Manufacturer.Length > 0 ? offer.Manufacturer : offer.Provider, offer.ClassName, offer.HardwareId,
            offer.Date, "", DriverCertification.Unverified)
        {
            SizeBytes = offer.SizeBytes,
            Catalog = new([source], [])
            {
                Family = offer.ClassName, Branch = "Windows Update", ListedHardwareIds = offer.HardwareId.Length > 0 ? [offer.HardwareId] : [],
                ReleaseDate = DateOnly.TryParseExact(offer.ReleaseDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var release) ? release : null,
                LicenseRequired = offer.LicenseRequired,
                Verification = offer.LicenseRequired ? DriverVerification.LicenseRequired :
                    offer.PayloadError.Length > 0 ? DriverVerification.Failed : DriverVerification.Metadata,
                VerificationDetail = offer.VerificationSkipped ? "Outside the automatic verification limit." : offer.PayloadError
            }
        };
    }

    private async Task<DriverUpdate> InspectAsync(DriverUpdate update, string[] files, string root, DriverInventory inventory, CancellationToken ct)
    {
        if (files.Length == 0) throw new DriverScanException("Windows Update exported an empty package.");
        var evidence = await _packages.InspectAsync(files, root, ct).ConfigureAwait(false);
        var original = update.Catalog!;
        var ids = new HashSet<string>(original.ListedHardwareIds, StringComparer.OrdinalIgnoreCase);
        foreach (var id in inventory.Devices.Where(device => device.Present &&
                     DriverCatalogMatching.Identifiers(device).Any(identifier => original.ListedHardwareIds.Contains(identifier.Value,
                         StringComparer.OrdinalIgnoreCase))).SelectMany(DriverCatalogMatching.Identifiers))
            ids.Add(id.Value);
        // Native applicability grants visibility, but only matching certified INF evidence grants WHQL status.
        var matches = evidence.Components.Where(component => component.Identifiers.Any(item => ids.Contains(item.Value))
            && (update.ClassName.Length == 0 || component.ClassName.Equals(update.ClassName, StringComparison.OrdinalIgnoreCase))).ToArray();
        var versions = matches.Select(component => component.Version).Distinct().ToArray();
        var verification = evidence.Unsupported ? DriverVerification.Unsupported :
            evidence.Components.Length == 0 ? DriverVerification.Failed : DriverVerification.Verified;
        List<string> hashes = [];
        if (verification == DriverVerification.Verified)
            foreach (var file in files)
            {
                await using var stream = File.OpenRead(file);
                hashes.Add(await DriverPayloadIntegrity.CheckAsync(stream, [], ct).ConfigureAwait(false));
            }
        return update with
        {
            Version = versions.Length == 1 ? versions[0] : "",
            Certification = verification == DriverVerification.Verified && matches.Length > 0 &&
                matches.All(component => component.Certification == DriverCertification.Whql)
                ? DriverCertification.Whql : DriverCertification.Unverified,
            Catalog = original with { Components = evidence.Components, ContentSha256 = hashes.ToArray(), Verification = verification }
        };
    }

    private static string OfferFolder(DriverScanWorkspace workspace, string id) =>
        DriverScanWorkspace.ValidateChild(workspace.Root, Path.Combine(workspace.Root, id));
    private static bool IsPackageError(Exception ex) => ex is DriverScanException or IOException or UnauthorizedAccessException or Win32Exception or InvalidDataException;
}
