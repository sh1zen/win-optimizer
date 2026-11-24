using WinOptimizer.Application.Extensions.DI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WinOptimizer.Infrastructure.Extensions.DI;

namespace WinOptimizer.UI.Features.Common.Extensions.DI;

/// <summary>
/// The composition root for the win-optimizer WinUI 3 application.
/// Orchestrates the registration of all services following Clean Architecture principles.
/// </summary>
public static class CompositionRoot
{
    /// <summary>
    /// Configures all services for the win-optimizer application.
    /// </summary>
    /// <param name="services">The service collection to configure</param>
    /// <returns>The configured service collection for method chaining</returns>
    public static IServiceCollection ConfigureWinOptimizerServices(this IServiceCollection services, bool desktopWidgetHost = false)
    {
        // Register services in dependency order
        services
            .AddInfrastructureServices() // Infrastructure implementations
            .AddApplicationServices()    // Configuration workflows
            .AddUIServices(desktopWidgetHost); // UI-specific services (ThemeService, etc.)

        return services;
    }

    /// <summary>
    /// Creates and configures a host builder with the win-optimizer service configuration.
    /// </summary>
    /// <returns>Configured host builder</returns>
    public static IHostBuilder CreateWinOptimizerHost(bool desktopWidgetHost = false)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                services.ConfigureWinOptimizerServices(desktopWidgetHost);
            });
    }
}
