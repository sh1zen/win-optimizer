using System;
using System.Collections.Generic;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using Windows.Management.Deployment;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;

namespace WinOptimizer.Infrastructure.Features.SoftwareApps.Services;

public class AppxPackageSource(
    ILogService logService,
    IPowerShellRunner powerShellRunner,
    IAppxDeployment deployment) : IAppxPackageSource
{
    /// <summary>Uses the interactive account explicitly under alternate-account elevation.</summary>
    public async Task<HashSet<string>> GetInstalledPackageNamesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sid = deployment.InteractiveAccountSid;
        return await DiscoverPackageNamesAsync(sid.Length == 0 ? null : sid, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Enumerates retained device packages without falling back to a narrower account inventory.</summary>
    public async Task<HashSet<string>> GetDevicePackageNamesAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            return await Task.Run(() =>
            {
                timeout.Token.ThrowIfCancellationRequested();
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var package in new PackageManager().FindProvisionedPackages())
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    names.Add(package.Id.Name);
                }
                timeout.Token.ThrowIfCancellationRequested();
                return names;
            }, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new IOException("Cannot read installed packages retained on this device. Check administrator permissions and retry.", ex);
        }
    }

    public Task<HashSet<string>> GetInstalledPackageNamesForUserAsync(string accountSid, CancellationToken cancellationToken = default)
    {
        _ = new System.Security.Principal.SecurityIdentifier(accountSid);
        return DiscoverPackageNamesAsync(accountSid, cancellationToken);
    }

    /// <summary>Queries the requested account; legacy fallbacks apply only to the implicit current-account query.</summary>
    private async Task<HashSet<string>> DiscoverPackageNamesAsync(string? accountSid, CancellationToken cancellationToken)
    {
        var packageNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts.CancelAfter(TimeSpan.FromSeconds(15));
            linkedCts.Token.ThrowIfCancellationRequested();
            var packages = await deployment.GetPackagesAsync(accountSid ?? "", linkedCts.Token).ConfigureAwait(false);
            foreach (var package in packages)
            {
                linkedCts.Token.ThrowIfCancellationRequested();
                packageNames.Add(package.Name);
            }

            logService.LogInformation($"AppX detection via PackageManager: found {packageNames.Count} packages");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // External cancellation — propagate immediately
            throw;
        }
        catch (OperationCanceledException)
        {
            if (accountSid is not null) throw new IOException($"AppX inventory timed out for account {accountSid}.");
            logService.LogWarning("PackageManager enumeration timed out after 15s, falling back to WMI");

            // Tier 2: WMI (Win32_InstalledStoreProgram)
            var wmiResult = await GetInstalledAppxPackageNamesViaWmiAsync().ConfigureAwait(false);
            if (wmiResult.Count > 0)
                return wmiResult;

            // Tier 3: Get-AppxPackage via PowerShell (last resort)
            logService.LogWarning("WMI also returned 0 results, trying Get-AppxPackage");
            return await GetInstalledAppxPackageNamesViaPowerShellAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (accountSid is not null) throw new IOException($"AppX inventory is unavailable for account {accountSid}.", ex);
            logService.LogWarning($"PackageManager failed ({ex.Message}), falling back to WMI");

            // Tier 2: WMI (Win32_InstalledStoreProgram)
            var wmiResult = await GetInstalledAppxPackageNamesViaWmiAsync().ConfigureAwait(false);
            if (wmiResult.Count > 0)
                return wmiResult;

            // Tier 3: Get-AppxPackage via PowerShell (last resort)
            logService.LogWarning("WMI also returned 0 results, trying Get-AppxPackage");
            return await GetInstalledAppxPackageNamesViaPowerShellAsync().ConfigureAwait(false);
        }

        return packageNames;
    }

    private async Task<HashSet<string>> GetInstalledAppxPackageNamesViaWmiAsync()
    {
        var packageNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            await Task.Run(() =>
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_InstalledStoreProgram");
                foreach (var obj in searcher.Get())
                {
                    using (obj)
                    {
                        try
                        {
                            var name = obj["Name"]?.ToString();
                            if (!string.IsNullOrEmpty(name))
                            {
                                // Name is the package family name (e.g., "Microsoft.BingSearch_8wekyb3d8bbwe")
                                // Extract the package name before the publisher hash to match PackageManager.Id.Name
                                var underscoreIndex = name.IndexOf('_');
                                packageNames.Add(underscoreIndex > 0 ? name[..underscoreIndex] : name);
                            }
                        }
                        catch (Exception ex) { logService.LogDebug($"Failed to read WMI InstalledStoreProgram entry: {ex.Message}"); }
                    }
                }
            }).ConfigureAwait(false);

            logService.LogInformation($"WMI InstalledStoreProgram: found {packageNames.Count} packages");
        }
        catch (Exception ex)
        {
            logService.LogError($"WMI InstalledStoreProgram query also failed: {ex.Message}");
        }

        return packageNames;
    }

    private async Task<HashSet<string>> GetInstalledAppxPackageNamesViaPowerShellAsync()
    {
        var packageNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            logService.LogInformation("Fetching installed AppX packages via Get-AppxPackage...");

            var output = await powerShellRunner.RunScriptAsync(
                "Get-AppxPackage | Select-Object -ExpandProperty Name").ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(output))
            {
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var name = line.Trim();
                    if (!string.IsNullOrEmpty(name))
                        packageNames.Add(name);
                }
            }

            logService.LogInformation($"Get-AppxPackage: found {packageNames.Count} packages");
        }
        catch (Exception ex)
        {
            logService.LogError($"Get-AppxPackage also failed: {ex.Message}");
        }

        return packageNames;
    }
}
