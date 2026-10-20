using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WinOptimizer.Application.Configuration;
using WinOptimizer.Application.Updates;
using WinOptimizer.Core.Features.Common.Interfaces;

namespace WinOptimizer.Application.Extensions.DI;

/// <summary>Registers application workflows independently of Windows and UI implementations.</summary>
public static class ApplicationServicesExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddSingleton<IConfigExportService, ConfigExportService>();
        services.AddSingleton<IConfigLoadService, ConfigLoadService>();
        services.AddSingleton<IConfigApplicationExecutionService, ConfigApplicationExecutionService>();
        services.AddSingleton<IConfigReviewOrchestrationService, ConfigReviewOrchestrationService>();
        services.AddSingleton<IConfigurationService, ConfigurationService>();
        services.AddSingleton<IActivityJournalService, WinOptimizer.Application.Journal.ActivityJournalService>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IBackgroundUpdateCheckService, BackgroundUpdateCheckService>();
        services.AddSingleton<WinOptimizer.Core.Features.SoftwareApps.Interfaces.IAppUninstallWorkflow,
            WinOptimizer.Application.SoftwareApps.AppUninstallWorkflow>();
        return services;
    }
}
