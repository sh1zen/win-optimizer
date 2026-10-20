using System.ComponentModel;
using System.Text;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Drivers.Interfaces;
using WinOptimizer.Core.Features.Drivers.Models;

namespace WinOptimizer.Infrastructure.Features.Drivers;

/// <summary>Retrieves official applicable offers and verifies their exported package INFs in a temporary workspace.</summary>
public sealed class WindowsDriverUpdateSource(DriverPowerShell shell, IProcessExecutor processes, IDriverInfInspector inspector, string? temporaryDirectory = null) : IDriverUpdateSource
{
    private sealed record Offer(string Id, string Title, string Provider, string ClassName, string HardwareId, string Date, string PayloadError)
    {
        public long? SizeBytes { get; init; }
    }
    private sealed record OfferList(IReadOnlyList<Offer> Updates);

    public async Task<DriverUpdateSearchResult> SearchAsync(IProgress<DriverScanProgress>? progress, CancellationToken ct)
    {
        try
        {
            using var workspace = new DriverScanWorkspace(temporaryDirectory);
            var encodedPath = Convert.ToBase64String(Encoding.UTF8.GetBytes(workspace.Root));
            var prelude = $"$workspace=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{encodedPath}'));";
            var result = await shell.RunAsync<OfferList>("DriverUpdates", prelude, ct).ConfigureAwait(false);
            if (result.Updates is null) throw new DriverScanException("Windows Update returned no offer collection.");
            List<DriverUpdate> updates = [];
            List<string> notices = [];
            for (var index = 0; index < result.Updates.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var offer = result.Updates[index];
                if (offer is null || !Guid.TryParseExact(offer.Id, "D", out var id) || string.IsNullOrWhiteSpace(offer.Title) ||
                    offer.HardwareId is null || offer.Provider is null || offer.ClassName is null || offer.Date is null || offer.PayloadError is null || offer.SizeBytes < 0)
                    throw new DriverScanException("Windows Update returned an invalid driver identity.");
                progress?.Report(new("Drivers_VerifyingPackages", index, result.Updates.Count));
                var version = "";
                var certification = DriverCertification.Unverified;
                if (!string.IsNullOrEmpty(offer.PayloadError)) notices.Add($"{offer.Title}: {offer.PayloadError}");
                else
                {
                    try
                    {
                        var directory = DriverScanWorkspace.ValidateChild(workspace.Root, Path.Combine(workspace.Root, id.ToString("D")));
                        await ExpandPackagesAsync(directory, ct).ConfigureAwait(false);
                        List<DriverInfInspection> evidence = [];
                        foreach (var path in DriverScanWorkspace.EnumerateFiles(directory, ".inf"))
                        {
                            ct.ThrowIfCancellationRequested();
                            DriverScanWorkspace.ValidateChild(workspace.Root, path);
                            var info = await Task.Run(() => inspector.Inspect(path, offer.HardwareId), ct).ConfigureAwait(false);
                            if (info is not null) evidence.Add(info);
                        }
                        var versions = evidence.Select(info => info.Version).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                        if (versions.Length == 1) version = versions[0];
                        if (evidence.Count > 0 && evidence.All(info => info.Certification == DriverCertification.Whql)) certification = DriverCertification.Whql;
                        else notices.Add($"{offer.Title}: WHQL certification could not be established for the matching driver INF files.");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
                    { notices.Add($"{offer.Title}: {ex.Message}"); }
                }
                updates.Add(new(offer.Id, offer.Title, offer.Provider, offer.ClassName, offer.HardwareId, offer.Date, version, certification) { SizeBytes = offer.SizeBytes });
            }
            ct.ThrowIfCancellationRequested();
            progress?.Report(new("Drivers_VerifyingPackages", result.Updates.Count, result.Updates.Count));
            return new(updates, notices);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        { throw new DriverScanException($"Driver update package inspection failed: {ex.Message}", ex); }
    }

    /// <summary>Extracts official cached CAB payloads; no files are added to the Windows driver store.</summary>
    private async Task ExpandPackagesAsync(string directory, CancellationToken ct)
    {
        foreach (var cabinet in DriverScanWorkspace.EnumerateFiles(directory, ".cab").ToArray())
        {
            var source = DriverScanWorkspace.ValidateChild(directory, cabinet);
            var target = DriverScanWorkspace.ValidateChild(directory, Path.Combine(directory, "expanded-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(target);
            var expanded = await processes.ExecuteWithStreamingAsync(Path.Combine(Environment.SystemDirectory, "expand.exe"),
                $"-F:* \"{source}\" \"{target}\"", ct: ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!expanded.Succeeded) throw new IOException($"Cannot extract driver package: {expanded.StandardError.Trim()}");
        }
    }
}
