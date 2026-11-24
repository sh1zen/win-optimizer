using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Infrastructure.Features.Common.Services;
using WinOptimizer.Infrastructure.Features.Extensions;
using WinOptimizer.UI.Features.Common.Extensions.DI;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Extensions.Notch;
using WinOptimizer.UI.Features.Extensions.ViewModels;
using Windows.ApplicationModel.Activation;

namespace WinOptimizer.UI.Features.Extensions;

/// <summary>Runs SysMonitor and Notch on their own desktop dispatcher and process lifetime.</summary>
internal sealed class DesktopWidgetsApplication(string[] args) : CompanionApplication, IDisposable
{
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private bool _automatic = args is [WinOptimizer.Core.Features.Extensions.DesktopWidgetLaunch.StartupArgument];
    private bool _closing;
    private IHost? _host;
    private ExtensionsViewModel? _preferences;
    private SystemMonitorWidgetHost? _monitor;
    private NotchExtension? _notch;
    private ExtensionApplicationLifetime? _lifetime;
    private WidgetPreferencesWatcher? _watcher;
    private EventWaitHandle? _monitorReady;
    private Task _initialization = Task.CompletedTask;
    private Window? _lifetimeWindow;

    protected override void ValidateProcess()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("The desktop widget host requires an administrator token.");
    }

    protected override void OnLaunched(App app, string? language) => _initialization = InitializeAsync(app);

    private async Task InitializeAsync(App app)
    {
        try
        {
            if (args is [DesktopExtensionProcess.StopWidgetsArgument]) { app.Exit(); return; }
            _host = CompositionRoot.CreateWinOptimizerHost(desktopWidgetHost: true).Build();
            var services = _host.Services;
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            services.GetRequiredService<IDispatcherService>().Initialize(dispatcher);
            await services.GetRequiredService<AppDataStorage>().InitializeAsync();
            var store = services.GetRequiredService<IUserPreferencesService>();
            app.InitializeWidgetResources(_host, await store.ReadSnapshotAsync());
            // Keep the dispatcher alive while a closed widget drains its native readings.
            _lifetimeWindow = new Window();
            _lifetimeWindow.AppWindow.IsShownInSwitchers = false;
            var log = services.GetRequiredService<ILogService>();
            _preferences = services.GetRequiredService<ExtensionsViewModel>();
            _monitor = (SystemMonitorWidgetHost)services.GetRequiredService<ISystemMonitorWidgetHost>();
            _monitorReady = SystemMonitorAvailability.Create();
            _monitor.StateChanged += OnMonitorStateChanged;
            _notch = services.GetRequiredService<NotchExtension>();
            _monitor.SettingsRequested += OpenMonitor;
            _notch.OpenOptimizerRequested += OpenNotch;
            _lifetime = new(_monitor, _preferences, _notch, ReportWidgetError,
                () => { _closing = true; app.Exit(); }, log);
            _watcher = new(services.GetRequiredService<IInteractiveUserService>(), dispatcher, async () =>
            {
                await _initialization;
                await RefreshAsync();
            }, log);
            await _lifetime.StartInBackgroundAsync(_automatic);
            if (!_closing && args is [DesktopExtensionProcess.ShowMonitorArgument]) await _monitor.ShowAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception
            or COMException or ArgumentException)
        {
            ReportStartupError(DesktopExtensionProcess.Widgets.Name, ex);
            app.Exit();
        }
    }

    protected override async void Activate(AppActivationArguments arguments)
    {
        if (arguments.Data is not ILaunchActivatedEventArgs launch) return;
        await _initialization;
        if (_lifetime is null || _closing) return;
        try
        {
            var command = launch.Arguments.TrimEnd().Split(' ')[^1];
            if (command == DesktopExtensionProcess.StopWidgetsArgument)
            {
                _closing = true;
                await _lifetime.CloseAsync();
                return;
            }
            _automatic = false;
            await RefreshAsync();
            if (command == DesktopExtensionProcess.ShowMonitorArgument && _preferences?.Options.Enabled == true)
                await _monitor!.ShowAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        { ReportStartupError(DesktopExtensionProcess.Widgets.Name, ex); }
    }

    private async Task RefreshAsync()
    {
        await _refreshGate.WaitAsync();
        try
        {
            if (_closing || _preferences is null || _notch is null) return;
            var snapshot = await _host!.Services.GetRequiredService<IUserPreferencesService>().ReadSnapshotAsync();
            var localization = _host.Services.GetRequiredService<ILocalizationService>();
            var language = snapshot.Get("Language", "en");
            if (localization.CurrentLanguage != language) localization.SetLanguage(language);
            var theme = _host.Services.GetRequiredService<IThemeService>();
            var previousTheme = theme.GetEffectiveTheme();
            theme.LoadSavedTheme(snapshot);
            await _lifetime!.RefreshAsync(_automatic);
            if (!_closing && _monitor?.IsRunning == true && theme.GetEffectiveTheme() != previousTheme)
                await _monitor.ApplyOptionsAsync(_preferences.Options);
        }
        finally { _refreshGate.Release(); }
    }

    private void ReportWidgetError()
    {
        var message = _monitor?.LastError ?? (_notch?.HasError == true ? _notch.Error : _preferences?.Status);
        ReportStartupError(DesktopExtensionProcess.Widgets.Name,
            new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? "The desktop widget could not start." : message));
    }

    private void OpenMonitor(object? sender, EventArgs e) => OpenSettings(notch: false);
    private void OpenNotch(object? sender, EventArgs e) => OpenSettings(notch: true);
    private void OnMonitorStateChanged(object? sender, EventArgs e)
    {
        if (_monitor?.IsRunning == true) _monitorReady?.Set();
        else _monitorReady?.Reset();
    }

    private static void OpenSettings(bool notch)
    {
        try { DesktopExtensionProcess.OpenWidgetSettings(notch); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        { ReportStartupError(DesktopExtensionProcess.Widgets.Name, ex); }
    }

    public void Dispose()
    {
        _closing = true;
        _watcher?.Dispose();
        _lifetime?.Dispose();
        if (_monitor is not null)
        {
            _monitor.SettingsRequested -= OpenMonitor;
            _monitor.StateChanged -= OnMonitorStateChanged;
        }
        _monitorReady?.Reset();
        _monitorReady?.Dispose();
        if (_notch is not null) _notch.OpenOptimizerRequested -= OpenNotch;
        _host?.Dispose();
    }
}
