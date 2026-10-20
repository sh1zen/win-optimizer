using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Services;
using WinOptimizer.Core.Features.Common.Extensions;
using WinOptimizer.UI.Features.Common.Extensions.DI;
using WinOptimizer.UI.Features.Common.Interfaces;

namespace WinOptimizer.UI;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Microsoft.UI.Xaml.Application
{
    private Window? _mainWindow;
    private IHost? _host;
    private ILogService? _logService;

    /// <summary>
    /// Gets the main window instance.
    /// </summary>
    public static Window? MainWindow => (Current as App)?._mainWindow;

    /// <summary>
    /// Gets the service provider for dependency injection.
    /// </summary>
    public static IServiceProvider Services => (Current as App)?._host?.Services
        ?? throw new InvalidOperationException("Host not initialized");

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        StartupLogger.Log("App", "App constructor starting");
        try
        {
            // Register exception handlers before any UI initialization
            RegisterExceptionHandlers();
            StartupLogger.Log("App", "Exception handlers registered");

            // Only native control resources are needed to display the first loading frame.
            InitializeComponent();
        }
        catch (Exception ex)
        {
            StartupLogger.Log("App", $"App constructor EXCEPTION: {ex}");
            throw;
        }
    }

    /// <summary>Uses an explicitly composed host when embedding the application in a native test host.</summary>
    internal App(IHost host) : this()
    {
        _host = host;
    }

    /// <summary>
    /// Registers all exception handlers for comprehensive error logging.
    /// </summary>
    private void RegisterExceptionHandlers()
    {
        // AppDomain unhandled exceptions (fatal errors)
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

        // WinUI 3 unhandled exceptions (replaces WPF DispatcherUnhandledException)
        this.UnhandledException += OnAppUnhandledException;

        // Unobserved task exceptions
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    /// <summary>
    /// Handles fatal AppDomain unhandled exceptions.
    /// </summary>
    private void OnAppDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        StartupLogger.Log("App", $"[FATAL] AppDomain unhandled exception: {ex?.Message}\n{ex?.StackTrace}");
        _logService?.LogError($"Fatal unhandled exception: {ex?.Message}", ex);
    }

    /// <summary>
    /// Handles WinUI 3 unhandled exceptions.
    /// </summary>
    private void OnAppUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        StartupLogger.Log("App", $"[CRASH] Unhandled UI exception: {e.Exception?.Message}\n{e.Exception?.StackTrace}\nInner: {e.Exception?.InnerException?.Message}\n{e.Exception?.InnerException?.StackTrace}");
        _logService?.LogError($"Unhandled UI exception: {e.Exception?.Message}", e.Exception);
        e.Handled = true; // Prevent crash if possible
    }

    /// <summary>
    /// Handles unobserved task exceptions.
    /// </summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        StartupLogger.Log("App", $"[ERROR] Unobserved task exception: {e.Exception?.Message}\n{e.Exception?.StackTrace}");
        _logService?.LogError($"Unobserved task exception: {e.Exception?.Message}", e.Exception);
        e.SetObserved(); // Prevent crash
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        StartupLogger.Log("App", "OnLaunched starting");
        try
        {
            // Present the real main window before host construction, storage or preferences.
            var window = new MainWindow();
            _mainWindow = window;
            if (!Program.IsWidgetStartup)
            {
                window.Activate();
                try { await window.WaitForLoadingFrameAsync(); }
                catch (OperationCanceledException) when (window.IsClosed) { return; }
                if (window.IsClosed) return;
                StartupLogger.Log("App", "Initial loading frame presented");
            }

            // Feature styles and templates can load after the loading frame is visible.
            InitializeShellResources();

            // Build the host with DI container using CompositionRoot
            StartupLogger.Log("App", "Building DI host...");
            _host = CompositionRoot.CreateWinOptimizerHost().Build();
            StartupLogger.Log("App", "DI host built successfully");

            try
            {
                await Services.GetRequiredService<WinOptimizer.Infrastructure.Features.Common.Services.AppDataStorage>().InitializeAsync();
            }
            catch (Exception storageError)
            {
                StartupLogger.Log("App", $"Storage initialization failed: {storageError}");
                ShowStartupMessage(0, "Application data could not be prepared. Your data has been preserved.\n\n" +
                    storageError.Message + "\n\nResolve the reported file or permission problem and reopen win-optimizer.",
                    "win-optimizer storage", 0x10);
                _host.Dispose();
                Exit();
                return;
            }
            if (window.IsClosed) { _host.Dispose(); return; }

            // Initialize log service for exception handlers and file logging
            try
            {
                _logService = Services.GetService<ILogService>();
                StartupLogger.Log("App", "LogService obtained");

                // Start file logging to %ProgramData%\win-optimizer\Logs
                try
                {
                    // Supply the diagnostic provider before starting file logging.
                    if (_logService is WinOptimizer.Core.Features.Common.Services.LogService concreteLogService)
                    {
                        var systemInfoProvider = Services.GetService<ISystemInfoProvider>();
                        if (systemInfoProvider != null)
                        {
                            concreteLogService.SetSystemInfoProvider(systemInfoProvider);
                        }
                    }
                    // The writer opens now; hardware diagnostics must not delay shell initialization.
                    _ = CompleteDiagnosticLoggingAsync();
                }
                catch (Exception startLogEx)
                {
                    StartupLogger.Log("App", $"Diagnostic logging setup FAILED: {startLogEx}");
                }
            }
            catch (Exception ex)
            {
                StartupLogger.Log("App", $"LogService unavailable: {ex.Message}");
            }

            // Apply the saved language before constructing the full shell.
            StartupLogger.Log("App", "Initializing localization...");
            InitializeLocalization();
            StartupLogger.Log("App", "Localization initialized");

            // Complete the shell in the window that is already displaying startup progress.
            window.InitializeShell();
            StartupLogger.Log("App", "MainWindow shell initialized");

            // Initialize theme service after window is created
            StartupLogger.Log("App", "Initializing theme...");
            InitializeTheme();
            StartupLogger.Log("App", "Theme initialized");

            // Start required settings, app inventory and initial backup work.
            // The loading overlay provides visual feedback while these run
            StartupLogger.Log("App", "Starting startup operations...");
            if (Program.IsWidgetStartup && !window.IsManualActivationRequested)
                (_mainWindow as MainWindow)?.StartWidgetStartupAsync().FireAndForget(_logService!);
            else (_mainWindow as MainWindow)?.StartStartupOperations();
            StartupLogger.Log("App", "Startup operations kicked off - OnLaunched complete");
        }
        catch (Exception ex)
        {
            StartupLogger.Log("App", $"OnLaunched EXCEPTION: {ex}");
            throw;
        }
    }

    /// <summary>Loads feature styles only when the loading window has been presented.</summary>
    internal void InitializeShellResources()
    {
        // Keep the live accent dictionary at the application root for its DI registration.
        Resources.MergedDictionaries.Add(new Features.Common.Resources.AppColorResources());
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("ms-appx:///Features/Common/Resources/ShellResources.xaml")
        });
    }

    /// <summary>Reports fatal storage failures before a XAML root or localized shell is available.</summary>
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int ShowStartupMessage(nint owner, string text, string caption, uint type);

    /// <summary>Observes diagnostic initialization and reports failures through the pre-DI logger.</summary>
    private async Task CompleteDiagnosticLoggingAsync()
    {
        try
        {
            if (_logService is null) return;
            var startup = _logService.StartLogAsync();
            StartupLogger.Log("App", "Diagnostic log initialization started in background");
            await startup.ConfigureAwait(false);
            StartupLogger.Log("App", "Diagnostic log initialization completed");
            _logService.LogInformation("win-optimizer application starting...");
        }
        catch (Exception ex)
        {
            StartupLogger.Log("App", $"StartLogAsync() FAILED: {ex}");
        }
    }

    /// <summary>
    /// Initializes the localization system for use with x:Bind in XAML.
    /// </summary>
    private void InitializeLocalization()
    {
        try
        {
            var localizationService = Services.GetRequiredService<ILocalizationService>();

            // Load and apply the saved language preference (sync to avoid async deadlock on UI thread)
            var preferencesService = Services.GetRequiredService<IUserPreferencesService>();
            var savedLanguage = preferencesService.GetPreference("Language", "en");
            localizationService.SetLanguage(savedLanguage);
        }
        catch (Exception ex)
        {
            // Log error but don't crash - app will show key names
            _logService?.LogDebug($"Failed to initialize localization: {ex.Message}");
        }
    }

    /// <summary>
    /// Initializes the theme system and loads the saved user preference.
    /// </summary>
    private void InitializeTheme()
    {
        try
        {
            var themeService = Services.GetRequiredService<IThemeService>();
            themeService.LoadSavedTheme();
        }
        catch (Exception ex)
        {
            // Log error but don't crash - app will use default theme
            _logService?.LogDebug($"Failed to load theme: {ex.Message}");
        }
    }

}
