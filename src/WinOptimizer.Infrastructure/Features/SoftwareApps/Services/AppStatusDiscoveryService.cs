using WinOptimizer.Core.Features.Common.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Enums;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;

namespace WinOptimizer.Infrastructure.Features.SoftwareApps.Services;

public class AppStatusDiscoveryService(
    ILogService logService,
    IWinGetBootstrapper winGetBootstrapper,
    IWinGetDetectionService winGetDetectionService,
    IChocolateyService chocolateyService,
    IInstalledProgramSource installedPrograms,
    IAppxPackageSource appxPackageSource,
    IWindowsComponentStatusSource componentStatusSource,
    IAppMetadataSource metadataSource) : IAppStatusDiscoveryService
{
    private HashSet<string>? _cachedWinGetPackageIds;

    public void InvalidateCache()
    {
        _cachedWinGetPackageIds = null;
    }

    public async Task<Dictionary<string, bool>> GetInstallationStatusBatchAsync(IEnumerable<ItemDefinition> definitions)
    {
        using var logTopic = logService.BeginTopic(LogTopic.Software);
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var definitionList = definitions.ToList();

        if (!definitionList.Any()) return result;

        try
        {
            var apps = definitionList.Where(d => d.AppxPackageName?.Length > 0).ToList();
            var capabilities = definitionList.Where(d => !string.IsNullOrEmpty(d.CapabilityName)).ToList();
            var features = definitionList.Where(d => !string.IsNullOrEmpty(d.OptionalFeatureName)).ToList();

            // DISM keeps its existing serialized queries and deadlines while
            // independent AppX enumeration runs alongside it.
            var componentsTask = capabilities.Count > 0 || features.Count > 0
                ? componentStatusSource.GetStatusAsync(
                    capabilities.Select(c => c.CapabilityName!).ToList(),
                    features.Select(f => f.OptionalFeatureName!).ToList())
                : Task.FromResult(new WindowsComponentStatus(
                    new Dictionary<string, bool>(), new Dictionary<string, bool>()));
            var packagesTask = apps.Count > 0
                ? appxPackageSource.GetInstalledPackageNamesAsync()
                : Task.FromResult(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            // Ordinary AppX identities do not depend on servicing results. Start
            // their conditional WinGet fallback as soon as package enumeration
            // finishes, while DISM is still running. Shared component identities
            // retain the component-first precedence below.
            var componentIds = capabilities.Concat(features).Select(item => item.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var winGetTask = apps.Any(app => componentIds.Contains(app.Id))
                ? null : GetAppWinGetFallbackAsync(apps, packagesTask);
            await Task.WhenAll(componentsTask, packagesTask,
                winGetTask is null ? Task.CompletedTask : winGetTask).ConfigureAwait(false);
            var components = await componentsTask.ConfigureAwait(false);

            int capCount = 0, featCount = 0, appxCount = 0, wingetCount = 0;

            if (capabilities.Any())
            {
                var capabilityResults = components.Capabilities;
                foreach (var capability in capabilities)
                {
                    if (capabilityResults.TryGetValue(capability.CapabilityName!, out bool isInstalled))
                    {
                        result[capability.Id] = isInstalled;
                        if (isInstalled)
                        {
                            capCount++;
                            logService.LogInformation($"Installed (Capability): {capability.Name} ({capability.CapabilityName})");
                        }
                    }
                }
            }

            if (features.Any())
            {
                var featureResults = components.Features;
                foreach (var feature in features)
                {
                    if (featureResults.TryGetValue(feature.OptionalFeatureName!, out bool isInstalled))
                    {
                        result[feature.Id] = isInstalled;
                        if (isInstalled)
                        {
                            featCount++;
                            logService.LogInformation($"Installed (Feature): {feature.Name} ({feature.OptionalFeatureName})");
                        }
                    }
                }
            }

            if (apps.Any())
            {
                var installedPackageNames = await packagesTask.ConfigureAwait(false);
                foreach (var app in apps)
                {
                    if (app.AppxPackageName!.Any(name => installedPackageNames.Contains(name)))
                    {
                        result[app.Id] = true;
                        appxCount++;
                        logService.LogInformation($"Installed (AppX): {app.Name} ({string.Join(", ", app.AppxPackageName!)})");
                    }
                }

                // WinGet fallback for apps not found by PackageManager
                var undetectedApps = apps
                    .Where(a => !result.ContainsKey(a.Id) || !result[a.Id])
                    .Where(a => (a.WinGetPackageId != null && a.WinGetPackageId.Any()) || !string.IsNullOrEmpty(a.MsStoreId))
                    .ToList();

                if (undetectedApps.Any())
                {
                    var winGetIds = winGetTask is null
                        ? await GetOrFetchWinGetPackageIdsAsync().ConfigureAwait(false)
                        : await winGetTask.ConfigureAwait(false);
                    if (winGetIds != null)
                    {
                        foreach (var app in undetectedApps)
                        {
                            var matchedById = app.WinGetPackageId?.Any(pkgId => winGetIds.Contains(pkgId)) == true;
                            var matchedByStoreId = !string.IsNullOrEmpty(app.MsStoreId) && winGetIds.Contains(app.MsStoreId);
                            if (matchedById || matchedByStoreId)
                            {
                                result[app.Id] = true;
                                wingetCount++;
                                var matchedId = matchedByStoreId
                                    ? app.MsStoreId!
                                    : app.WinGetPackageId!.First(pkgId => winGetIds.Contains(pkgId));
                                logService.LogInformation($"Installed (WinGet): {app.Name} ({matchedId})");
                            }
                        }
                    }
                }

                // Mark remaining unfound apps as not installed
                foreach (var app in apps)
                {
                    if (!result.ContainsKey(app.Id))
                        result[app.Id] = false;
                }
            }

            var notFoundCount = result.Count(kvp => !kvp.Value);
            logService.LogInformation(
                $"Detection complete: {capCount} via Capability, {featCount} via Feature, {appxCount} via AppX, {wingetCount} via WinGet, {notFoundCount} not found");

            await RefreshMetadataAsync(definitionList, result).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex)
        {
            logService.LogError("Error checking batch installation status", ex);
            return definitionList.ToDictionary(d => d.Id, d => false, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Overlaps required fallback detection with independent Windows component queries.</summary>
    private async Task<HashSet<string>?> GetAppWinGetFallbackAsync(IReadOnlyList<ItemDefinition> apps,
        Task<HashSet<string>> packagesTask)
    {
        var packages = await packagesTask.ConfigureAwait(false);
        var installedIds = apps.Where(app => app.AppxPackageName!.Any(packages.Contains))
            .Select(app => app.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return apps.Any(app => !installedIds.Contains(app.Id) &&
            (app.WinGetPackageId?.Length > 0 || !string.IsNullOrEmpty(app.MsStoreId)))
            ? await GetOrFetchWinGetPackageIdsAsync().ConfigureAwait(false) : null;
    }

    private async Task<HashSet<string>?> GetOrFetchWinGetPackageIdsAsync()
    {
        if (_cachedWinGetPackageIds != null)
            return _cachedWinGetPackageIds;

        try
        {
            bool winGetReady = await winGetBootstrapper.EnsureWinGetReadyAsync().ConfigureAwait(false);
            if (!winGetReady)
            {
                logService.LogWarning("WinGet unavailable - skipping WinGet detection");
                return null;
            }

            _cachedWinGetPackageIds = await winGetDetectionService.GetInstalledPackageIdsAsync().ConfigureAwait(false);
            logService.LogInformation($"WinGet: Fetched {_cachedWinGetPackageIds.Count} installed package IDs");
            return _cachedWinGetPackageIds;
        }
        catch (Exception ex)
        {
            logService.LogWarning($"WinGet detection failed: {ex.Message}");
            return null;
        }
    }

    internal record RegistryUninstallInfo(
        HashSet<string> KeyNames,
        HashSet<string> DisplayNames,
        HashSet<string> AllKeyNames,
        HashSet<string> KeyPaths);

    /// <summary>
    /// Tests whether input matches a pattern containing {version}, {arch}, {locale} placeholders.
    /// Each placeholder is replaced by a non-greedy wildcard (.+?) for regex matching.
    /// Patterns without placeholders perform exact case-insensitive comparison.
    /// </summary>
    internal static bool MatchesPattern(string input, string pattern)
    {
        if (string.IsNullOrEmpty(input))
            return false;

        var regexPattern = Regex.Escape(pattern)
            .Replace(@"\{version}", ".+?")
            .Replace(@"\{arch}", ".+?")
            .Replace(@"\{locale}", ".+?");
        return Regex.IsMatch(input, $"^{regexPattern}$", RegexOptions.IgnoreCase);
    }

    #region External Apps Detection

    public async Task<Dictionary<string, bool>> GetExternalAppsInstallationStatusAsync(IEnumerable<ItemDefinition> definitions)
    {
        using var logTopic = logService.BeginTopic(LogTopic.Software);
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var definitionList = definitions.ToList();

        if (!definitionList.Any())
            return result;

        try
        {
            int wingetCount = 0, chocoCount = 0, appxCount = 0, registryCount = 0, fileSystemCount = 0;

            // Fetched lazily below and reused in Phase 4 to avoid a second registry scan.
            RegistryUninstallInfo? sharedRegInfo = null;

            // Phase 1: WinGet detection for apps with WinGetPackageId or MsStoreId
            var appsWithWinGetId = definitionList
                .Where(d => (d.WinGetPackageId != null && d.WinGetPackageId.Any()) || !string.IsNullOrEmpty(d.MsStoreId))
                .ToList();

            if (appsWithWinGetId.Any())
            {
                var winGetIds = await GetOrFetchWinGetPackageIdsAsync().ConfigureAwait(false);

                if (winGetIds != null)
                {
                    // If any Phase 1 app also carries a registry disambiguator, pull the registry
                    // uninstall info up front so we can require winget+registry agreement below.
                    // WinGet's installed-package list can false-positive when a sibling build
                    // (e.g. AutoHotkey v1 installed via direct URL) shares the vendor's uninstall
                    // entry that winget uses to fingerprint "AutoHotkey.AutoHotkey" (v2).
                    bool needRegistryDisambiguation = appsWithWinGetId.Any(d =>
                        !string.IsNullOrEmpty(d.RegistryDisplayName) || !string.IsNullOrEmpty(d.RegistrySubKeyName));
                    if (needRegistryDisambiguation)
                    {
                        sharedRegInfo = await GetRegistryUninstallInfoAsync().ConfigureAwait(false);
                    }

                    foreach (var def in appsWithWinGetId)
                    {
                        var matchedById = def.WinGetPackageId?.Any(pkgId => winGetIds.Contains(pkgId)) == true;
                        var matchedByStoreId = !string.IsNullOrEmpty(def.MsStoreId) && winGetIds.Contains(def.MsStoreId);
                        bool isInstalled = matchedById || matchedByStoreId;

                        // When the definition provides registry fingerprints, require registry
                        // agreement to accept a WinGet match. Rejected items fall through to
                        // later phases rather than being marked installed on a false positive.
                        if (isInstalled && sharedRegInfo != null &&
                            (!string.IsNullOrEmpty(def.RegistryDisplayName) || !string.IsNullOrEmpty(def.RegistrySubKeyName)))
                        {
                            bool registryAgrees =
                                (!string.IsNullOrEmpty(def.RegistryDisplayName)
                                    && sharedRegInfo.DisplayNames.Any(dn => MatchesPattern(dn, def.RegistryDisplayName!)))
                                || (!string.IsNullOrEmpty(def.RegistrySubKeyName)
                                    && sharedRegInfo.AllKeyNames.Any(k => MatchesPattern(k, def.RegistrySubKeyName!)));

                            if (!registryAgrees)
                            {
                                logService.LogInformation(
                                    $"WinGet reported {def.Name} installed, but registry fingerprint did not match - treating as false positive");
                                isInstalled = false;
                            }
                        }

                        result[def.Id] = isInstalled;

                        if (isInstalled)
                        {
                            def.DetectedVia = DetectionSource.WinGet;
                            wingetCount++;
                            var matchedPackageId = matchedByStoreId
                                ? def.MsStoreId!
                                : def.WinGetPackageId!.First(pkgId => winGetIds.Contains(pkgId));
                            logService.LogInformation($"Installed (WinGet): {def.Name} ({matchedPackageId})");
                        }
                    }

                    logService.LogInformation($"WinGet: Checked {appsWithWinGetId.Count} apps");
                }
                else
                {
                    logService.LogWarning("WinGet unavailable - cannot check installation status for apps with WinGetPackageId");
                }
            }

            // Phase 2: Chocolatey detection for apps not found by WinGet
            var appsForChocoCheck = definitionList
                .Where(d => !string.IsNullOrEmpty(d.ChocoPackageId)
                    && (!result.ContainsKey(d.Id) || !result[d.Id]))
                .ToList();

            if (appsForChocoCheck.Any())
            {
                try
                {
                    var chocoPackageIds = await chocolateyService.GetInstalledPackageIdsAsync().ConfigureAwait(false);

                    if (chocoPackageIds.Count > 0)
                    {
                        foreach (var def in appsForChocoCheck)
                        {
                            if (chocoPackageIds.Contains(def.ChocoPackageId!))
                            {
                                result[def.Id] = true;
                                def.DetectedVia = DetectionSource.Chocolatey;
                                chocoCount++;
                                logService.LogInformation($"Installed (Chocolatey): {def.Name} ({def.ChocoPackageId})");
                            }
                        }

                        logService.LogInformation($"Chocolatey: Checked {appsForChocoCheck.Count} apps");
                    }
                    else
                    {
                        logService.LogInformation("Chocolatey not installed or no packages found - skipping Chocolatey detection");
                    }
                }
                catch (Exception ex)
                {
                    logService.LogWarning($"Chocolatey detection failed: {ex.Message}");
                }
            }

            // Phase 3: AppX detection for apps with AppxPackageName
            var appsWithAppxName = definitionList
                .Where(d => d.AppxPackageName?.Length > 0)
                .Where(d => !result.ContainsKey(d.Id) || !result[d.Id])
                .ToList();

            if (appsWithAppxName.Any())
            {
                var installedPackageNames = await appxPackageSource.GetInstalledPackageNamesAsync().ConfigureAwait(false);
                foreach (var def in appsWithAppxName)
                {
                    if (def.AppxPackageName!.Any(name => installedPackageNames.Contains(name)))
                    {
                        result[def.Id] = true;
                        def.DetectedVia = DetectionSource.AppX;
                        appxCount++;
                        logService.LogInformation($"Installed (AppX): {def.Name} ({string.Join(", ", def.AppxPackageName!)})");
                    }
                }
            }

            // Phase 4: Registry fallback for apps not detected via WinGet, Chocolatey, or AppX
            var appsForRegistryCheck = definitionList
                .Where(d => !result.ContainsKey(d.Id) || !result[d.Id])
                .ToList();

            if (appsForRegistryCheck.Any())
            {
                var regInfo = sharedRegInfo ?? await GetRegistryUninstallInfoAsync().ConfigureAwait(false);

                // Discovered entries require their exact registration, even when another installation has the same name.
                foreach (var def in appsForRegistryCheck.Where(d => d.RegistryUninstallKeyPath != null))
                {
                    result[def.Id] = regInfo.KeyPaths.Contains(def.RegistryUninstallKeyPath!);
                    if (result[def.Id])
                    {
                        def.DetectedVia = DetectionSource.Registry;
                        registryCount++;
                    }
                }
                var catalogueRegistryCheck = appsForRegistryCheck.Where(d => d.RegistryUninstallKeyPath == null).ToList();

                // Pass 1: Exact def.Name match against KeyNames
                foreach (var def in catalogueRegistryCheck.Where(d => !result.ContainsKey(d.Id) || !result[d.Id]))
                {
                    if (regInfo.KeyNames.Contains(def.Name))
                    {
                        result[def.Id] = true;
                        def.DetectedVia = DetectionSource.Registry;
                        registryCount++;
                        logService.LogInformation($"Installed (Registry Pass 1 - KeyName): {def.Name}");
                    }
                }

                // Pass 2: Exact def.Name match against DisplayNames
                foreach (var def in catalogueRegistryCheck.Where(d => !result.ContainsKey(d.Id) || !result[d.Id]))
                {
                    if (regInfo.DisplayNames.Contains(def.Name))
                    {
                        result[def.Id] = true;
                        def.DetectedVia = DetectionSource.Registry;
                        registryCount++;
                        logService.LogInformation($"Installed (Registry Pass 2 - DisplayName): {def.Name}");
                    }
                }

                // Pass 3: RegistrySubKeyName pattern match against AllKeyNames
                foreach (var def in catalogueRegistryCheck
                    .Where(d => !string.IsNullOrEmpty(d.RegistrySubKeyName))
                    .Where(d => !result.ContainsKey(d.Id) || !result[d.Id]))
                {
                    var matchedKey3 = regInfo.AllKeyNames.FirstOrDefault(k => MatchesPattern(k, def.RegistrySubKeyName!));
                    if (matchedKey3 != null)
                    {
                        result[def.Id] = true;
                        def.DetectedVia = DetectionSource.Registry;
                        registryCount++;
                        logService.LogInformation($"Installed (Registry Pass 3 - SubKeyName pattern): {def.Name}");
                    }
                }

                // Pass 4: RegistryDisplayName pattern match against DisplayNames
                foreach (var def in catalogueRegistryCheck
                    .Where(d => !string.IsNullOrEmpty(d.RegistryDisplayName))
                    .Where(d => !result.ContainsKey(d.Id) || !result[d.Id]))
                {
                    var matchedDn4 = regInfo.DisplayNames.FirstOrDefault(dn => MatchesPattern(dn, def.RegistryDisplayName!));
                    if (matchedDn4 != null)
                    {
                        result[def.Id] = true;
                        def.DetectedVia = DetectionSource.Registry;
                        registryCount++;
                        logService.LogInformation($"Installed (Registry Pass 4 - DisplayName pattern): {def.Name}");
                    }
                }

                // Mark remaining undetected items as false
                foreach (var def in appsForRegistryCheck.Where(d => !result.ContainsKey(d.Id)))
                {
                    result[def.Id] = false;
                }
            }

            // Phase 5: File system path detection for portable apps
            var appsWithDetectionPaths = definitionList
                .Where(d => d.DetectionPaths?.Length > 0)
                .Where(d => !result.ContainsKey(d.Id) || !result[d.Id])
                .ToList();

            if (appsWithDetectionPaths.Any())
            {
                foreach (var def in appsWithDetectionPaths)
                {
                    foreach (var rawPath in def.DetectionPaths!)
                    {
                        var expandedPath = Environment.ExpandEnvironmentVariables(rawPath);
                        if (Directory.Exists(expandedPath) || File.Exists(expandedPath))
                        {
                            result[def.Id] = true;
                            def.DetectedVia = DetectionSource.FileSystem;
                            fileSystemCount++;
                            logService.LogInformation($"Installed (FileSystem): {def.Name} ({expandedPath})");
                            break;
                        }
                    }
                }
            }

            var totalFound = result.Count(kvp => kvp.Value);
            logService.LogInformation(
                $"Status check complete: {totalFound}/{definitionList.Count} apps installed ({wingetCount} via WinGet, {chocoCount} via Chocolatey, {appxCount} via AppX, {registryCount} via Registry, {fileSystemCount} via FileSystem)");

            await RefreshMetadataAsync(definitionList, result).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex)
        {
            logService.LogError($"Error checking installation status: {ex.Message}", ex);
            return definitionList.ToDictionary(d => d.Id, d => false, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Refreshes metadata independently so unavailable measurements cannot change installed status.</summary>
    private async Task RefreshMetadataAsync(IReadOnlyList<ItemDefinition> definitions, IReadOnlyDictionary<string, bool> status)
    {
        foreach (var definition in definitions)
            definition.Metadata = null;
        try
        {
            var installed = definitions.Where(item => status.TryGetValue(item.Id, out var found) && found).ToList();
            if (installed.Count == 0) return;
            var metadata = await metadataSource.ReadAsync(installed).ConfigureAwait(false);
            foreach (var definition in installed)
                definition.Metadata = metadata.GetValueOrDefault(definition.Id);
        }
        catch (Exception ex)
        {
            logService.LogWarning($"App sorting metadata unavailable: {ex.Message}");
        }
    }

    /// <summary>Builds catalogue fingerprints and exact installed-program identities from one registry inventory.</summary>
    private async Task<RegistryUninstallInfo> GetRegistryUninstallInfoAsync()
    {
        var registrations = await installedPrograms.ReadAsync().ConfigureAwait(false);
        var visible = registrations.Where(program => !program.IsSystemComponent).ToList();
        return new RegistryUninstallInfo(
            visible.Select(program => program.KeyName).ToHashSet(StringComparer.OrdinalIgnoreCase),
            visible.Select(program => program.Name).Where(name => !string.IsNullOrEmpty(name)).ToHashSet(StringComparer.OrdinalIgnoreCase),
            registrations.Select(program => program.KeyName).ToHashSet(StringComparer.OrdinalIgnoreCase),
            registrations.Select(program => program.KeyPath).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }
    #endregion
}
