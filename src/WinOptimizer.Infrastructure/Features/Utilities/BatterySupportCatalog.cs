using WinOptimizer.Core.Features.Utilities;

namespace WinOptimizer.Infrastructure.Features.Utilities;

/// <summary>Owns verified OEM installation routes independently of direct firmware-control capability.</summary>
internal static class BatterySupportCatalog
{
    internal static BatterySupportSetup? Find(string manufacturer, string model)
    {
        var name = manufacturer.Trim().ToUpperInvariant();
        return name switch
        {
            var value when value.Contains("ASUS") || value.Contains("ASUSTEK") => new(
                "ASUS System Control Interface + MyASUS", new("https://www.asus.com/support/download-center/"),
                new("https://www.asus.com/support/faq/1045651/"), BatterySupportSetupKind.DriverAndUtility),
            var value when value.Contains("ACER") => new(
                "AcerSense / Acer Care Center / PredatorSense", new("https://www.acer.com/us-en/support/drivers-and-manuals"),
                new("https://community.acer.com/en/kb/articles/19027-battery-charge-limit-function-on-acer-notebooks"), BatterySupportSetupKind.DriverAndUtility),
            var value when value.Contains("DELL") => new(
                "Dell Power Manager / Dell Optimizer", new("https://www.dell.com/support/home/en-us?app=drivers"),
                new("https://www.dell.com/support/manuals/en-us/command-powershell-provider/dellcommandpowershellprovider_ug_2.4/using-the-primarybattchargecfg-feature?guid=guid-934fb5f8-9e50-4d17-9d11-578d5e43aa84&lang=en-us"), BatterySupportSetupKind.OemUtility),
            var value when value == "HP" || value.Contains("HEWLETT") => new(
                "HP Battery Health Manager", new("https://support.hp.com/us-en/drivers"),
                new("https://support.hp.com/us-en/document/c06465959"), BatterySupportSetupKind.Firmware),
            var value when value.Contains("LENOVO") => new(
                "Lenovo Vantage + Lenovo power/ACPI driver", new("https://pcsupport.lenovo.com/us/en"),
                new("https://support.lenovo.com/us/en/solutions/ht513315"), BatterySupportSetupKind.DriverAndUtility),
            var value when value == "MSI" || value.Contains("MICRO-STAR") => new(
                "MSI Center / Dragon Center (Battery Master)", new("https://www.msi.com/support/download"),
                new("https://us.msi.com/support/technical_details/NB_Laptop_Battery"), BatterySupportSetupKind.OemUtility),
            var value when value.Contains("SAMSUNG") => new(
                "Samsung Settings", new("https://www.samsung.com/us/support/downloads/"),
                new("https://www.samsung.com/us/support/galaxy-battery/galaxy-book-battery/"), BatterySupportSetupKind.OemUtility),
            var value when value == "LG" || value.StartsWith("LG ELECTRONICS") => new(
                "LG Smart Assistant / LG Control Center", new("https://www.lg.com/us/support/software-firmware-drivers"),
                new("https://www.lg.com/us/support/help-library/lg-laptop-battery-battery-is-charged-only-up-to-80--20154629826640"), BatterySupportSetupKind.OemUtility),
            var value when value.Contains("MICROSOFT") && model.Contains("Surface", StringComparison.OrdinalIgnoreCase) => new(
                "Surface app", new("https://support.microsoft.com/en-us/surface/battery/smart-charging-on-surface"),
                new("https://support.microsoft.com/en-us/surface/battery/smart-charging-on-surface"), BatterySupportSetupKind.OemUtility),
            _ => null
        };
    }
}
