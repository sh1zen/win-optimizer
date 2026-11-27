using Microsoft.UI.Xaml.Controls;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Localization;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.AdvancedTools;
using WinOptimizer.UI.Features.Benchmark;
using WinOptimizer.UI.Features.Cleaner;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Models;
using WinOptimizer.UI.Features.Customize;
using WinOptimizer.UI.Features.Customize.Pages;
using WinOptimizer.UI.Features.Customize.ViewModels;
using WinOptimizer.UI.Features.Drivers;
using WinOptimizer.UI.Features.Disks;
using WinOptimizer.UI.Features.Extensions;
using WinOptimizer.UI.Features.Extensions.Notch;
using WinOptimizer.UI.Features.Extensions.Preview;
using WinOptimizer.UI.Features.Management;
using WinOptimizer.UI.Features.Optimize;
using WinOptimizer.UI.Features.Optimize.Pages;
using WinOptimizer.UI.Features.Optimize.ViewModels;
using WinOptimizer.UI.Features.Restore;
using WinOptimizer.UI.Features.Security;
using WinOptimizer.UI.Features.Settings;
using WinOptimizer.UI.Features.Settings.ViewModels;
using WinOptimizer.UI.Features.SoftwareApps;
using WinOptimizer.UI.Features.SystemInformation;
using WinOptimizer.UI.Features.TimeMachine;
using WinOptimizer.UI.Features.Utilities;

namespace WinOptimizer.UI.Features.Dashboard;

