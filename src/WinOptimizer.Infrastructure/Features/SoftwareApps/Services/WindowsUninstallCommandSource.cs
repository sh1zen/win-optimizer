using Microsoft.Win32;
using System.Text.RegularExpressions;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;

namespace WinOptimizer.Infrastructure.Features.SoftwareApps.Services;

/// <summary>Reads the selected product's registered native uninstall command in the interactive account.</summary>
public interface IUninstallCommandSource
{
    Task<(bool Found, string UninstallString)> GetAsync(ItemDefinition item);
}

/// <summary>Owns registry command discovery separately from process execution and exit-code verification.</summary>
public sealed class WindowsUninstallCommandSource(IInteractiveUserService interactiveUserService, ILogService logService,
    IInstalledProgramSource installedPrograms) : IUninstallCommandSource
{
    public async Task<(bool Found, string UninstallString)> GetAsync(ItemDefinition item)
    {
        // Discovered programs are bound to one registration; never fall back to a similarly named product.
        if (item.RegistryUninstallKeyPath is { } keyPath)
        {
            var command = await installedPrograms.GetUninstallCommandAsync(keyPath).ConfigureAwait(false);
            return (!string.IsNullOrWhiteSpace(command), command ?? string.Empty);
        }
        return await Task.Run(() =>
        {
            // OTS: redirect HKCU to HKU\{interactive user SID} so we read
            // the standard user's uninstall keys, not the admin's.
            var hkcuUninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
            RegistryKey hkcuHive;
            string hkcuPath;

            if (interactiveUserService.IsOtsElevation && interactiveUserService.InteractiveUserSid != null)
            {
                hkcuHive = Registry.Users;
                hkcuPath = $@"{interactiveUserService.InteractiveUserSid}\{hkcuUninstallPath}";
            }
            else
            {
                hkcuHive = Registry.CurrentUser;
                hkcuPath = hkcuUninstallPath;
            }

            var registryPaths = new[]
            {
                (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                (hkcuHive, hkcuPath),
                (hkcuHive, hkcuPath.Replace(@"SOFTWARE\Microsoft", @"SOFTWARE\WOW6432Node\Microsoft"))
            };

            foreach (var (hive, path) in registryPaths)
            {
                try
                {
                    using var key = hive.OpenSubKey(path);
                    if (key == null) continue;

                    foreach (var subKeyName in key.GetSubKeyNames())
                    {
                        try
                        {
                            using var subKey = key.OpenSubKey(subKeyName);
                            if (subKey == null) continue;

                            var regDisplayName = subKey.GetValue("DisplayName")?.ToString();

                            // Prefer explicit definition patterns when provided (e.g. AutoHotkey v1's
                            // "AutoHotkey 1.{version}"), since item.Name ("AutoHotkey v1") won't
                            // fuzzy-match the actual registry DisplayName ("AutoHotkey 1.1.37.02").
                            bool matched = false;

                            if (!string.IsNullOrEmpty(item.RegistrySubKeyName)
                                && AppStatusDiscoveryService.MatchesPattern(subKeyName, item.RegistrySubKeyName!))
                            {
                                matched = true;
                            }

                            if (!matched
                                && !string.IsNullOrEmpty(item.RegistryDisplayName)
                                && !string.IsNullOrEmpty(regDisplayName)
                                && AppStatusDiscoveryService.MatchesPattern(regDisplayName!, item.RegistryDisplayName!))
                            {
                                matched = true;
                            }

                            // Only fall back to fuzzy name matching when the definition didn't
                            // pin down a precise registry pattern. Apps that set RegistryDisplayName
                            // or RegistrySubKeyName (e.g. XnView, Steam) do so to AVOID collateral
                            // hits like "XnView MP" — fuzzy fallback would re-introduce that bug.
                            if (!matched
                                && string.IsNullOrEmpty(item.RegistryDisplayName)
                                && string.IsNullOrEmpty(item.RegistrySubKeyName)
                                && !string.IsNullOrEmpty(regDisplayName)
                                && IsFuzzyMatch(item.Name, regDisplayName!))
                            {
                                matched = true;
                            }

                            if (matched)
                            {
                                var uninstallString = subKey.GetValue("UninstallString")?.ToString();
                                if (!string.IsNullOrEmpty(uninstallString))
                                {
                                    logService.LogInformation($"Found uninstall string for {item.Name}: {uninstallString}");
                                    return (true, uninstallString);
                                }
                            }
                        }
                        catch (Exception ex) { logService.LogDebug($"Failed to read registry subkey '{subKeyName}': {ex.Message}"); }
                    }
                }
                catch (Exception ex) { logService.LogDebug($"Failed to open registry key for uninstall lookup: {ex.Message}"); }
            }

            return (false, string.Empty);
        }).ConfigureAwait(false);
    }

    private bool IsFuzzyMatch(string searchName, string registryName)
    {
        var normalized1 = NormalizeString(searchName);
        var normalized2 = NormalizeString(registryName);

        if (normalized1 == normalized2)
            return true;

        if (normalized2.Contains(normalized1) || normalized1.Contains(normalized2))
            return true;

        var words1 = normalized1.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var words2 = normalized2.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var matchCount = words1.Count(w => words2.Contains(w));
        return matchCount >= Math.Min(words1.Length, 2);
    }

    private static readonly Regex NonWordOrSpaceRegex = new(@"[^\w\s]", RegexOptions.Compiled);
    private static readonly Regex MultipleSpacesRegex = new(@"\s+", RegexOptions.Compiled);

    private static string NormalizeString(string input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        var normalized = NonWordOrSpaceRegex.Replace(input, "").ToLowerInvariant().Trim();
        return MultipleSpacesRegex.Replace(normalized, " ");
    }

}
