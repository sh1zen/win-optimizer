using System.Security.Principal;
using System.Text.Json;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Optimize.Interfaces;

namespace WinOptimizer.Infrastructure.Features.Optimize.Services;

/// <summary>Coordinates explicit Optimize actions through existing setting owners and supported Windows commands.</summary>
public sealed partial class OptimizationToolsService(IPowerShellRunner scripts, ISettingApplicationService settings,
    ICompatibleSettingsRegistry catalog, IWindowsRegistryService registry, IApplicationModeService mode,
    IInteractiveUserService user, IWindowsVersionService windows,
    ISystemSettingsDiscoveryService discovery) : IOptimizationToolsService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal const string ReadNetworkScript = """
        $ErrorActionPreference = 'Stop'
        $tcp = Get-NetTCPSetting -SettingName Internet -ErrorAction Stop
        $offload = Get-NetOffloadGlobalSetting -ErrorAction Stop
        [pscustomobject]@{
            AutoTuningLevel = [string]$tcp.AutoTuningLevelLocal
            ReceiveSideScaling = [string]$offload.ReceiveSideScaling
        } | ConvertTo-Json -Compress
        """;

    internal const string EnhanceNetworkScript = """
        $ErrorActionPreference = 'Stop'
        $netsh = Join-Path $env:SystemRoot 'System32\netsh.exe'
        & $netsh interface tcp set global autotuninglevel=normal | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "TCP auto-tuning failed (netsh exit $LASTEXITCODE)." }
        Set-NetOffloadGlobalSetting -ReceiveSideScaling Enabled -ErrorAction Stop
        """;

    public async Task<NetworkPerformanceState> ReadNetworkAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return await ReadNetworkCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>Restores normal TCP window scaling and enables global RSS, then verifies native readback.</summary>
    public async Task<NetworkPerformanceState> EnhanceNetworkAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var reservation = ReserveWrite();
            await scripts.RunScriptInMemoryAsync(EnhanceNetworkScript).ConfigureAwait(false);
            var state = await ReadNetworkCoreAsync().ConfigureAwait(false);
            if (!state.AutoTuningLevel.Equals("Normal", StringComparison.OrdinalIgnoreCase) || !state.ReceiveSideScaling)
                throw new InvalidOperationException("Windows did not confirm normal TCP auto-tuning and enabled receive-side scaling. Refresh to inspect the current state.");
            return state;
        }
        finally { _gate.Release(); }
    }

    private async Task<NetworkPerformanceState> ReadNetworkCoreAsync()
    {
        var output = await scripts.RunScriptInMemoryAsync(ReadNetworkScript).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(output);
            var autoTuning = document.RootElement.GetProperty("AutoTuningLevel").GetString();
            var rss = document.RootElement.GetProperty("ReceiveSideScaling").GetString();
            if (string.IsNullOrWhiteSpace(autoTuning) || rss is not ("Enabled" or "Disabled"))
                throw new InvalidOperationException("Windows returned an unrecognized network configuration.");
            return new(autoTuning, rss == "Enabled");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException)
        { throw new InvalidOperationException("Windows did not return a readable network configuration.", ex); }
    }

    /// <summary>Reuses existing settings, filters absent optional services and stops on the first reported failure.</summary>
    public Task ApplyFeatureAsync(WindowsFeatureOptimization feature) => SetFeatureAsync(feature, true);

    /// <summary>Reads the existing setting owners without treating missing services or failed reads as applied restrictions.</summary>
    public async Task<bool?> ReadFeatureAsync(WindowsFeatureOptimization feature)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var targets = ResolveTargets(feature);
            if (targets.Length == 0) return null;
            var states = await discovery.GetSettingStatesAsync(targets.Select(target => target.Definition)).ConfigureAwait(false);
            var isOn = true;
            foreach (var target in targets)
            {
                if (!states.TryGetValue(target.Definition.Id, out var state) || !state.Success)
                    throw new InvalidOperationException($"Could not read '{target.Definition.Name}': {state?.ErrorMessage ?? "Setting state unavailable."}");
                isOn &= target.Definition.InputType == InputType.Selection
                    ? Equals(state.CurrentValue, target.Request.Value) : !state.IsEnabled;
            }
            return isOn;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Uses the same setting owners for both toggle directions and restores declared service defaults.</summary>
    public async Task SetFeatureAsync(WindowsFeatureOptimization feature, bool isOn)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var reservation = ReserveWrite();
            if (feature == WindowsFeatureOptimization.Copilot && !windows.IsWindows11())
                throw new InvalidOperationException("This Copilot action requires Windows 11.");
            // Resolve the entire plan before writing, so an unavailable definition cannot cause a partial apply.
            var targets = ResolveTargets(feature);
            if (targets.Length == 0)
                throw new InvalidOperationException("None of the optional services in this action are installed. No settings were changed.");
            var requests = targets.Select(target => isOn ? target.Request : target.Request with
            {
                Enable = true,
                Value = target.Definition.InputType == InputType.Selection
                    ? target.Definition.ComboBox!.Options.Select((option, index) => (option, index)).Single(item => item.option.IsDefault).index
                    : null
            }).ToArray();
            string? copilotScript = null;
            if (isOn && feature == WindowsFeatureOptimization.Copilot)
            {
                using var identity = user.IsOtsElevation ? null : WindowsIdentity.GetCurrent();
                var sid = user.IsOtsElevation ? user.InteractiveUserSid : identity?.User?.Value;
                if (string.IsNullOrWhiteSpace(sid)) throw new InvalidOperationException("The interactive user identity is unavailable.");
                copilotScript = CopilotRemovalScript(sid);
            }
            var completed = 0;
            for (var index = 0; index < targets.Length; index++)
            {
                var target = targets[index];
                var result = await settings.ApplySettingAsync(requests[index]).ConfigureAwait(false);
                if (!result.Success)
                    throw new InvalidOperationException($"Could not apply '{target.Definition.Name}': {result.ErrorMessage}. {completed} earlier settings completed; review their individual controls before retrying.", result.Exception);
                completed++;
            }
            if (copilotScript != null)
            {
                try { await scripts.RunScriptInMemoryAsync(copilotScript).ConfigureAwait(false); }
                catch (InvalidOperationException ex)
                { throw new InvalidOperationException("Copilot app removal failed after the Windows and Edge policies were applied. Review their individual controls before retrying. " + ex.Message, ex); }
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Resolves the authoritative plan and excludes optional services absent from this machine.</summary>
    private (ApplySettingRequest Request, SettingDefinition Definition)[] ResolveTargets(WindowsFeatureOptimization feature)
    {
        var targets = Plan(feature).Select(request => (Request: request, Definition: catalog.GetById(request.SettingId)
            ?? throw new InvalidOperationException($"Setting '{request.SettingId}' is unavailable on this Windows version.")));
        if (feature is WindowsFeatureOptimization.OptionalServices or WindowsFeatureOptimization.Telemetry)
            targets = targets.Where(target => target.Definition.InputType != InputType.Selection
                || target.Definition.RegistrySettings.Count > 0
                && target.Definition.RegistrySettings.All(value => registry.KeyExists(value.KeyPath)));
        return targets.ToArray();
    }

    private IDisposable ReserveWrite()
    {
        if (mode.CurrentMode != WinOptimizerMode.Normal)
            throw new InvalidOperationException("Optimization actions require normal application mode.");
        return mode.BeginSystemOperation();
    }

    /// <summary>Lists existing setting IDs and option indexes rather than duplicating policy/service implementations.</summary>
    internal static IReadOnlyList<ApplySettingRequest> Plan(WindowsFeatureOptimization feature) => feature switch
    {
        WindowsFeatureOptimization.OptionalServices => [DisableService("gaming-retail-demo-service"), DisableService("gaming-maps-broker-service"), DisableService("gaming-fax-service")],
        WindowsFeatureOptimization.Telemetry => [Disable("privacy-diagnostics"), DisableService("gaming-telemetry-service")],
        WindowsFeatureOptimization.Cortana => [Disable("privacy-allow-cortana")],
        WindowsFeatureOptimization.Copilot => [Disable("privacy-turn-off-copilot"), Disable("privacy-edge-copilot-sidebar"), Disable("privacy-edge-m365-copilot-icon"), Disable("privacy-edge-copilot-page-context"), Disable("privacy-edge-entra-copilot")],
        WindowsFeatureOptimization.ActivityHistory => [Disable("privacy-activity-history")],
        WindowsFeatureOptimization.Advertising => [Disable("privacy-advertising-id"), Disable("privacy-tailored-experiences")],
        _ => throw new ArgumentOutOfRangeException(nameof(feature)),
    };

    private static ApplySettingRequest Disable(string id) => new() { SettingId = id, Enable = false };
    private static ApplySettingRequest DisableService(string id) => new() { SettingId = id, Enable = false, Value = 0 };

    /// <summary>Removes only Microsoft's standalone Copilot registration for the explicitly identified interactive account.</summary>
    internal static string CopilotRemovalScript(string sid)
    {
        var normalizedSid = new SecurityIdentifier(sid).Value;
        return $$"""
            $ErrorActionPreference = 'Stop'
            $targetSid = '{{normalizedSid}}'
            $packages = @(Get-AppxPackage -User $targetSid -Name 'Microsoft.Copilot' -ErrorAction Stop |
                Where-Object { $_.Name -eq 'Microsoft.Copilot' -and $_.PublisherId -eq '8wekyb3d8bbwe' })
            foreach ($package in $packages) {
                Remove-AppxPackage -User $targetSid -Package $package.PackageFullName -ErrorAction Stop
            }
            $remaining = @(Get-AppxPackage -User $targetSid -Name 'Microsoft.Copilot' -ErrorAction Stop |
                Where-Object { $_.Name -eq 'Microsoft.Copilot' -and $_.PublisherId -eq '8wekyb3d8bbwe' })
            if ($remaining.Count -ne 0) { throw 'The standalone Copilot app is still registered. Windows and Edge policies may already have changed; review them before retrying.' }
            """;
    }
}
