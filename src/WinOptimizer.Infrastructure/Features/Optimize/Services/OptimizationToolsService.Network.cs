using System.Globalization;
using System.Text.Json;
using Microsoft.Win32;
using WinOptimizer.Core.Features.Optimize.Models;

namespace WinOptimizer.Infrastructure.Features.Optimize.Services;

/// <summary>Reads network controls in batches and serializes validated, independently verified changes.</summary>
public sealed partial class OptimizationToolsService
{
    internal const string NetworkPriorityPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\ServiceProvider";
    internal const string DeliveryOptimizationPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization";

    private static readonly Dictionary<NetworkTuningOwner, string> NetworkRegistryPaths = new()
    {
        [NetworkTuningOwner.NameResolution] = NetworkPriorityPath,
        [NetworkTuningOwner.DeliveryOptimization] = DeliveryOptimizationPath
    };

    internal static readonly string ReadNetworkTuningsScript = BuildNetworkReadScript();

    public async Task<IReadOnlyList<NetworkTuningState>> ReadNetworkTuningsAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return await IncludeOriginalsAsync(await ReadNetworkTuningsCoreAsync().ConfigureAwait(false)).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>Rejects stale baselines and invalid values before touching the selected native owner.</summary>
    public async Task<IReadOnlyList<NetworkTuningState>> SetNetworkTuningAsync(string id, string? value, string? expectedValue)
    {
        var definition = NetworkTuningCatalog.Get(id);
        definition.Validate(value);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var reservation = ReserveWrite();
            var snapshot = await ReadNetworkTuningsCoreAsync().ConfigureAwait(false);
            var before = snapshot.Single(state => state.Id == id);
            if (!before.IsAvailable)
                throw new InvalidOperationException($"Network setting '{id}' is unavailable: {before.ErrorMessage}");
            if (!string.Equals(before.Value, expectedValue, StringComparison.Ordinal))
                throw new InvalidOperationException("This network setting changed outside the page. Refresh before retrying.");
            await IncludeOriginalsAsync(snapshot).ConfigureAwait(false);
            if (NetworkRegistryPaths.TryGetValue(definition.Owner, out var path))
            {
                var success = value == null
                    ? !registry.ValueExists(path, definition.Property) || registry.DeleteValue(path, definition.Property)
                    : registry.SetValue(path, definition.Property, unchecked((int)uint.Parse(value, CultureInfo.InvariantCulture)), RegistryValueKind.DWord);
                if (!success) throw new InvalidOperationException($"Could not change '{definition.Property}'. Check administrator permissions.");
            }
            else
                await scripts.RunScriptInMemoryAsync(NetworkWriteScript(definition, value!)).ConfigureAwait(false);
            var after = await ReadNetworkTuningsCoreAsync().ConfigureAwait(false);
            var actual = after.Single(state => state.Id == id);
            if (!actual.IsAvailable || !string.Equals(actual.Value, value, StringComparison.Ordinal))
                throw new InvalidOperationException($"Windows did not confirm '{id}'. Refresh to inspect any partial changes. {actual.ErrorMessage}");
            return await IncludeOriginalsAsync(after).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Joins the first observed values to a live readback without substituting them for live state.</summary>
    private async Task<IReadOnlyList<NetworkTuningState>> IncludeOriginalsAsync(IReadOnlyList<NetworkTuningState> states)
    {
        var results = new NetworkTuningState[states.Count];
        for (var index = 0; index < states.Count; index++)
        {
            var state = states[index];
            results[index] = state with { Original = await originals.GetOrCaptureAsync(state).ConfigureAwait(false) };
        }
        return results;
    }

    /// <summary>Saves the two values affected by Enhance before its first native write.</summary>
    private async Task CaptureEnhancementOriginalsAsync()
    {
        var needsAutoTuning = await originals.ReadAsync(NetworkTuningCatalog.AutoTuningId).ConfigureAwait(false) == null;
        var needsRss = await originals.ReadAsync(NetworkTuningCatalog.RssId).ConfigureAwait(false) == null;
        if (!needsAutoTuning && !needsRss) return;

        var state = await ReadNetworkCoreAsync().ConfigureAwait(false);
        if (needsAutoTuning)
            await originals.GetOrCaptureAsync(new(NetworkTuningCatalog.AutoTuningId, state.AutoTuningLevel, true)).ConfigureAwait(false);
        if (needsRss)
            await originals.GetOrCaptureAsync(new(NetworkTuningCatalog.RssId, state.ReceiveSideScaling ? NetworkTuningCatalog.Enabled : "Disabled", true)).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<NetworkTuningState>> ReadNetworkTuningsCoreAsync()
    {
        var output = await scripts.RunScriptInMemoryAsync(ReadNetworkTuningsScript).ConfigureAwait(false);
        try
        {
            var states = JsonSerializer.Deserialize<NetworkTuningState[]>(output)
                ?? throw new InvalidOperationException("Windows returned no network settings.");
            if (states.Any(state => state == null) || states.Length != NetworkTuningCatalog.All.Count || states.Select(state => state.Id).Distinct().Count() != states.Length
                || NetworkTuningCatalog.All.Any(definition => states.All(state => state.Id != definition.Id))
                || states.Any(state => state.IsAvailable && state.Value == null && !NetworkTuningCatalog.Get(state.Id).HasRegistryOverride))
                throw new InvalidOperationException("Windows returned incomplete network settings. Refresh before making changes.");
            return states;
        }
        catch (JsonException ex) { throw new InvalidOperationException("Windows did not return readable network settings.", ex); }
    }

    /// <summary>Builds commands exclusively from catalogue metadata and validated values, never user-supplied command text.</summary>
    internal static string NetworkWriteScript(NetworkTuningDefinition definition, string value)
    {
        // Resolve again to require the authoritative metadata even for internal callers.
        definition = NetworkTuningCatalog.Get(definition.Id);
        definition.Validate(value);
        var command = definition.Owner switch
        {
            NetworkTuningOwner.Tcp => "Set-NetTCPSetting -SettingName Internet",
            NetworkTuningOwner.Offload => "Set-NetOffloadGlobalSetting",
            NetworkTuningOwner.IPv4 => "Set-NetIPv4Protocol",
            NetworkTuningOwner.IPv6 => "Set-NetIPv6Protocol",
            _ => throw new ArgumentException("This setting uses the registry owner.", nameof(definition))
        };
        return $"$ErrorActionPreference = 'Stop'\n{command} -{definition.Parameter} '{value}' -ErrorAction Stop | Out-Null";
    }

    /// <summary>Reads each native source once, preserving per-owner errors and absent registry overrides.</summary>
    private static string BuildNetworkReadScript()
    {
        var definitions = JsonSerializer.Serialize(NetworkTuningCatalog.All.Select(item => new
        { item.Id, Owner = item.Owner.ToString(), item.Property, item.Parameter, Input = item.Input.ToString(), item.HasRegistryOverride }));
        var paths = JsonSerializer.Serialize(NetworkRegistryPaths);
        return $$"""
            $ErrorActionPreference = 'Stop'
            $definitions = '{{definitions}}' | ConvertFrom-Json
            $paths = '{{paths}}' | ConvertFrom-Json
            $sources = @{}
            $errors = @{}
            $setters = @{}
            foreach ($owner in @('Tcp', 'Offload', 'IPv4', 'IPv6')) {
                $noun = switch ($owner) { 'Tcp' { 'NetTCPSetting' } 'Offload' { 'NetOffloadGlobalSetting' } 'IPv4' { 'NetIPv4Protocol' } 'IPv6' { 'NetIPv6Protocol' } }
                try {
                    $setters[$owner] = Get-Command ('Set-' + $noun) -ErrorAction Stop
                    $sources[$owner] = if ($owner -eq 'Tcp') { Get-NetTCPSetting -SettingName Internet -ErrorAction Stop } else { & ('Get-' + $noun) -ErrorAction Stop }
                } catch { $errors[$owner] = $_.Exception.Message }
            }
            $keys = @{}
            try {
                foreach ($entry in $paths.PSObject.Properties) {
                    try {
                        $path = 'Registry::' + $entry.Value
                        if (Test-Path -LiteralPath $path -ErrorAction Stop) { $keys[$entry.Name] = Get-Item -LiteralPath $path -ErrorAction Stop }
                    } catch { $errors[$entry.Name] = $_.Exception.Message }
                }
                $results = foreach ($definition in $definitions) {
                    $value = $null
                    $errorText = $errors[$definition.Owner]
                    if (-not $errorText) {
                        try {
                            if ($definition.HasRegistryOverride) {
                                $key = $keys[$definition.Owner]
                                if ($null -ne $key) {
                                    $raw = $key.GetValue($definition.Property, $null)
                                    if ($null -ne $raw) {
                                        if ($key.GetValueKind($definition.Property) -ne [Microsoft.Win32.RegistryValueKind]::DWord) { throw 'The override is not a DWORD value.' }
                                        if ($definition.Owner -eq 'DeliveryOptimization') { $raw = [int64]$raw -band 4294967295L }
                                        $value = [string]$raw
                                    }
                                }
                            } else {
                                if (-not $setters[$definition.Owner].Parameters.ContainsKey($definition.Parameter)) { throw 'This Windows version does not support editing this setting.' }
                                $property = $sources[$definition.Owner].PSObject.Properties[$definition.Property]
                                if ($null -eq $property -or $null -eq $property.Value) { throw 'Windows did not expose the setting value.' }
                                $value = [string]$property.Value
                                if ($definition.Input -eq 'Toggle' -and $value -notin @('Enabled', 'Disabled')) { throw ('Unrecognized toggle state: ' + $value) }
                            }
                        } catch { $errorText = $_.Exception.Message }
                    }
                    [pscustomobject]@{ Id = $definition.Id; Value = $value; IsAvailable = -not [bool]$errorText; ErrorMessage = $errorText }
                }
                ConvertTo-Json -InputObject @($results) -Compress
            } finally { foreach ($key in $keys.Values) { $key.Dispose() } }
            """;
    }
}
