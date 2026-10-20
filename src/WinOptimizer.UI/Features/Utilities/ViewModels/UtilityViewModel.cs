using CommunityToolkit.Mvvm.ComponentModel;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.UI.Features.Common.Interfaces;

namespace WinOptimizer.UI.Features.Utilities.ViewModels;

/// <summary>Owns common utility operation presentation and mode/localization subscriptions.</summary>
public abstract partial class UtilityViewModel : ObservableObject, IDisposable
{
    private readonly ILocalizationService _localization;
    private readonly IApplicationModeService _mode;
    private readonly IDispatcherService _dispatcher;
    private readonly ITaskProgressService _tasks;
    private readonly string _sectionTitleKey;
    private Task _pendingWrite = Task.CompletedTask;
    protected abstract string TitleKey { get; }
    public string Title => Text(TitleKey);
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsIdle), nameof(CanWrite))]
    public partial bool IsBusy { get; private set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasStatus))] public partial string Status { get; protected set; } = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StatusSeverity))] public partial bool IsError { get; protected set; }
    public Microsoft.UI.Xaml.Controls.InfoBarSeverity StatusSeverity => IsError ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success;
    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);
    public bool IsIdle => !IsBusy;
    public bool CanWrite => IsIdle && _mode.CurrentMode == WinOptimizerMode.Normal;
    public bool ShowModeNotice => _mode.CurrentMode != WinOptimizerMode.Normal;
    public string ModeNotice => Text("Utilities_ModeNotice");
    public string SectionTitle => Text(_sectionTitleKey);
    public string RefreshLabel => Text("Utilities_Refresh");
    public string SaveLabel => Text("Utilities_Save");
    public string Description => Text(TitleKey + "Description");

    protected UtilityViewModel(ILocalizationService localization, IApplicationModeService mode,
        IDispatcherService dispatcher, ITaskProgressService tasks, string sectionTitleKey = "Nav_Management")
    {
        _localization = localization; _mode = mode; _dispatcher = dispatcher; _tasks = tasks;
        _sectionTitleKey = sectionTitleKey;
        _localization.LanguageChanged += OnLanguageChanged;
        _mode.ModeChanged += OnModeChanged;
    }

    protected string Text(string key) => _localization.GetString(key);
    protected abstract void NotifyActions();
    partial void OnIsBusyChanged(bool value) => NotifyActions();

    /// <summary>Lets the existing shutdown hook drain an in-flight write without saving an uncommitted draft.</summary>
    public Task WaitForWriteAsync() => _pendingWrite;

    /// <summary>Shows reads locally and writes in the shared footer with explicitly supported cancellation.</summary>
    protected async Task ExecuteAsync(Func<Task> operation, bool write = false, bool canCancel = false)
    {
        if (IsBusy || (write && !CanWrite)) return;
        if (write && _tasks.IsTaskRunning) { Status = Text("Utilities_Wait"); IsError = true; return; }
        IsBusy = true; Status = ""; IsError = false;
        CancellationTokenSource? task = null;
        var completion = write ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        if (completion != null) _pendingWrite = completion.Task;
        try
        {
            if (write) task = _tasks.StartTask(Title, true, true, canCancel);
            await operation();
        }
        catch (Exception ex) when (ex is UtilityOperationException or IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or System.Security.SecurityException)
        { Status = ex.Message; IsError = true; }
        finally
        {
            if (task != null && ReferenceEquals(_tasks.CurrentTaskCancellationSource, task)) _tasks.CompleteTask();
            IsBusy = false;
            completion?.TrySetResult();
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => _dispatcher.RunOnUIThread(() => OnPropertyChanged(string.Empty));
    private void OnModeChanged(object? sender, EventArgs e) => _dispatcher.RunOnUIThread(() =>
    {
        OnPropertyChanged(nameof(CanWrite)); OnPropertyChanged(nameof(ShowModeNotice)); NotifyActions();
    });

    public void Dispose()
    {
        _localization.LanguageChanged -= OnLanguageChanged;
        _mode.ModeChanged -= OnModeChanged;
        GC.SuppressFinalize(this);
    }
}
