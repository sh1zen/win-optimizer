using System.Management;
using System.Runtime.InteropServices;
using WinOptimizer.Core.Features.Utilities;

namespace WinOptimizer.Infrastructure.Features.Utilities;

/// <summary>Disposable WMI boundary shared by battery inventory and OEM firmware providers.</summary>
internal interface IBatteryWmi
{
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string scope, string query);
    IReadOnlyDictionary<string, object?> Invoke(string scope, string className, string method,
        IReadOnlyDictionary<string, object> arguments, string? resultProperty, string? instanceName = null);
}

internal sealed class BatteryWmi : IBatteryWmi
{
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string scope, string query)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, query);
            searcher.Options.Timeout = TimeSpan.FromSeconds(8);
            using var results = searcher.Get();
            var values = new List<IReadOnlyDictionary<string, object?>>();
            foreach (ManagementObject item in results)
            {
                using (item) values.Add(item.Properties.Cast<PropertyData>().ToDictionary(p => p.Name, p => (object?)p.Value, StringComparer.OrdinalIgnoreCase));
            }
            return values;
        }
        catch (ManagementException ex) when (scope != @"root\cimv2" && ex.ErrorCode is ManagementStatus.InvalidNamespace or ManagementStatus.InvalidClass)
        { return []; }
        catch (Exception ex) when (ex is ManagementException { ErrorCode: ManagementStatus.AccessDenied } || ex is COMException { HResult: unchecked((int)0x80070005) })
        { throw new UnauthorizedAccessException("Windows denied access to the battery interface. Run as administrator and check BIOS permissions.", ex); }
        catch (Exception ex) when (ex is ManagementException or COMException)
        { throw new UtilityOperationException($"Battery firmware query failed ({scope}): {ex.Message}", ex); }
    }

    /// <summary>Invokes a method on the selected endpoint; each OEM owns the meaning of its output fields.</summary>
    public IReadOnlyDictionary<string, object?> Invoke(string scope, string className, string method,
        IReadOnlyDictionary<string, object> arguments, string? resultProperty, string? instanceName = null)
    {
        try
        {
            var query = "SELECT * FROM " + className;
            if (instanceName != null)
                query += " WHERE InstanceName='" + instanceName.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
            using var searcher = new ManagementObjectSearcher(scope, query);
            searcher.Options.Timeout = TimeSpan.FromSeconds(8);
            using var instances = searcher.Get();
            foreach (ManagementObject instance in instances)
            {
                using (instance)
                {
                    using var input = instance.GetMethodParameters(method);
                    foreach (var argument in arguments) input[argument.Key] = argument.Value;
                    using var output = instance.InvokeMethod(method, input, new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(8) });
                    if (output == null)
                        throw new UtilityOperationException("The firmware returned no result for the charge-limit change.");
                    var values = output.Properties.Cast<PropertyData>().ToDictionary(p => p.Name, p => (object?)p.Value, StringComparer.OrdinalIgnoreCase);
                    if (resultProperty != null)
                    {
                        if (values.GetValueOrDefault(resultProperty) is not { } status)
                            throw new UtilityOperationException("The firmware returned no result for the charge-limit change.");
                        var code = Convert.ToUInt32(status);
                        if (code != 0) throw new UtilityOperationException($"The firmware rejected the charge-limit change (code {code}). Check BIOS permissions/password and refresh before retrying.");
                    }
                    return values;
                }
            }
            throw new UtilityOperationException("The OEM battery firmware interface is unavailable.");
        }
        catch (Exception ex) when (ex is ManagementException { ErrorCode: ManagementStatus.AccessDenied } || ex is COMException { HResult: unchecked((int)0x80070005) })
        { throw new UnauthorizedAccessException("Windows denied access to the battery interface. Run as administrator and check BIOS permissions.", ex); }
        catch (Exception ex) when (ex is ManagementException or COMException)
        { throw new UtilityOperationException($"Battery firmware method failed ({className}.{method}): {ex.Message}", ex); }
    }
}
