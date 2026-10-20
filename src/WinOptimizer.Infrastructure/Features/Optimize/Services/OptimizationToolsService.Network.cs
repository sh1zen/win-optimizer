using System.Globalization;
using System.Text.Json;
using Microsoft.Win32;
using WinOptimizer.Core.Features.Optimize.Models;

namespace WinOptimizer.Infrastructure.Features.Optimize.Services;

/// <summary>Reads network controls in batches and serializes validated, independently verified changes.</summary>
public sealed partial class OptimizationToolsService
{
    internal const string NetworkPriorityPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\ServiceProvider";

    internal static readonly string ReadNetworkTuningsScript = BuildNetworkReadScript();

    public async Task<IReadOnlyList<NetworkTuningState>> ReadNetworkTuningsAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return await ReadNetworkTuningsCoreAsync().ConfigureAwait(false); }
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
            var before = (await ReadNetworkTuningsCoreAsync().ConfigureAwait(false)).Single(state => state.Id == id);
            if (!before.IsAvailable)
                throw new InvalidOperationException($"Network setting '{id}' is unavailable: {before.ErrorMessage}");
            if (!string.Equals(before.Value, expectedValue, StringComparison.Ordinal))
                throw new InvalidOperationException("This network setting changed outside the page. Refresh before retrying.");
            if (definition.Owner == NetworkTuningOwner.NameResolution)
            {
                var success = value == null
                    ? !registry.ValueExists(NetworkPriorityPath, definition.Property) || registry.DeleteValue(NetworkPriorityPath, definition.Property)
                    : registry.SetValue(NetworkPriorityPath, definition.Property, int.Parse(value, CultureInfo.InvariantCulture), RegistryValueKind.DWord);
                if (!success) throw new InvalidOperationException($"Could not change '{definition.Property}'. Check administrator permissions.");
            }
            else
                await scripts.RunScriptInMemoryAsync(NetworkWriteScript(definition, value!)).ConfigureAwait(false);
            var after = await ReadNetworkTuningsCoreAsync().ConfigureAwait(false);
            var actual = after.Single(state => state.Id == id);
            if (!actual.IsAvailable || !string.Equals(actual.Value, value, StringComparison.Ordinal))
                throw new InvalidOperationException($"Windows did not confirm '{id}'. Refresh to inspect any partial changes. {actual.ErrorMessage}");
            return after;
        }
        finally { _gate.Release(); }
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
                || states.Any(state => state.IsAvailable && state.Value == null && NetworkTuningCatalog.Get(state.Id).Owner != NetworkTuningOwner.NameResolution))
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

    /// <summary>Reads each native source once, preserving per-owner errors and absent legacy registry overrides.</summary>
    private static string BuildNetworkReadScript()
    {
        var definitions = JsonSerializer.Serialize(NetworkTuningCatalog.All.Select(item => new
        { item.Id, Owner = item.Owner.ToString(), item.Property, item.Parameter, Input = item.Input.ToString() }));
        return $$"""
            $ErrorActionPreference = 'Stop'
            $definitions = '{{definitions}}' | ConvertFrom-Json
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
            $legacy = $null
            try {
                $priorityPath = 'Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\ServiceProvider'
                if (Test-Path -LiteralPath $priorityPath -ErrorAction Stop) { $legacy = Get-Item -LiteralPath $priorityPath -ErrorAction Stop }
            } catch { $errors['NameResolution'] = $_.Exception.Message }
            try {
                $results = foreach ($definition in $definitions) {
                    $value = $null
                    $errorText = $errors[$definition.Owner]
                    if (-not $errorText) {
                        try {
                            if ($definition.Owner -eq 'NameResolution') {
                                if ($null -ne $legacy) {
                                    $raw = $legacy.GetValue($definition.Property, $null)
                                    if ($null -ne $raw) {
                                        if ($legacy.GetValueKind($definition.Property) -ne [Microsoft.Win32.RegistryValueKind]::DWord) { throw 'The priority override is not a DWORD value.' }
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
            } finally { if ($null -ne $legacy) { $legacy.Dispose() } }
            """;
    }
}
