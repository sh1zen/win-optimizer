using WinOptimizer.Core.Features.Utilities;

namespace WinOptimizer.Infrastructure.Features.Utilities;

/// <summary>Uses Acer's advertised battery health mode through its existing firmware WMI endpoint.</summary>
internal sealed class AcerBatteryFirmware(IBatteryWmi wmi) : IBatteryFirmwareProvider
{
    internal const string Scope = @"root\wmi";
    private const string ClassName = "BatteryControl";
    private const string Provider = "Acer Battery Health Mode";

    public bool Matches(string manufacturer) => manufacturer.Contains("Acer", StringComparison.OrdinalIgnoreCase);

    /// <summary>Offers the fixed 80%/100% modes only after reading the health-mode capability bit.</summary>
    public FirmwareChargeState Read()
    {
        var instance = FindInstance();
        if (instance == null) return new(Provider, [], null, BatteryControlIssue.InterfaceUnavailable);
        var output = wmi.Invoke(Scope, ClassName, "GetBatteryHealthControlStatus", new Dictionary<string, object>
        {
            ["uBatteryNo"] = (byte)1, ["uFunctionQuery"] = (byte)1, ["uReserved"] = new byte[2]
        }, null, instance);
        if (output.GetValueOrDefault("uFunctionList") is not byte functions)
            throw new UtilityOperationException("Acer returned no battery health-mode capabilities. Check the OEM firmware and driver.");
        if ((functions & 1) == 0) return new(Provider, [], null, BatteryControlIssue.FirmwareUnsupported);
        if (output.GetValueOrDefault("uFunctionStatus") is not byte[] { Length: 5 } status)
            throw new UtilityOperationException("Acer returned an invalid battery health-mode status. Refresh before retrying.");
        return new(Provider, [80, 100], status[0] != 0 ? 80 : 100);
    }

    /// <summary>Changes health mode alone; the service confirms the resulting setting through a fresh read.</summary>
    public void SetLimit(int percentage)
    {
        var enabled = percentage switch
        {
            80 => (byte)1,
            100 => (byte)0,
            _ => throw new ArgumentOutOfRangeException(nameof(percentage))
        };
        var instance = FindInstance() ?? throw new UtilityOperationException("The Acer battery control endpoint is unavailable.");
        // Acer's uReturn bytes have no documented success convention. A fresh health-mode read is authoritative.
        wmi.Invoke(Scope, ClassName, "SetBatteryHealthControl", new Dictionary<string, object>
        {
            ["uBatteryNo"] = (byte)1, ["uFunctionMask"] = (byte)1,
            ["uFunctionStatus"] = enabled, ["uReservedIn"] = new byte[5]
        }, null, instance);
    }

    /// <summary>Prefers the active APGe endpoint used by Predator firmware and rejects ambiguous controls.</summary>
    private string? FindInstance()
    {
        var active = wmi.Query(Scope, "SELECT * FROM " + ClassName + " WHERE Active=TRUE")
            .Where(item => item.GetValueOrDefault("Active") is true).ToArray();
        var preferred = active.Where(item => item.GetValueOrDefault("InstanceName") is string name &&
            name.Contains("APGe", StringComparison.OrdinalIgnoreCase)).ToArray();
        var candidates = preferred.Length > 0 ? preferred : active;
        if (candidates.Length > 1)
            throw new UtilityOperationException("Multiple Acer battery control endpoints are active; a unique endpoint could not be selected.");
        if (candidates.Length == 0) return null;
        return candidates[0].GetValueOrDefault("InstanceName") is string { Length: > 0 } instance ? instance :
            throw new UtilityOperationException("The Acer battery control endpoint has no instance name.");
    }
}
