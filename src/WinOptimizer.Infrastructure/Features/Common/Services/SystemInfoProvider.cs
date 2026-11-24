using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Common.Native;

namespace WinOptimizer.Infrastructure.Features.Common.Services;

public class SystemInfoProvider : ISystemInfoProvider
{
    private readonly IInteractiveUserService _interactiveUserService;
    private readonly Func<string?, string, IEnumerable<Func<string, object?>>> _readWmi;

    public SystemInfoProvider(IInteractiveUserService interactiveUserService)
        : this(interactiveUserService, ReadWmi) { }

    internal SystemInfoProvider(IInteractiveUserService interactiveUserService,
        Func<string?, string, IEnumerable<Func<string, object?>>> readWmi)
    {
        _interactiveUserService = interactiveUserService
            ?? throw new ArgumentNullException(nameof(interactiveUserService));
        _readWmi = readWmi;
    }

    /// <summary>Exposes WMI properties while the corresponding native record is still open.</summary>
    private static IEnumerable<Func<string, object?>> ReadWmi(string? scope, string query)
    {
        using var searcher = scope is null ? new ManagementObjectSearcher(query) : new ManagementObjectSearcher(scope, query);
        using var collection = searcher.Get();
        foreach (ManagementObject row in collection)
        {
            using (row) yield return property => row[property];
        }
    }

    public SystemInfo Collect()
    {
        // Query Win32_ComputerSystem once for DeviceType, RAM, and DomainJoined
        string deviceType = "Unknown";
        string ram = "Unknown";
        string domainJoined = "Unknown";
        CollectComputerSystemInfo(ref deviceType, ref ram, ref domainJoined);

        return new SystemInfo
        {
            AppVersion = CollectAppVersion(),
            OperatingSystem = CollectOperatingSystem(),
            Architecture = CollectArchitecture(),
            DeviceType = deviceType,
            Cpu = CollectCpu(),
            Ram = ram,
            Gpu = CollectGpu(),
            DotNetRuntime = CollectDotNetRuntime(),
            Elevation = CollectElevation(),
            FirmwareType = CollectFirmwareType(),
            SecureBoot = CollectSecureBoot(),
            Tpm = CollectTpm(),
            DomainJoined = domainJoined
        };
    }

    private static string CollectAppVersion()
    {
        try
        {
            var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            string? location = assembly.Location;

            if (string.IsNullOrEmpty(location))
                return "Unknown";

            var versionInfo = FileVersionInfo.GetVersionInfo(location);
            string version = versionInfo.ProductVersion ?? versionInfo.FileVersion ?? "Unknown";

            // Trim build metadata
            int plusIndex = version.IndexOf('+');
            if (plusIndex > 0)
                version = version.Substring(0, plusIndex);

            return version;
        }
        catch
        {
            return "Unknown";
        }
    }

