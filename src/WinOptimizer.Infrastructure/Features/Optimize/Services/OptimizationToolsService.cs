using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Optimize.Interfaces;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Services;

namespace WinOptimizer.Infrastructure.Features.Optimize.Services;

/// <summary>Coordinates explicit Optimize actions through existing setting owners and supported Windows commands.</summary>
public sealed partial class OptimizationToolsService(IPowerShellRunner scripts, ISettingApplicationService settings,
    ICompatibleSettingsRegistry catalog, IWindowsRegistryService registry, IApplicationModeService mode,
    IAppxDeployment deployment, IWindowsVersionService windows,
    ISystemSettingsDiscoveryService discovery, NetworkTuningOriginalStore originals) : IOptimizationToolsService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

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
            var account = isOn && feature == WindowsFeatureOptimization.Copilot ? deployment.InteractiveAccountSid : null;
            var completed = 0;
            for (var index = 0; index < targets.Length; index++)
            {
                var target = targets[index];
                var result = await settings.ApplySettingAsync(requests[index]).ConfigureAwait(false);
                if (!result.Success)
                    throw new InvalidOperationException($"Could not apply '{target.Definition.Name}': {result.ErrorMessage}. {completed} earlier settings completed; review their individual controls before retrying.", result.Exception);
                completed++;
            }
            if (account is not null)
            {
                try { await RemoveCopilotRegistrationAsync(account).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
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
    private async Task RemoveCopilotRegistrationAsync(string account)
    {
        var installed = await deployment.GetPackagesAsync(account, CancellationToken.None).ConfigureAwait(false);
        foreach (var package in installed.Where(IsMicrosoftCopilot))
            await deployment.RemovePackageAsync(package.FullName, account, _ => { }, CancellationToken.None).ConfigureAwait(false);
        if ((await deployment.GetPackagesAsync(account, CancellationToken.None).ConfigureAwait(false)).Any(IsMicrosoftCopilot))
            throw new IOException("The standalone Copilot app is still registered.");

        static bool IsMicrosoftCopilot(AppxPackageIdentity package) =>
            package.Name.Equals("Microsoft.Copilot", StringComparison.OrdinalIgnoreCase) &&
            package.FullName.EndsWith("_8wekyb3d8bbwe", StringComparison.OrdinalIgnoreCase);
    }
}
