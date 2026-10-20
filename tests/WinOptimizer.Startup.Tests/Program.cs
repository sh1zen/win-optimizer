using WinOptimizer.UI.Tests.Helpers;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.UI.Tests.Services;
using WinOptimizer.UI.Tests.Converters;
using WinOptimizer.UI.Tests.ViewModels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Extensions;
using WinOptimizer.UI.Features.Settings.ViewModels;
using WinUIApplication = Microsoft.UI.Xaml.Application;

namespace WinOptimizer.StartupTests;

/// <summary>Runs production startup, rendering and section-navigation checks with fake OS boundaries.</summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        WinUIApplication.Start(initialization =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            var resultPath = Path.GetFullPath(args[0]);
            try { _ = new StartupTestApplication(resultPath); }
            catch (Exception error)
            {
                File.WriteAllText(resultPath, error.ToString());
                Console.WriteLine(error);
                Environment.Exit(1);
            }
        });
    }
}

public sealed partial class StartupTestApplication(string resultPath) : WinOptimizer.UI.App(CreateTestHost())
{
    /// <summary>Supplies synthetic service boundaries to pages activated by the production XAML factory.</summary>
    private static IHost CreateTestHost() => new HostBuilder().ConfigureServices(services =>
    {
        services.AddTransient<SettingsViewModel>(_ => new SettingsViewModelTests().CreateViewModel());
        var localization = new Mock<ILocalizationService>();
        localization.Setup(service => service.GetString(It.IsAny<string>())).Returns((string key) => key);
        services.AddSingleton(localization.Object);
        services.AddSingleton(Mock.Of<ISystemMonitorWidgetHost>());
    }).Build();
    private Window? _window;
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        InitializeComponent();
        InitializeShellResources();
        UnhandledException += (_, error) => Finish(error.Exception.ToString());
        var content = new Grid();
        _window = new Window { Content = content };
        var deadline = _window.DispatcherQueue.CreateTimer(); deadline.Interval = TimeSpan.FromSeconds(60);
        deadline.Tick += (_, _) => Finish("Startup tests timed out."); deadline.Start();
        content.Loaded += async (_, _) =>
        {
            try
            {
                var count = await NativeTestCases.RunAsync(typeof(CustomIconTests), typeof(SettingViewModelFactoryTests), typeof(TechnicalDetailsManagerTests), typeof(StartupOrchestratorTests), typeof(StartupPresentationTests), typeof(SectionNavigationTests), typeof(NavigationRouterTests), typeof(SavedWifiViewModelTests), typeof(SavedWifiPageTests), typeof(SettingsPageTests));
                Finish($"PASS: {count} startup, native first-frame and navigation cases. Fake OS boundaries only.", true);
            }
            catch (Exception ex) { Finish(ex.ToString()); }
        };
        _window.Activate();
    }
    private void Finish(string message, bool passed = false)
    {
        File.WriteAllText(resultPath, message); Console.WriteLine(message); Environment.Exit(passed ? 0 : 1);
    }
}
