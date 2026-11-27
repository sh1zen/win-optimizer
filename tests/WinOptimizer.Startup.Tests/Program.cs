using WinOptimizer.UI.Tests.Helpers;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.UI.Tests.Services;
using WinOptimizer.UI.Tests.Converters;
using WinOptimizer.UI.Tests.ViewModels;
using WinOptimizer.UI.Tests.Utilities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using WinOptimizer.Core.Features.AdvancedTools.Experimental;
using WinOptimizer.Core.Features.Benchmark;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Disks;
using WinOptimizer.Core.Features.Extensions;
using WinOptimizer.Core.Features.Management;
using WinOptimizer.Core.Features.SystemInformation.Interfaces;
using WinOptimizer.Core.Features.SystemInformation.Models;
using WinOptimizer.Infrastructure.Features.Extensions;
using WinOptimizer.UI.Features.AdvancedTools.ViewModels;
using WinOptimizer.UI.Features.Benchmark;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Dashboard;
using WinOptimizer.UI.Features.Disks;
using WinOptimizer.UI.Features.Extensions;
using WinOptimizer.UI.Features.Extensions.Notch;
using WinOptimizer.UI.Features.Extensions.Preview;
using WinOptimizer.UI.Features.Extensions.ViewModels;
using WinOptimizer.UI.Features.Management.ViewModels;
using WinOptimizer.UI.Features.Settings.ViewModels;
using WinOptimizer.UI.Features.Utilities.ViewModels;
using WinUIApplication = Microsoft.UI.Xaml.Application;

namespace WinOptimizer.StartupTests;

