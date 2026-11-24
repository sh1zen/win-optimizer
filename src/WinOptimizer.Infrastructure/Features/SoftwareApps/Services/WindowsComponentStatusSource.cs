using System.Runtime.InteropServices;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Native;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.Infrastructure.Features.Common.Utilities;

namespace WinOptimizer.Infrastructure.Features.SoftwareApps.Services;

/// <summary>Reads Windows servicing status with serialized, bounded DISM queries.</summary>
public sealed class WindowsComponentStatusSource : IWindowsComponentStatusSource
{
    private readonly ILogService _log;
    private readonly DismSessionManager _sessions;
    private readonly Func<uint, HashSet<string>> _readCapabilities;
    private readonly Func<uint, HashSet<string>> _readFeatures;

    public WindowsComponentStatusSource(ILogService log)
    {
        _log = log;
        _sessions = DismSessionManager.Shared;
        _readCapabilities = ReadInstalledCapabilities;
        _readFeatures = ReadEnabledFeatures;
    }

    internal WindowsComponentStatusSource(ILogService log, DismSessionManager sessions,
        Func<uint, HashSet<string>> readCapabilities, Func<uint, HashSet<string>> readFeatures)
    {
        _log = log;
        _sessions = sessions;
        _readCapabilities = readCapabilities;
        _readFeatures = readFeatures;
    }

    /// <inheritdoc />
    public async Task<WindowsComponentStatus> GetStatusAsync(
        IReadOnlyList<string> capabilities, IReadOnlyList<string> features)
    {
        var queries = new List<Func<uint, HashSet<string>>>(2);
        if (capabilities.Count > 0) queries.Add(_readCapabilities);
        if (features.Count > 0) queries.Add(_readFeatures);
        var results = await _sessions.ExecuteAsync(queries, _log.LogDebug).ConfigureAwait(false);
        var capabilityResult = capabilities.Count == 0 ? null : results[0];
        var featureResult = features.Count == 0 ? null : results[^1];
        return new(Match(capabilities, capabilityResult, isCapability: true),
            Match(features, featureResult, isCapability: false))
        {
            CapabilitiesRead = capabilities.Count == 0 || capabilityResult?.Value is not null,
            FeaturesRead = features.Count == 0 || featureResult?.Value is not null
        };
    }

    private Dictionary<string, bool> Match(IReadOnlyList<string> names,
        DismSessionManager.QueryResult<HashSet<string>>? read, bool isCapability)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var kind = isCapability ? "Capability" : "Feature";
        if (read?.Error is OperationCanceledException)
            _log.LogWarning($"[DISM-Detect] {kind} query timed out after 30s. Marking requested entries as unknown.");
        else if (read?.Error is { } error)
            _log.LogError($"[DISM-Detect] Error checking {kind} status: {error.GetType().Name}: {error.Message}", error);

        foreach (var name in names)
        {
            var match = read?.Value is { } installed && (isCapability
                ? installed.Contains(name) || installed.Any(entry => entry.StartsWith(name + "~", StringComparison.OrdinalIgnoreCase))
                : installed.Contains(name));
            result[name] = match;
            if (read?.Value is not null) _log.LogInformation($"[DISM-Detect] {kind} match: '{name}' => {match}");
        }
        return result;
    }

    private HashSet<string> ReadInstalledCapabilities(uint session)
    {
        DismApi.ThrowIfFailed(DismApi.DismGetCapabilities(session, out var buffer, out var count), "GetCapabilities");
        try
        {
            var installed = DismApi.MarshalArray<DismApi.DISM_CAPABILITY>(buffer, count)
                .Where(entry => entry.State == DismApi.DismStateInstalled)
                .Select(entry => Marshal.PtrToStringUni(entry.Name)).OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _log.LogInformation($"[DISM-Detect] DismGetCapabilities: {installed.Count} installed out of {count} total");
            return installed;
        }
        finally { DismApi.DismDelete(buffer); }
    }

    private HashSet<string> ReadEnabledFeatures(uint session)
    {
        DismApi.ThrowIfFailed(DismApi.DismGetFeatures(session, null, 0, out var buffer, out var count), "GetFeatures");
        try
        {
            var enabled = DismApi.MarshalArray<DismApi.DISM_FEATURE>(buffer, count)
                .Where(entry => entry.State == DismApi.DismStateInstalled)
                .Select(entry => Marshal.PtrToStringUni(entry.FeatureName)).OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _log.LogInformation($"[DISM-Detect] DismGetFeatures: {enabled.Count} enabled out of {count} total");
            return enabled;
        }
        finally { DismApi.DismDelete(buffer); }
    }
}