    private static string CollectOperatingSystem()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key == null)
                return "Unknown";

            string productName = key.GetValue("ProductName") as string ?? "Windows";
            string displayVersion = key.GetValue("DisplayVersion") as string ?? "";
            string buildNumber = key.GetValue("CurrentBuildNumber") as string ?? "";
            var ubr = key.GetValue("UBR");
            string ubrStr = ubr != null ? $".{ubr}" : "";

            // Registry ProductName still says "Windows 10" on Win11 machines.
            // Build 22000+ is Windows 11.
            if (int.TryParse(buildNumber, out int build) && build >= 22000
                && productName.Contains("Windows 10"))
            {
                productName = productName.Replace("Windows 10", "Windows 11");
            }

            string result = productName;
            if (!string.IsNullOrEmpty(displayVersion))
                result += $" {displayVersion}";
            if (!string.IsNullOrEmpty(buildNumber))
                result += $" (Build {buildNumber}{ubrStr})";

            return result;
        }
        catch
        {
            return "Unknown";
        }
    }

    private static string CollectArchitecture()
    {
        try
        {
            return RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.X86 => "x86",
                Architecture.Arm64 => "arm64",
                Architecture.Arm => "arm",
                _ => RuntimeInformation.OSArchitecture.ToString()
            };
        }
        catch
        {
            return "Unknown";
        }
    }

    private void CollectComputerSystemInfo(
        ref string deviceType, ref string ram, ref string domainJoined)
    {
        try
        {
            foreach (var property in _readWmi(null, "SELECT PCSystemType, Model, Manufacturer, TotalPhysicalMemory, PartOfDomain, Domain FROM Win32_ComputerSystem"))
            {
                // DeviceType
                try
                {
                    if (property("PCSystemType") != null)
                    {
                        int pcSystemType = Convert.ToInt32(property("PCSystemType"));
                        deviceType = pcSystemType switch
                        {
                            1 => "Desktop",
                            2 => "Laptop",
                            3 => "Workstation",
                            4 => "Enterprise Server",
                            5 => "SOHO Server",
                            7 => "Performance Server",
                            8 => "Slate",
                            _ => $"Other ({pcSystemType})"
                        };
                    }

                    // Detect virtual machines from Model/Manufacturer
                    string model = (property("Model") as string ?? "").Trim();
                    string manufacturer = (property("Manufacturer") as string ?? "").Trim();
                    if (IsVirtualMachine(model, manufacturer))
                        deviceType += " (Virtual Machine)";
                }
                catch { /* field failure */ }

                // RAM
                try
                {
                    if (property("TotalPhysicalMemory") != null)
                    {
                        long totalBytes = Convert.ToInt64(property("TotalPhysicalMemory"));
                        int totalGb = (int)Math.Round(totalBytes / (1024.0 * 1024 * 1024));
                        ram = $"{totalGb} GB";
                    }
                }
                catch { /* field failure */ }

                // Domain
                try
                {
                    if (property("PartOfDomain") != null)
                    {
                        bool partOfDomain = Convert.ToBoolean(property("PartOfDomain"));
                        if (partOfDomain)
                        {
                            string domain = property("Domain") as string ?? "";
                            domainJoined = $"Yes ({domain})";
                        }
                        else
                        {
                            domainJoined = "No";
                        }
                    }
                }
                catch { /* field failure */ }
            }

            // Fallback to ChassisTypes if PCSystemType wasn't conclusive
            if (deviceType == "Unknown" || deviceType.StartsWith("Other"))
            {
                try
                {
                    string chassisResult = CollectDeviceTypeFromChassis();
                    if (chassisResult != "Unknown")
                        deviceType = chassisResult;
                }
                catch { /* fallback failure */ }
            }
        }
        catch
        {
            // WMI query failed entirely — all three stay "Unknown"
        }
    }

    private static readonly int[] LaptopChassisTypes = { 3, 8, 9, 10, 11, 14, 30, 31, 32 };

    private static readonly string[] VmIndicators =
    {
        "virtual", "vmware", "virtualbox", "vbox", "hyper-v",
        "qemu", "kvm", "xen", "parallels", "bhyve"
    };

    private static bool IsVirtualMachine(string model, string manufacturer)
    {
        string combined = $"{model} {manufacturer}".ToLowerInvariant();
        foreach (var indicator in VmIndicators)
        {
            if (combined.Contains(indicator))
                return true;
        }
        return false;
    }

    private string CollectDeviceTypeFromChassis()
    {
        try
        {
            foreach (var property in _readWmi(null, "SELECT ChassisTypes FROM Win32_SystemEnclosure"))
            {
                if (property("ChassisTypes") is Array chassisTypes && chassisTypes.Length > 0)
                {
                    foreach (var chassisType in chassisTypes)
                    {
                        int type = Convert.ToInt32(chassisType);
                        if (LaptopChassisTypes.Contains(type))
                            return "Laptop";
                    }
                    return "Desktop";
                }
            }
        }
        catch { /* fallback failure */ }

        return "Unknown";
    }

    private string CollectCpu()
    {
        try
        {
            foreach (var property in _readWmi(null, "SELECT Name, NumberOfLogicalProcessors FROM Win32_Processor"))
            {
                string name = (property("Name") as string ?? "Unknown").Trim();
                int cores = Convert.ToInt32(property("NumberOfLogicalProcessors") ?? 0);
                return cores > 0 ? $"{name} ({cores} cores)" : name;
            }
        }
        catch { /* query failure */ }

        return "Unknown";
    }

    private string CollectGpu()
    {
        try
        {
            var gpus = new System.Collections.Generic.List<string>();

            foreach (var property in _readWmi(null, "SELECT Name, AdapterDACType FROM Win32_VideoController"))
            {
                string name = (property("Name") as string ?? "").Trim();
                if (string.IsNullOrEmpty(name))
                    continue;

                string dacType = (property("AdapterDACType") as string ?? "").Trim();
                string gpuType = ClassifyGpu(name, dacType);
                gpus.Add($"{name} ({gpuType})");
            }

            return gpus.Count > 0 ? string.Join("; ", gpus) : "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    private static string ClassifyGpu(string name, string dacType)
    {
        string nameLower = name.ToLowerInvariant();

        // "Internal" DAC type is a strong signal for integrated
        if (dacType.Equals("Internal", StringComparison.OrdinalIgnoreCase))
            return "Integrated";

        // Name-based heuristics for integrated GPUs
        if (nameLower.Contains("intel") && (nameLower.Contains("uhd") || nameLower.Contains("hd graphics")
            || nameLower.Contains("iris")))
            return "Integrated";
        if (nameLower.Contains("radeon") && nameLower.Contains("graphics")
            && !nameLower.Contains("rx "))
            return "Integrated";

        return "Dedicated";
    }

    private static string CollectDotNetRuntime()
    {
        try
        {
            return RuntimeInformation.FrameworkDescription;
        }
        catch
        {
            return "Unknown";
        }
    }

    private string CollectElevation()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            bool isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);

            if (!isAdmin)
                return "Standard";

            return _interactiveUserService.IsOtsElevation ? "Admin (OTS)" : "Admin";
        }
        catch
        {
            return "Unknown";
        }
    }

    private static string CollectFirmwareType()
    {
        try
        {
            if (Kernel32Api.GetFirmwareType(out var firmwareType))
            {
                return firmwareType switch
                {
                    Kernel32Api.FirmwareType.Bios => "Legacy BIOS",
                    Kernel32Api.FirmwareType.Uefi => "UEFI",
                    _ => "Unknown"
                };
            }
        }
        catch { /* P/Invoke failure */ }

        return "Unknown";
    }

    private static string CollectSecureBoot()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            if (key == null)
                return "Not Supported";

            var value = key.GetValue("UEFISecureBootEnabled");
            if (value != null)
            {
                int enabled = Convert.ToInt32(value);
                return enabled == 1 ? "Enabled" : "Disabled";
            }
        }
        catch { /* registry failure */ }

        return "Unknown";
    }

    private string CollectTpm()
    {
        try
        {
            foreach (var property in _readWmi(@"root\cimv2\Security\MicrosoftTpm", "SELECT SpecVersion FROM Win32_Tpm"))
            {
                string specVersion = property("SpecVersion") as string ?? "";
                if (!string.IsNullOrEmpty(specVersion))
                {
                    // SpecVersion is like "2.0, 0, 1.59" — take the major part
                    string major = specVersion.Split(',')[0].Trim();
                    return major;
                }
            }
        }
        catch { /* WMI failure — TPM may not exist */ }

        return "Not Detected";
    }
}