/// <summary>Runs production startup, rendering and section-navigation checks with fake OS boundaries.</summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Load the production module's Windows App SDK runtime before invoking WinUI.
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(typeof(WinOptimizer.UI.App).Module.ModuleHandle);
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
        services.AddTransient(_ => new HostsEditorViewModelTests().Create());
        services.AddTransient(_ => new EnvironmentEditorViewModelTests().Create());
        var localization = new Mock<ILocalizationService>();
        localization.Setup(service => service.GetString(It.IsAny<string>())).Returns((string key) => key);
        localization.Setup(service => service.GetString(It.Is<string>(key => key.StartsWith("Dashboard_", StringComparison.Ordinal) ||
            key.StartsWith("Benchmark_", StringComparison.Ordinal) || key.StartsWith("Disks_", StringComparison.Ordinal)), It.IsAny<object[]>()))
            .Returns((string key, object[] args) => $"{key} · {string.Join(" · ", args)}");
        localization.Setup(service => service.ReadTranslations(It.IsAny<IReadOnlySet<string>>()))
            .Returns(new Dictionary<string, IReadOnlyList<string>> { ["WindowsStatus_Title"] = ["Ripara", "Reparación", "修復"] });
        services.AddSingleton(localization.Object);
        services.AddSingleton(Mock.Of<ICompatibleSettingsRegistry>());
        services.AddSingleton<ProgramSearchService>();
        services.AddSingleton(_ => new Mock<IBenchmarkService>(MockBehavior.Strict));
        services.AddSingleton(provider => provider.GetRequiredService<Mock<IBenchmarkService>>().Object);
        services.AddTransient<BenchmarkViewModel>();
        services.AddSingleton(_ => new Mock<IDiskToolsService>(MockBehavior.Strict));
        services.AddSingleton(provider => provider.GetRequiredService<Mock<IDiskToolsService>>().Object);
        services.AddSingleton<DiskToolSessions>();
        services.AddSingleton(Mock.Of<ITaskProgressService>());
        services.AddSingleton(Mock.Of<IDialogService>());
        services.AddSingleton(Mock.Of<IFilePickerService>());
        services.AddSingleton(Mock.Of<ISystemMonitorWidgetHost>());
        services.AddSingleton<AdvancedToolsViewModel>();
        var extensionPreferences = new Mock<IUserPreferencesService>();
        extensionPreferences.Setup(service => service.GetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>()))
            .ReturnsAsync(new SystemMonitorOptions());
        extensionPreferences.Setup(service => service.ReadSnapshotAsync())
            .ReturnsAsync(new UserPreferencesSnapshot(new Dictionary<string, object>(), Mock.Of<ILogService>()));
        var monitorStartup = new Mock<ISystemMonitorStartupService>();
        monitorStartup.Setup(service => service.ApplyAsync(It.IsAny<SystemMonitorOptions>()))
            .ReturnsAsync(OperationResult.Succeeded());
        var dispatcher = new ExtensionTestDispatcher();
        var preview = new PreviewSettingsSnapshot(false, false);
        services.AddSingleton(new UtilitiesCatalogViewModel(localization.Object, dispatcher));
        services.AddSingleton(provider => new ExtensionsViewModel(extensionPreferences.Object, localization.Object,
            provider.GetRequiredService<ISystemMonitorWidgetHost>(), dispatcher, monitorStartup.Object));
        services.AddSingleton(provider => NotchExtension.CreateForShell(extensionPreferences.Object, localization.Object,
            provider.GetRequiredService<ILogService>(), Mock.Of<INotchStartupService>(), httpClient: null));
        services.AddSingleton(new PreviewSettingsViewModel(localization.Object, () => Task.FromResult(preview),
            (_, _) => Task.FromResult(preview), _ => Task.FromResult(preview), _ => Task.FromResult(preview)));
        services.AddSingleton<Mock<IExperimentalFeatures>>();
        services.AddSingleton(provider => provider.GetRequiredService<Mock<IExperimentalFeatures>>().Object);
        services.AddSingleton(Mock.Of<IApplicationModeService>(mode => mode.CurrentMode == WinOptimizerMode.Normal));
        services.AddSingleton(Mock.Of<ILogService>());
        var startup = new Mock<IStartupOrchestrator>(MockBehavior.Strict);
        startup.Setup(service => service.RunStartupSequenceAsync(It.IsAny<IProgress<string>>(), It.IsAny<IProgress<TaskProgressDetail>>()))
            .ReturnsAsync(new StartupResult());
        startup.Setup(service => service.RunDeferredInitializationAsync()).ReturnsAsync(new StartupResult());
        services.AddSingleton(startup);
        services.AddSingleton(startup.Object);

        var inventory = new Mock<ISystemInformationService>(MockBehavior.Strict);
        inventory.Setup(service => service.ReadSectionsAsync(It.IsAny<IReadOnlyCollection<SystemInformationCategory>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new(SystemInformationCategory.Machine, [new("Example PC",
                    [new("SystemInfo_Field_Manufacturer", new SystemInformationText("Example manufacturer")),
                     new("SystemInfo_Field_Model", new SystemInformationText("Model One")),
                     new("SystemInfo_Field_LogicalProcessors", new SystemInformationNumber(16, SystemInformationUnit.Count))])], null),
                new(SystemInformationCategory.Windows, [new("Example Windows", [])], null),
                new(SystemInformationCategory.Processors, [new("Example processor",
                    [new("SystemInfo_Field_Cores", new SystemInformationNumber(8, SystemInformationUnit.Count)),
                     new("SystemInfo_Field_MaximumClock", new SystemInformationNumber(2500, SystemInformationUnit.Megahertz))])], null),
                new(SystemInformationCategory.Graphics, [new("Example graphics adapter",
                    [new("SystemInfo_Field_DriverVersion", new SystemInformationText("31.0.1"))])], null),
                new(SystemInformationCategory.Volumes, [new(Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\'),
                    [new("SystemInfo_Field_Capacity", new SystemInformationNumber(100, SystemInformationUnit.Bytes)),
                     new("SystemInfo_Field_FreeSpace", new SystemInformationNumber(25, SystemInformationUnit.Bytes)),
                     new("SystemInfo_Field_FileSystem", new SystemInformationText("NTFS")),
                     new("SystemInfo_Field_VolumeLabel", new SystemInformationText("Example volume"))])], null),
                new(SystemInformationCategory.Battery, [], null)
            ]);
        services.AddSingleton(inventory);
        var dashboardClock = new Mock<TimeProvider> { CallBase = true };
        dashboardClock.SetupGet(clock => clock.TimestampFrequency).Returns(TimeSpan.TicksPerSecond);
        dashboardClock.Setup(clock => clock.GetTimestamp()).Returns(0L);
        services.AddSingleton(dashboardClock);
        services.AddTransient(provider =>
        {
            var monitor = new Mock<ISystemMonitorService>(MockBehavior.Strict);
            monitor.Setup(service => service.ReadAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SystemMonitorSnapshot(DateTimeOffset.UtcNow, 27, 100, 50, null, null, [], [], []));
            monitor.Setup(service => service.StopAsync()).Returns(Task.CompletedTask);
            return new DashboardViewModel(inventory.Object, monitor.Object,
                Mock.Of<IInteractiveUserService>(user => user.InteractiveUserName == "Example user"),
                provider.GetRequiredService<ILocalizationService>(), provider.GetRequiredService<ILogService>(),
                provider.GetRequiredService<IApplicationModeService>(), dashboardClock.Object);
        });
        services.AddSingleton<WindowsStatusViewModel>(provider => new WindowsStatusViewModel(
            Mock.Of<IWindowsServicingService>(MockBehavior.Strict), Mock.Of<IDialogService>(), Mock.Of<IFilePickerService>(),
            provider.GetRequiredService<ILocalizationService>(), provider.GetRequiredService<IApplicationModeService>(),
            new ExtensionTestDispatcher(), Mock.Of<ITaskProgressService>()));
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
                var count = await NativeTestCases.RunAsync(typeof(PowerOptimizationsViewModelTests), typeof(TaskProgressViewModelTests), typeof(UpdateCheckViewModelTests), typeof(MainWindowViewModelTests), typeof(ApplicationInfoViewModelTests), typeof(SupportEntryPointTests), typeof(EnvironmentEditorViewModelTests), typeof(UninstallBackupsViewModelTests), typeof(ApplicationCloseServiceTests), typeof(CustomIconTests), typeof(SettingViewModelFactoryTests), typeof(TechnicalDetailsManagerTests), typeof(StartupOrchestratorTests), typeof(StartupPresentationTests), typeof(WindowSizeManagerTests), typeof(CleanerProgressReporterTests), typeof(CleanerViewModelTests), typeof(DriversViewModelTests), typeof(WimStep4IsoViewModelTests), typeof(WimWorkflowRegressionTests), typeof(WimStep1ViewModelTests), typeof(WimStep2XmlViewModelTests), typeof(WimStep3DriversViewModelTests), typeof(WimImageFormatViewModelTests), typeof(WimUtilViewModelTests), typeof(SectionNavigationTests), typeof(NavigationRouterTests), typeof(SavedWifiViewModelTests), typeof(SavedWifiPageTests), typeof(SettingsPageTests));
                count += await NativeTestCases.RunAsync(typeof(SidebarPreferencesViewModelTests), typeof(AppColorPreferencesViewModelTests),
                    typeof(SettingsViewModelTests), typeof(SettingApplicationViewModelTests), typeof(SettingItemViewModelTests),
                    typeof(HostsEditorViewModelTests), typeof(UtilityViewModelTests), typeof(ExtensionApplicationLifetimeTests),
                    typeof(SystemMonitorWidgetHostTests), typeof(ExperimentalFeaturesPageTests), typeof(SystemInformationViewModelTests));
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
