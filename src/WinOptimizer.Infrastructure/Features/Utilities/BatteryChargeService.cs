using System.ComponentModel;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Utilities;

namespace WinOptimizer.Infrastructure.Features.Utilities;

/// <summary>Discovers OEM capabilities and publishes changes only after firmware readback confirms them.</summary>
internal sealed class BatteryChargeService(IBatteryWmi wmi, IEnumerable<IBatteryFirmwareProvider> providers,
    IApplicationModeService mode) : IBatteryChargeService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<BatteryChargeState> ReadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(Read).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<BatteryChargeState> SetLimitAsync(int percentage)
    {
        if (percentage is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(percentage));
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var reservation = mode.BeginSystemOperation();
            if (mode.CurrentMode != WinOptimizerMode.Normal) throw new InvalidOperationException("Battery changes require normal application mode.");
            return await Task.Run(() =>
            {
                var state = Read();
                if (!state.CanApply || !state.SupportedLimits.Contains(percentage))
                    throw new UtilityOperationException("The requested percentage is not supported by this device's firmware.");
                var provider = providers.Single(p => p.Matches(state.Manufacturer));
                try { provider.SetLimit(percentage); }
                catch (Exception ex) when (ex is UtilityOperationException or Win32Exception or UnauthorizedAccessException)
                { throw new UtilityOperationException($"The limit was not confirmed; some firmware changes may already be applied. Refresh before retrying. {ex.Message}", ex); }
                var actual = Read();
                if (actual.ActiveLimit != percentage)
                    throw new UtilityOperationException("The firmware has not confirmed the requested limit. Refresh and check the OEM charging utility or BIOS; a restart may be required.");
                return actual;
            }).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private BatteryChargeState Read()
    {
        var computer = wmi.Query(@"root\cimv2", "SELECT Manufacturer, Model FROM Win32_ComputerSystem").FirstOrDefault();
        var manufacturer = computer?.GetValueOrDefault("Manufacturer") as string ?? "";
        var model = computer?.GetValueOrDefault("Model") as string ?? "";
        var batteries = wmi.Query(@"root\cimv2", "SELECT EstimatedChargeRemaining FROM Win32_Battery");
        var values = batteries.Select(b => b.GetValueOrDefault("EstimatedChargeRemaining"))
            .Where(v => v != null).Select(v => Convert.ToInt32(v)).Where(v => v is >= 0 and <= 100).ToArray();
        int? charge = values.Length > 0 ? (int)Math.Round(values.Average()) : null;
        var provider = providers.SingleOrDefault(p => p.Matches(manufacturer));
        FirmwareChargeState firmware = new("", [], null);
        var issue = BatteryControlIssue.NoProvider;
        var diagnostic = "";
        if (batteries.Count > 0 && provider != null)
        {
            try
            {
                firmware = provider.Read();
                issue = firmware.Limits.Count > 0 ? BatteryControlIssue.None :
                    firmware.Issue == BatteryControlIssue.None ? BatteryControlIssue.FirmwareUnsupported : firmware.Issue;
            }
            catch (Exception ex) when (ex is UtilityOperationException or UnauthorizedAccessException or Win32Exception or IOException or FormatException or OverflowException or InvalidCastException)
            {
                diagnostic = ex.Message;
                issue = ex is UnauthorizedAccessException or Win32Exception { NativeErrorCode: 5 }
                    ? BatteryControlIssue.AccessDenied : BatteryControlIssue.DetectionFailed;
            }
        }
        var setup = BatterySupportCatalog.Find(manufacturer, model);
        return new(manufacturer, model, batteries.Count > 0, charge, firmware.Provider,
            firmware.Limits, firmware.ActiveLimit, diagnostic, setup?.HelpUri ?? new("https://support.microsoft.com/windows"))
        { Issue = issue, Setup = setup, Telemetry = batteries.Count > 0 ? BatteryTelemetryReader.Read(wmi) : null };
    }
}
