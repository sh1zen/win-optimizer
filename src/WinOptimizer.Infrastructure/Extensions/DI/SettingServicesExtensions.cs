using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Services;
using WinOptimizer.Core.Features.Customize.Interfaces;
using WinOptimizer.Core.Features.Optimize.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Infrastructure.Features.Common.Services;
using WinOptimizer.Infrastructure.Features.Customize.Services;
using WinOptimizer.Infrastructure.Features.Optimize.Services;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Services;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Services.WinGet;
using WinOptimizer.Core.Features.Common.Events;

namespace WinOptimizer.Infrastructure.Extensions.DI;

/// <summary>
/// Extension methods for registering setting services and dispatcher registries.
/// </summary>
public static class SettingServicesExtensions
{
    /// <summary>
    /// Registers all setting services for the win-optimizer application.
    /// </summary>
    public static IServiceCollection AddSettingServices(this IServiceCollection services)
    {
        services
            .AddCustomizationServices()
            .AddOptimizationServices()
            .AddSoftwareAppServices();

        // Id-keyed dispatcher registries â€” settingId â†’ handler mapping.
        services.AddSingleton<ISpecialSettingHandlerRegistry>(sp =>
            new SpecialSettingHandlerRegistry(new Dictionary<string, ISpecialSettingHandler>
            {
                [SettingIds.PowerPlanSelection] = sp.GetRequiredService<PowerService>(),
                [SettingIds.UpdatesPolicyMode]  = sp.GetRequiredService<UpdateService>(),
                [SettingIds.ThemeModeWindows]   = sp.GetRequiredService<ThemeWallpaperApplier>(),
            }));

        // Only handlers that override DiscoverSpecialSettingsAsync (i.e. self-filter
        // and return raw values) belong in the discovery registry.
        services.AddSingleton<ISpecialDiscoveryRegistry>(sp =>
            new SpecialDiscoveryRegistry(new List<ISpecialSettingHandler>
            {
                sp.GetRequiredService<PowerService>(),
                sp.GetRequiredService<UpdateService>(),
            }));

        return services;
    }

    /// <summary>
    /// Registers customization setting services.
    /// </summary>
    public static IServiceCollection AddCustomizationServices(this IServiceCollection services)
    {
        // Register WallpaperService (consumed by ThemeWallpaperApplier)
        services.AddSingleton<IWallpaperService, WallpaperService>();

        // Register ThemeWallpaperApplier (special handler for theme-mode-windows;
        // the explorer refresh is now declarative via SettingDefinition.RestartProcess).
        services.AddSingleton<ThemeWallpaperApplier>();

        return services;
    }

    /// <summary>
    /// Registers optimization setting services.
    /// </summary>
    public static IServiceCollection AddOptimizationServices(this IServiceCollection services)
    {
        // Register PowerService (keeps factory â€” IPowerService forwards to the concrete)
        services.AddSingleton<PowerService>(sp => new PowerService(
            sp.GetRequiredService<ILogService>(),
            sp.GetRequiredService<IPowerSettingsQueryService>(),
            sp.GetRequiredService<ICompatibleSettingsRegistry>(),
            sp.GetRequiredService<IEventBus>(),
            sp.GetRequiredService<IPowerPlanComboBoxService>(),
            sp.GetRequiredService<IProcessExecutor>(),
            sp.GetRequiredService<IFileSystemService>(),
            sp.GetRequiredService<IPowerSchemeOperations>(),
            sp.GetRequiredService<IConfigImportState>()
        ));
        services.AddSingleton<IPowerService>(sp => sp.GetRequiredService<PowerService>());

        // Register UpdateService
        services.AddSingleton<UpdateService>();

        return services;
    }

    /// <summary>
    /// Registers software apps setting services.
    /// </summary>
    public static IServiceCollection AddSoftwareAppServices(this IServiceCollection services)
    {
        // Software apps services (Singleton - consumed by Singleton ViewModels)
        services.AddSingleton<IWindowsAppsService, WindowsAppsService>();
        services.AddSingleton<IExternalAppsService, ExternalAppsService>();
        services.AddSingleton<IInstalledProgramSource, WindowsInstalledProgramSource>();
        services.AddSingleton<WinOptimizer.Infrastructure.Features.SoftwareApps.Leftover.IInstallerInventory,
            WinOptimizer.Infrastructure.Features.SoftwareApps.Leftover.WindowsInstallerInventory>();
        services.AddSingleton<WinOptimizer.Infrastructure.Features.SoftwareApps.Leftover.ILeftoverSource,
            WinOptimizer.Infrastructure.Features.SoftwareApps.Leftover.WindowsLeftoverSource>();
        services.AddSingleton<ILeftoverService, WinOptimizer.Infrastructure.Features.SoftwareApps.Leftover.LeftoverService>();
        services.AddSingleton<IAppInstallationService, AppInstallationService>();
        services.AddSingleton<IWindowsAppUninstallService, WindowsAppUninstallService>();

        // AppX package source (PackageManager COM â†’ WMI â†’ PowerShell fallback)
        services.AddSingleton<IAppxPackageSource, AppxPackageSource>();
        services.AddSingleton<IAppxDeployment, WindowsAppxDeployment>();
        services.AddSingleton<IWindowsComponentStatusSource, WindowsComponentStatusSource>();

        // AppX icon source (PackageManager + AppListEntry.DisplayInfo.GetLogo,
        // covers current-user / all-users / provisioned scopes)
        services.AddSingleton<IAppxIconSource, AppxIconSource>();
        services.AddSingleton<IDesktopAppIconSource, DesktopAppIconSource>();

        // App icon resolver (cache-first, called from WindowsAppsViewModel after install-status discovery)
        services.AddSingleton<IAppIconResolver, AppIconResolver>();

        // App Status Discovery Service (Singleton - Expensive operation)
        services.AddSingleton<IAppStatusDiscoveryService, AppStatusDiscoveryService>();
        services.AddSingleton<IAppMetadataSource, WindowsAppMetadataSource>();

        // WinGet decomposed services
        services.AddSingleton<WinGetComSession>();
        services.AddSingleton<IWinGetBootstrapper, WinGetBootstrapper>();
        services.AddSingleton<IWinGetDetectionService, WinGetDetectionService>();
        services.AddSingleton<IWinGetPackageInstaller, WinGetPackageInstaller>();

        // Chocolatey Services (Fallback package manager)
        services.AddSingleton<IChocolateyService, ChocolateyService>();

        // App Uninstall Service
        services.AddSingleton<IExternalAppUninstallService, ExternalAppUninstallService>();
        services.AddSingleton<IUninstallCommandSource, WindowsUninstallCommandSource>();

        // Store Download Service (Fallback for market-restricted apps)
        services.AddSingleton<IStoreDownloadService, StoreDownloadService>();

        // Direct Download Service (For non-WinGet apps)
        services.AddSingleton<IDirectDownloadService, DirectDownloadService>();

        // Legacy Capability and Optional Feature Services (Singleton)
        services.AddSingleton<ILegacyCapabilityService, LegacyCapabilityService>();
        services.AddSingleton<IOptionalFeatureService, OptionalFeatureService>();

        // App Removal Service (Singleton - Simplified removal logic)
        services.AddSingleton<IBloatRemovalService, BloatRemovalService>();

        return services;
    }

}