/// <summary>Associates searchable program components with their existing navigation destinations.</summary>
internal static class ProgramSearchCatalog
{
    internal static IReadOnlyList<ProgramSearchEntry> Create(ICompatibleSettingsRegistry registry, bool includeWindowsSettings)
    {
        var entries = new List<ProgramSearchEntry>();
        void Add<T>(string title, string? section = null, string? description = null, object? parameter = null,
            bool normalModeOnly = false, params string[] keywords) where T : Page =>
            entries.Add(new(title, section, description, typeof(T), parameter, normalModeOnly, keywords));

        void AddTools<T>(string section, params string[] titleKeys) where T : Page
        {
            foreach (var key in titleKeys) Add<T>(key, section);
        }

        Add<DashboardPage>("Nav_Dashboard");
        Add<BenchmarkPage>("Benchmark_Title", "Nav_Dashboard",
            keywords: ["Benchmark_CpuClock", "Benchmark_Flops", "Benchmark_MemoryBandwidth", "Benchmark_DiskWrite", "Benchmark_GpuFlops"]);
        Add<SoftwareAppsPage>("Nav_SoftwareAndApps");
        Add<SoftwareAppsPage>("SoftwareApps_Tab_WindowsApps", "Nav_SoftwareAndApps", parameter: SoftwareAppsTab.Windows,
            keywords: ["WindowsApps_Section_Apps", "WindowsApps_Section_Capabilities", "WindowsApps_Section_OptionalFeatures"]);
        Add<SoftwareAppsPage>("SoftwareApps_Tab_ExternalApps", "Nav_SoftwareAndApps", parameter: SoftwareAppsTab.External);
        Add<SoftwareAppsPage>("SoftwareApps_Tab_Leftover", "Nav_SoftwareAndApps", parameter: SoftwareAppsTab.Leftovers, normalModeOnly: true);

        Add<OptimizePage>("Nav_Optimize", description: "Category_Optimize_StatusText");
        Add<CustomizePage>("Nav_Customize", description: "Category_Customize_StatusText");
        var settings = includeWindowsSettings ? registry.GetAllBypassedSettings() : null;
        AddSettingsSections(typeof(OptimizePage), "Nav_Optimize", OptimizeViewModel.Sections, settings, entries);
        AddSettingsSections(typeof(CustomizePage), "Nav_Customize", CustomizeViewModel.Sections, settings, entries);
        Add<ContextMenuCustomizePage>("ContextMenu_Title", "Nav_Customize", "ContextMenu_TitleDescription");

        Add<NetworkOptimizePage>("Nav_Internet", description: "OptimizeNetwork_TitleDescription", keywords: ["OptimizeNetwork_Title"]);
        Add<NetworkOptimizePage>("SpeedTest_Title", "Nav_Internet", "SpeedTest_Description");
        AddTools<NetworkOptimizePage>("Nav_Internet", "Connection_Dns", "OptimizeNetwork_offload-task_Title", "Connection_Vpn");
        Add<FeaturesOptimizePage>("OptimizeFeatures_Title", "Nav_Optimize", "OptimizeFeatures_TitleDescription");
        AddTools<FeaturesOptimizePage>("OptimizeFeatures_Title", "OptimizeFeatures_OptionalServices", "OptimizeFeatures_Telemetry",
            "OptimizeFeatures_Cortana", "OptimizeFeatures_Copilot", "OptimizeFeatures_ActivityHistory", "OptimizeFeatures_Advertising");

        Add<CleanerPage>("Nav_Cleaner", description: "Cleaner_Description");
        AddTools<CleanerPage>("Nav_Cleaner", "Cleaner_Privacy", "Cleaner_JunkFiles", "Cleaner_Shortcuts", "Cleaner_Registry", "Cleaner_RegistryDefrag", "Cleaner_Disk");
        Add<EventsCleanupPage>("EventsCleanup_Title", "Nav_Cleaner", "EventsCleanup_TitleDescription");
        Add<DisksPage>("Nav_Disks", description: "Disks_Description");
        foreach (var tool in DiskToolCatalog.Tools)
            Add<DiskToolPage>(tool.TitleKey(), "Nav_Disks", tool.DescriptionKey(), parameter: tool);
        Add<DriversPage>("Nav_Drivers", keywords: ["Drivers_DetectDevicesStep", "Drivers_CheckSourcesStep", "Drivers_Updates"]);
        Add<SecurityPage>("Security_Title", description: "Security_TitleDescription");
        AddTools<SecurityPage>("Security_Title", "Security_Area_Antivirus", "Security_Area_Firewall", "Security_Area_Spyware",
            "Security_CoverageDefender", "Security_CoverageUpdates", "Security_DeepScan");
        Add<WindowsStatusPage>("WindowsStatus_Title", description: "WindowsStatus_TitleDescription");
        AddTools<WindowsStatusPage>("WindowsStatus_Title", "WindowsStatus_ScanTitle", "WindowsStatus_RepairTitle");

        Add<ManagementPage>("Nav_Management", description: "Management_Description");
        Add<EnvironmentEditorPage>("Utilities_Environment", "Nav_Management", "Utilities_EnvironmentDescription");
        Add<HostsEditorPage>("Utilities_Hosts", "Nav_Management", "Utilities_HostsDescription");
        Add<BatteryChargePage>("Utilities_Battery", "Nav_Management", "Utilities_BatteryDescription");
        Add<ConnectionPage>("Utilities_Connection", "Nav_Management", "Utilities_ConnectionDescription");
        Add<SavedWifiPage>("Connection_SavedWifi", "Utilities_Connection", "Connection_SavedWifiDescription");
        Add<AutorunsPage>("Autoruns_Title", "Nav_Management", "Autoruns_TitleDescription");
        Add<ProcessMonitorPage>("ProcessMonitor_Title", "Nav_Management", "ProcessMonitor_TitleDescription");

        Add<UtilitiesPage>("Nav_Functionalities", description: "Utilities_Description");
        Add<SystemMonitorSettingsPage>("SysMonitor_Title", "Nav_Functionalities", "SysMonitor_Description",
            keywords: ["SysMonitor_SensorsTitle", "SysMonitor_BackgroundTitle"]);
        Add<NotchSettingsPage>("Notch_Title", "Nav_Functionalities", "Notch_Description", keywords: ["Notch_Shape", "Notch_WeatherCity"]);
        Add<NotchSysInfoPage>("Notch_SysInfo", "Notch_Title", "Notch_SysInfoDescription");
        Add<PreviewPage>("QuickPreview_Title", "Nav_Functionalities", "QuickPreview_Description");

        Add<AdvancedToolsPage>("Nav_AdvancedTools", description: "Category_AdvancedTools_StatusText", normalModeOnly: true);
        Add<AdvancedToolsPage>("WIMUtil_Title", "Nav_AdvancedTools", "WIMUtil_Subtitle", new SectionPageNavigation("WimUtil"), true,
            "WIMUtil_Step1_Title", "WIMUtil_Step4_Title", "WIMUtil_Card_ConvertImage_Title");
        Add<AdvancedToolsPage>("AdvancedTools_MenuItem_CreateXML", "Nav_AdvancedTools", "AdvancedTools_GenerateCard_Description",
            new SectionPageNavigation("AutounattendXml"), true);
        Add<AdvancedToolsPage>("Experimental_Title", "Nav_AdvancedTools", "Experimental_Description", new SectionPageNavigation("ExperimentalFeatures"), true);
        Add<SystemInformationPage>("Nav_SystemInformation", "Nav_AdvancedTools", "SystemInfo_Description", normalModeOnly: true);
        foreach (var category in Enum.GetValues<WinOptimizer.Core.Features.SystemInformation.Models.SystemInformationCategory>())
            Add<SystemInformationPage>("SystemInfo_Section_" + category, "Nav_SystemInformation", parameter: category, normalModeOnly: true);
        Add<RestorePage>("Nav_Restore", "Nav_AdvancedTools", "Restore_Description", normalModeOnly: true,
            keywords: ["Restore_PointsTitle", "Settings_CreateRestorePoint_Button", "Settings_BackupRestore_Title"]);
        Add<TimeMachinePage>("TimeMachine_Title");

        Add<SettingsPage>("Nav_Settings");
        foreach (var category in SettingsViewModel.CategoryDefinitions)
        {
            var destination = new SettingsPageNavigation(category.Category, "");
            Add<SettingsPage>(category.TitleKey, "Nav_Settings", category.DescriptionKey, destination,
                keywords: [..category.SearchKeys, ..category.TrailingSearchKeys]);
        }
        return entries;
    }

