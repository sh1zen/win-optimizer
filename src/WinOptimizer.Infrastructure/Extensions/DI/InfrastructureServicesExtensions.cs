using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WinOptimizer.Core.Features.Common.Events;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Infrastructure.Features.Common.Events;
using WinOptimizer.Infrastructure.Features.Common.EventHandlers;
using WinOptimizer.Infrastructure.Features.Common.Services;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.Infrastructure.Features.Utilities;

namespace WinOptimizer.Infrastructure.Extensions.DI;

/// <summary>
/// Extension methods for registering infrastructure services.
/// </summary>
public static class InfrastructureServicesExtensions
{
    /// <summary>
    /// Registers infrastructure services for the win-optimizer application.
    /// </summary>
    /// <param name="services">The service collection to configure</param>
    /// <returns>The service collection for method chaining</returns>
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services)
    {
        // Core Infrastructure Services (Singleton - Cross-cutting concerns)
        services.AddSingleton<IConfigImportState, ConfigImportState>();
        // One domain state owner shared by headless services and the WinUI shell.
        services.AddSingleton<WinOptimizer.Core.Features.Common.Services.ConfigReviewService>();
        services.AddSingleton<IConfigReviewService>(sp => sp.GetRequiredService<WinOptimizer.Core.Features.Common.Services.ConfigReviewService>());
        services.AddSingleton<IConfigReviewModeService>(sp => sp.GetRequiredService<WinOptimizer.Core.Features.Common.Services.ConfigReviewService>());
        services.AddSingleton<IConfigReviewDiffService>(sp => sp.GetRequiredService<WinOptimizer.Core.Features.Common.Services.ConfigReviewService>());
        services.AddSingleton<IConfigReviewBadgeService>(sp => sp.GetRequiredService<WinOptimizer.Core.Features.Common.Services.ConfigReviewService>());
        services.AddSingleton<IApplicationModeService>(sp => sp.GetRequiredService<WinOptimizer.Core.Features.Common.Services.ConfigReviewService>());
        services.AddSingleton<IProcessExecutor, ProcessExecutor>();
        services.AddSingleton<WinOptimizer.Core.Features.Security.ISecurityBackend, WinOptimizer.Infrastructure.Features.Security.WindowsSecurityBackend>();
        services.AddSingleton<WinOptimizer.Core.Features.Security.ISecurityScanService, WinOptimizer.Core.Features.Security.SecurityScanService>();
        services.AddSingleton<WinOptimizer.Infrastructure.Features.Drivers.DriverPowerShell>();
        services.AddSingleton<WinOptimizer.Infrastructure.Features.Drivers.IDriverInfInspector, WinOptimizer.Infrastructure.Features.Drivers.WindowsDriverInfInspector>();
        services.AddSingleton<WinOptimizer.Core.Features.Drivers.Interfaces.IDriverInventorySource, WinOptimizer.Infrastructure.Features.Drivers.WindowsDriverInventorySource>();
        services.AddSingleton<WinOptimizer.Core.Features.Drivers.Interfaces.IDriverUpdateSource, WinOptimizer.Infrastructure.Features.Drivers.WindowsDriverUpdateSource>();
        services.AddSingleton<WinOptimizer.Core.Features.Drivers.Interfaces.IDriverScanService, WinOptimizer.Core.Features.Drivers.Services.DriverScanService>();
        services.AddSingleton<WinOptimizer.Core.Features.Drivers.Interfaces.IDriverRemovalBackend, WinOptimizer.Infrastructure.Features.Drivers.WindowsDriverRemovalBackend>();
        services.AddSingleton<WinOptimizer.Core.Features.Drivers.Interfaces.IDriverRemovalService, WinOptimizer.Core.Features.Drivers.Services.DriverRemovalService>();
        services.AddSingleton<WinOptimizer.Infrastructure.Features.Cleaner.CleanerFileScanner>();
        services.AddSingleton<WinOptimizer.Core.Features.SoftwareApps.Interfaces.IUninstallResidueService,
            WinOptimizer.Infrastructure.Features.SoftwareApps.Uninstall.UninstallResidueService>();
        services.AddSingleton<WinOptimizer.Infrastructure.Features.SoftwareApps.Uninstall.IUninstallRegistryProfiles,
            WinOptimizer.Infrastructure.Features.SoftwareApps.Uninstall.WindowsUninstallRegistryProfiles>();
        services.AddSingleton<WinOptimizer.Infrastructure.Features.SoftwareApps.Uninstall.IUninstallAccountSessions,
            WinOptimizer.Infrastructure.Features.SoftwareApps.Uninstall.WindowsUninstallAccountSessions>();
        services.AddSingleton<WinOptimizer.Infrastructure.Features.SoftwareApps.Uninstall.IUninstallPackageSource,
            WinOptimizer.Infrastructure.Features.SoftwareApps.Uninstall.WindowsUninstallPackageSource>();
        services.AddSingleton<WinOptimizer.Infrastructure.Features.SoftwareApps.Uninstall.IUninstallScanFolders,
            WinOptimizer.Infrastructure.Features.SoftwareApps.Uninstall.WindowsUninstallScanFolders>();
        services.AddSingleton<WinOptimizer.Infrastructure.Features.SoftwareApps.Uninstall.IUninstallProcessSource,
            WinOptimizer.Infrastructure.Features.SoftwareApps.Uninstall.WindowsUninstallProcessSource>();
        services.AddSingleton<WinOptimizer.Infrastructure.Features.Cleaner.ICleanerShortcutReader, WinOptimizer.Infrastructure.Features.Cleaner.WindowsCleanerShortcutReader>();
        services.AddSingleton<WinOptimizer.Infrastructure.Features.Cleaner.ICleanerSystemFolders, WinOptimizer.Infrastructure.Features.Cleaner.WindowsCleanerSystemFolders>();
        services.AddSingleton<WinOptimizer.Infrastructure.Features.Cleaner.ICleanerProcessMonitor, WinOptimizer.Infrastructure.Features.Cleaner.WindowsCleanerProcessMonitor>();
        services.AddSingleton<WinOptimizer.Infrastructure.Features.Cleaner.ICleanerBackupVault, WinOptimizer.Infrastructure.Features.Cleaner.WindowsCleanerBackupVault>();
        services.AddSingleton<WinOptimizer.Core.Features.Cleaner.Interfaces.ICleanerBackupService, WinOptimizer.Infrastructure.Features.Cleaner.CleanerBackupService>();
        services.AddSingleton<WinOptimizer.Infrastructure.Features.Cleaner.CleanerRegistryScanner>();
        services.AddSingleton<WinOptimizer.Infrastructure.Features.Cleaner.WindowsCleanerRegistrySessions>();
        services.AddSingleton<WinOptimizer.Infrastructure.Features.Cleaner.ICleanerRegistrySessions>(provider =>
            provider.GetRequiredService<WinOptimizer.Infrastructure.Features.Cleaner.WindowsCleanerRegistrySessions>());
        services.AddSingleton<WinOptimizer.Infrastructure.Features.Cleaner.ICleanerRegistryProfiles, WinOptimizer.Infrastructure.Features.Cleaner.WindowsCleanerRegistryProfiles>();
        services.AddSingleton<WinOptimizer.Infrastructure.Features.Cleaner.ICleanerRegistryHiveAccess, WinOptimizer.Infrastructure.Features.Cleaner.WindowsCleanerRegistryHiveAccess>();
        services.AddSingleton<WinOptimizer.Infrastructure.Features.Cleaner.ICleanerRegistryRecovery>(sp => sp.GetRequiredService<WinOptimizer.Infrastructure.Features.Cleaner.CleanerRegistryScanner>());
        services.AddSingleton<WinOptimizer.Infrastructure.Features.Cleaner.IRegistryHiveOperations, WinOptimizer.Infrastructure.Features.Cleaner.WindowsRegistryHiveOperations>();
        services.AddSingleton<WinOptimizer.Infrastructure.Features.Cleaner.RegistryHiveCompactor>();
        services.AddSingleton<WinOptimizer.Core.Features.Cleaner.Interfaces.ICleanerBackend, WinOptimizer.Infrastructure.Features.Cleaner.WindowsCleanerBackend>();
        services.AddSingleton<WinOptimizer.Core.Features.Cleaner.Interfaces.ICleanerService, WinOptimizer.Core.Features.Cleaner.Services.CleanerService>();
        services.AddSingleton<ILogService, WinOptimizer.Core.Features.Common.Services.LogService>();
        services.AddSingleton<IInteractiveUserService, InteractiveUserService>();
        services.AddSingleton<AppDataStorage>();
        services.AddSingleton<ISystemInfoProvider, SystemInfoProvider>();
        services.AddSingleton<WinOptimizer.Infrastructure.Features.SystemInformation.ISystemInformationQuery, WinOptimizer.Infrastructure.Features.SystemInformation.WindowsSystemInformationQuery>();
        services.AddSingleton<WinOptimizer.Core.Features.SystemInformation.Interfaces.ISystemInformationService, WinOptimizer.Infrastructure.Features.SystemInformation.WindowsSystemInformationService>();
        services.AddSingleton<WinOptimizer.Infrastructure.Features.Extensions.HardwareMonitorSensors>();
        services.AddSingleton<WinOptimizer.Infrastructure.Features.Extensions.ISystemMonitorProbe, WinOptimizer.Infrastructure.Features.Extensions.WindowsSystemMonitorProbe>();
        services.AddSingleton<WinOptimizer.Core.Features.Extensions.ISystemMonitorService, WinOptimizer.Infrastructure.Features.Extensions.WindowsSystemMonitorService>();
        services.AddSingleton<WinOptimizer.Core.Features.Extensions.ISystemMonitorStartupService, WinOptimizer.Infrastructure.Features.Extensions.SystemMonitorStartupService>();
        services.AddSingleton<IWindowsRegistryService, WindowsRegistryService>();
        services.AddSingleton<WinOptimizer.Core.Features.Optimize.Interfaces.IOptimizationToolsService, WinOptimizer.Infrastructure.Features.Optimize.Services.OptimizationToolsService>();
        services.AddSingleton<WinOptimizer.Infrastructure.Features.Customize.Services.IContextMenuStore, WinOptimizer.Infrastructure.Features.Customize.Services.WindowsContextMenuStore>();
        services.AddSingleton<WinOptimizer.Core.Features.Customize.Interfaces.IContextMenuService, WinOptimizer.Infrastructure.Features.Customize.Services.ContextMenuService>();
        services.AddSingleton<IEnvironmentVariableStore, WindowsEnvironmentVariableStore>();
        services.AddSingleton<IEnvironmentEditorService, EnvironmentEditorService>();
        services.AddSingleton<IHostsEditorService, HostsEditorService>();
        services.AddSingleton<IBatteryWmi, BatteryWmi>();
        services.AddSingleton<IAsusChargeDevice, AsusChargeDevice>();
        services.AddSingleton<IBatteryFirmwareProvider, DellBatteryFirmware>();
        services.AddSingleton<IBatteryFirmwareProvider, HpBatteryFirmware>();
        services.AddSingleton<IBatteryFirmwareProvider, AsusBatteryFirmware>();
        services.AddSingleton<IBatteryFirmwareProvider, AcerBatteryFirmware>();
        services.AddSingleton<IBatteryChargeService, BatteryChargeService>();
        services.AddSingleton<IConnectionAdapterSource, WindowsConnectionAdapters>();
        services.AddSingleton<IConnectionService, ConnectionService>();
        services.AddSingleton<ISavedWifiSource, WindowsSavedWifi>();
        services.AddSingleton<ISavedWifiService, SavedWifiService>();
        services.AddSingleton<WinOptimizer.Infrastructure.Features.Management.IEventLogStore, WinOptimizer.Infrastructure.Features.Management.WindowsEventLogStore>();
        services.AddSingleton<WinOptimizer.Core.Features.Management.IEventLogCleanupService, WinOptimizer.Infrastructure.Features.Management.EventLogCleanupService>();
        services.AddSingleton<WinOptimizer.Infrastructure.Features.Management.IWindowsServicingBackend, WinOptimizer.Infrastructure.Features.Management.WindowsServicingBackend>();
        services.AddSingleton<WinOptimizer.Infrastructure.Features.Management.IAutorunBackend, WinOptimizer.Infrastructure.Features.Management.AutorunPowerShellBackend>();
        services.AddSingleton<WinOptimizer.Core.Features.Management.IAutorunService, WinOptimizer.Infrastructure.Features.Management.AutorunService>();
        services.AddSingleton<WinOptimizer.Infrastructure.Features.Management.WindowsProcessPorts>();
        services.AddSingleton<WinOptimizer.Infrastructure.Features.Management.IProcessMonitorSource, WinOptimizer.Infrastructure.Features.Management.WindowsProcessSource>();
        services.AddSingleton<WinOptimizer.Infrastructure.Features.Management.IProcessOpenFileSource, WinOptimizer.Infrastructure.Features.Management.WindowsProcessFiles>();
        services.AddSingleton<WinOptimizer.Infrastructure.Features.Management.IProcessNetworkSource, WinOptimizer.Infrastructure.Features.Management.WindowsProcessNetwork>();
        services.AddSingleton<WinOptimizer.Core.Features.Management.IProcessMonitorService, WinOptimizer.Infrastructure.Features.Management.ProcessMonitorService>();
        services.AddSingleton<WinOptimizer.Core.Features.Management.IWindowsServicingService, WinOptimizer.Infrastructure.Features.Management.WindowsServicingService>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IConnectionHistorySource, WindowsConnectionHistory>();
        services.AddSingleton<IConnectionTrafficService, ConnectionTrafficService>();
        // Dependency Manager
        services.AddSingleton<IDependencyManager, WinOptimizer.Core.Features.Common.Services.DependencyManager>();

        // Windows Services
        services.AddSingleton<IWindowsVersionService, WindowsVersionService>();
        services.AddSingleton<IWindowsUIManagementService, WindowsUIManagementService>();

        // User Preferences Service
        services.AddSingleton<IUserPreferencesService, UserPreferencesService>();

        // New Badge Service (tracks which settings are new in current release)

        // Localization Service
        services.AddSingleton<ILocalizationService, LocalizationService>();

        // Event Bus (Singleton - Message routing)
        services.AddSingleton<IEventBus, EventBus>();

        // Initialization Service
        services.AddSingleton<IInitializationService, WinOptimizer.Core.Features.Common.Services.InitializationService>();

        // Settings Registry
        services.AddSingleton<IGlobalSettingsRegistry, WinOptimizer.Core.Features.Common.Services.GlobalSettingsRegistry>();

        // Global Settings Preloader (registers bypassed settings in the global registry)
        services.AddSingleton<IGlobalSettingsPreloader, GlobalSettingsPreloader>();

        // File System Service
        services.AddSingleton<IFileSystemService, FileSystemService>();

        // Power Scheme Operations (P/Invoke wrapper for plan-level power operations)
        services.AddSingleton<IPowerSchemeOperations, PowerSchemeOperations>();

        // Explorer Window Manager (open/focus folders in Explorer)
        services.AddSingleton<IExplorerWindowManager, ExplorerWindowManager>();

        // User-facing structured change receipts (ChangeHistory.jsonl)
        services.AddSingleton<IChangeHistoryService, ChangeHistoryService>();

        // System Parameters (wraps User32 SystemParametersInfo P/Invoke)
        services.AddSingleton<ISystemParametersService, SystemParametersService>();

        // PowerShell Runner
        services.AddSingleton<IPowerShellRunner, WinOptimizer.Infrastructure.Features.Common.Utilities.PowerShellRunner>();

        // Driver Categorizer
        services.AddSingleton<WinOptimizer.Core.Features.AdvancedTools.Interfaces.IDriverCategorizer,
            WinOptimizer.Infrastructure.Features.AdvancedTools.Helpers.DriverCategorizer>();

        // Windows handlers and their dispatchers are composed in Infrastructure.
        services.AddSettingServices();
        services.AddSingleton<ISystemSettingsDiscoveryService, SystemSettingsDiscoveryService>();
        services.AddSingleton<IProcessRestartManager, ProcessRestartManager>();
        services.AddSingleton<IPowerCfgApplier, PowerCfgApplier>();
        services.AddSingleton<ISettingDependencyResolver, SettingDependencyResolver>();
        services.AddSingleton<IRecommendedSettingsApplier, RecommendedSettingsApplier>();
        services.AddSingleton<IBulkSettingsActionService, BulkSettingsActionService>();
        services.AddSingleton<ISettingOperationExecutor, SettingOperationExecutor>();
        services.AddSingleton<ISettingApplicationService, SettingApplicationService>();

        // ComboBox Services
        services.AddSingleton<IComboBoxSetupService, ComboBoxSetupService>();
        services.AddSingleton<IComboBoxResolver, ComboBoxResolver>();
        services.AddSingleton<IPowerPlanComboBoxService, PowerPlanComboBoxService>();

        // Settings Compatibility
        services.AddSingleton<ICompatibleSettingsRegistry, CompatibleSettingsRegistry>();
        services.AddSingleton<IWindowsCompatibilityFilter, WindowsCompatibilityFilter>();
        services.AddSingleton<IHardwareCompatibilityFilter, HardwareCompatibilityFilter>();
        services.AddSingleton<IHardwareDetectionService, HardwareDetectionService>();

        // Script Services
        services.AddSingleton<IPowerSettingsQueryService, PowerSettingsQueryService>();
        services.AddSingleton<IPowerSettingsValidationService, PowerSettingsValidationService>();

        // System Services
        services.AddSingleton<IScheduledTaskService, ScheduledTaskService>();
        services.AddSingleton<ISystemBackupService, SystemBackupService>();
        services.AddSingleton<ISystemRestoreService, SystemRestoreService>();
        services.AddSingleton<WinOptimizer.Infrastructure.Features.Common.Interfaces.ISystemRestorePointStore, WindowsRestorePointStore>();
        services.AddSingleton<IVersionService, VersionService>();
        services.AddSingleton<ISponsorsService, SponsorsService>();

        // Script Services
        services.AddSingleton<IRemovalScriptUpdateService, RemovalScriptUpdateService>();

        // Task Progress Service
        services.AddSingleton<TaskProgressService>();
        services.AddSingleton<ITaskProgressService>(sp => sp.GetRequiredService<TaskProgressService>());
        services.AddSingleton<IMultiScriptProgressService>(sp => sp.GetRequiredService<TaskProgressService>());

        // Tooltip Services
        services.AddSingleton<ITooltipDataService, TooltipDataService>();
        services.AddSingleton<TooltipRefreshEventHandler>();

        // Configuration Application Bridge (for config import/export)
        services.AddSingleton<IConfigurationApplicationBridgeService, ConfigurationApplicationBridgeService>();

        // Policy Cleanup Service (for Windows Defaults import)
        services.AddSingleton<IPolicyCleanupService, PolicyCleanupService>();

        // Advanced Tools Services — DISM Process Runner (shared utility)
        services.AddSingleton<IDismProcessRunner, DismProcessRunner>();

        // Advanced Tools Services — WIM/ISO decomposed services
        services.AddSingleton<WinOptimizer.Core.Features.AdvancedTools.Interfaces.IWimImageService,
            WinOptimizer.Infrastructure.Features.AdvancedTools.Services.WimImageService>();
        services.AddSingleton<WinOptimizer.Core.Features.AdvancedTools.Interfaces.IOscdimgToolManager,
            WinOptimizer.Infrastructure.Features.AdvancedTools.Services.OscdimgToolManager>();
        services.AddSingleton<WinOptimizer.Core.Features.AdvancedTools.Interfaces.IIsoService,
            WinOptimizer.Infrastructure.Features.AdvancedTools.Services.IsoService>();
        services.AddSingleton<WinOptimizer.Core.Features.AdvancedTools.Interfaces.IWimCustomizationService,
            WinOptimizer.Infrastructure.Features.AdvancedTools.Services.WimCustomizationService>();
        services.AddSingleton<WinOptimizer.Infrastructure.Features.AdvancedTools.Services.AutounattendScriptBuilder>();

        // Http Client
        services.TryAddSingleton<System.Net.Http.HttpClient>();

        return services;
    }
}
