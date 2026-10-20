using System.Runtime.InteropServices;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Native;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.Infrastructure.Features.Common.Utilities;

namespace WinOptimizer.Infrastructure.Features.SoftwareApps.Services;

/// <summary>Reads Windows servicing status with serialized, bounded DISM queries.</summary>
public sealed class WindowsComponentStatusSource(ILogService logService) : IWindowsComponentStatusSource
{
    /// <inheritdoc />
    public async Task<WindowsComponentStatus> GetStatusAsync(
        IReadOnlyList<string> capabilities, IReadOnlyList<string> features)
    {
        var capabilityStatus = capabilities.Count > 0
            ? await CheckCapabilitiesAsync(capabilities).ConfigureAwait(false)
            : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var featureStatus = features.Count > 0
            ? await CheckFeaturesAsync(features).ConfigureAwait(false)
            : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        return new WindowsComponentStatus(capabilityStatus, featureStatus);
    }

    /// <summary>Matches requested capability prefixes against installed DISM entries.</summary>
    private async Task<Dictionary<string, bool>> CheckCapabilitiesAsync(IReadOnlyList<string> capabilities)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        try
        {
            logService.LogInformation($"[DISM-Detect] CheckCapabilitiesAsync: checking {capabilities.Count} capabilities: [{string.Join(", ", capabilities)}]");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var installedCapabilities = await DismSessionManager.ExecuteAsync<HashSet<string>>(session =>
            {
                DismApi.ThrowIfFailed(
                    DismApi.DismGetCapabilities(session, out IntPtr capPtr, out uint count),
                    "GetCapabilities");
                try
                {
                    var allCaps = DismApi.MarshalArray<DismApi.DISM_CAPABILITY>(capPtr, count);

                    var installed = new HashSet<string>(
                        allCaps.Where(c => c.State == DismApi.DismStateInstalled)
                               .Select(c => Marshal.PtrToStringUni(c.Name)!)
                               .Where(n => n != null),
                        StringComparer.OrdinalIgnoreCase);

                    logService.LogInformation($"[DISM-Detect] DismGetCapabilities: {installed.Count} installed out of {count} total");
                    return installed;
                }
                finally
                {
                    DismApi.DismDelete(capPtr);
                }
            }, cts.Token, msg => logService.LogDebug(msg)).ConfigureAwait(false);

            foreach (var capability in capabilities)
            {
                var match = installedCapabilities.Any(c =>
                    c.StartsWith(capability, StringComparison.OrdinalIgnoreCase));
                result[capability] = match;
                logService.LogInformation($"[DISM-Detect] Capability match: '{capability}' => {match}");
            }
        }
        catch (OperationCanceledException)
        {
            logService.LogWarning("[DISM-Detect] CheckCapabilitiesAsync timed out after 30s — DISM may be unresponsive on this system. Marking all capabilities as unknown.");
            foreach (var capability in capabilities)
                result[capability] = false;
        }
        catch (Exception ex)
        {
            logService.LogError($"[DISM-Detect] Error checking capabilities status: {ex.GetType().Name}: {ex.Message}", ex);
            foreach (var capability in capabilities)
                result[capability] = false;
        }

        return result;
    }

    /// <summary>Matches requested feature names against enabled DISM entries.</summary>
    private async Task<Dictionary<string, bool>> CheckFeaturesAsync(IReadOnlyList<string> features)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        try
        {
            logService.LogInformation($"[DISM-Detect] CheckFeaturesAsync: checking {features.Count} features: [{string.Join(", ", features)}]");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var enabledFeatures = await DismSessionManager.ExecuteAsync<HashSet<string>>(session =>
            {
                DismApi.ThrowIfFailed(
                    DismApi.DismGetFeatures(session, null, 0, out IntPtr featPtr, out uint count),
                    "GetFeatures");
                try
                {
                    var allFeatures = DismApi.MarshalArray<DismApi.DISM_FEATURE>(featPtr, count);

                    var enabled = new HashSet<string>(
                        allFeatures.Where(f => f.State == DismApi.DismStateInstalled)
                                   .Select(f => Marshal.PtrToStringUni(f.FeatureName)!)
                                   .Where(n => n != null),
                        StringComparer.OrdinalIgnoreCase);

                    logService.LogInformation($"[DISM-Detect] DismGetFeatures: {enabled.Count} enabled out of {count} total");
                    return enabled;
                }
                finally
                {
                    DismApi.DismDelete(featPtr);
                }
            }, cts.Token, msg => logService.LogDebug(msg)).ConfigureAwait(false);

            foreach (var feature in features)
            {
                var match = enabledFeatures.Contains(feature);
                result[feature] = match;
                logService.LogInformation($"[DISM-Detect] Feature match: '{feature}' => {match}");
            }
        }
        catch (OperationCanceledException)
        {
            logService.LogWarning("[DISM-Detect] CheckFeaturesAsync timed out after 30s — DISM may be unresponsive on this system. Marking all features as unknown.");
            foreach (var feature in features)
                result[feature] = false;
        }
        catch (Exception ex)
        {
            logService.LogError($"[DISM-Detect] Error checking features status: {ex.GetType().Name}: {ex.Message}", ex);
            foreach (var feature in features)
                result[feature] = false;
        }

        return result;
    }
}