    /// <summary>Indexes the same catalogue and detail keys used by Optimize and Customize.</summary>
    private static void AddSettingsSections(Type page, string ownerKey, IEnumerable<ISectionInfo> sections,
        IReadOnlyDictionary<string, IEnumerable<SettingDefinition>>? definitions, List<ProgramSearchEntry> entries)
    {
        foreach (var section in sections)
        {
            var feature = section.ModuleId == FeatureIds.ExplorerCustomization ? "Explorer" : section.ModuleId;
            string titleKey = $"Feature_{feature}_Name";
            entries.Add(new(titleKey, ownerKey, null, page, new SectionPageNavigation(section.Key), false, [], section.DisplayName));
            if (definitions is null || !definitions.TryGetValue(section.ModuleId, out var settings)) continue;
            foreach (var setting in settings)
            {
                entries.Add(new(SettingLocalizationKeys.Name(setting), titleKey, SettingLocalizationKeys.Description(setting), page,
                    new SectionPageNavigation(section.Key, Setting: setting), false, [], setting.Name, setting.Description, setting.Id));
            }
        }
    }
}

/// <summary>Stores translation keys independently of the language used to display results.</summary>
internal sealed record ProgramSearchEntry(string TitleKey, string? SectionKey, string? DescriptionKey, Type PageType,
    object? Parameter, bool NormalModeOnly, IReadOnlyList<string> Keywords, string? FallbackTitle = null,
    string? FallbackDescription = null, string? SettingId = null);
