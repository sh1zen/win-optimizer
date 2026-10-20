using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Optimize.Interfaces;
using WinOptimizer.Core.Features.Optimize.Models;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Utilities.ViewModels;

namespace WinOptimizer.UI.Features.Optimize.ViewModels;

/// <summary>Owns individually confirmed network edits and the existing verified quick enhancement action.</summary>
public partial class NetworkOptimizationViewModel : UtilityViewModel
{
    private readonly IOptimizationToolsService _service;
    private readonly IDialogService _dialogs;
    protected override string TitleKey => "OptimizeNetwork_Title";
    public IReadOnlyList<NetworkTuningChoice> Choices { get; }
    public IReadOnlyList<NetworkTuningGroup> Groups { get; }

    /// <summary>Builds stable presentation choices without querying Windows until page entry.</summary>
    public NetworkOptimizationViewModel(IOptimizationToolsService service, IDialogService dialogs,
        ILocalizationService localization, IApplicationModeService mode, IDispatcherService dispatcher, ITaskProgressService tasks)
        : base(localization, mode, dispatcher, tasks, "Nav_Optimize")
    {
        _service = service;
        _dialogs = dialogs;
        Choices = NetworkTuningCatalog.All.Select(item => new NetworkTuningChoice(item, localization)).ToArray();
        Groups = Choices.GroupBy(choice => choice.Definition.Owner)
            .Select(group => new NetworkTuningGroup(group.Key, group.ToArray(), localization)).ToArray();
    }
    public string ControlsLabel => string.Format(Text("OptimizeNetwork_Controls"), Choices.Count);
    public string EnhanceDescription => Text("OptimizeNetwork_EnhanceDescription");
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentState), nameof(CanEnhance))]
    public partial NetworkPerformanceState? State { get; private set; }
    public string EnhanceLabel => Text("OptimizeNetwork_Enhance");
    public string CurrentState => State is { } state
        ? string.Format(Text("OptimizeNetwork_Current"), state.AutoTuningLevel, Text(state.ReceiveSideScaling ? "Common_On" : "Common_Off"))
        : Text("OptimizeNetwork_Unknown");
    public bool CanEnhance => CanWrite && State != null;

    [RelayCommand(CanExecute = nameof(IsIdle))]
    public Task RefreshAsync() => ExecuteAsync(async () =>
    {
        State = null;
        foreach (var choice in Choices) choice.Update(choice.State with { IsAvailable = false });
        ApplyStates(await _service.ReadNetworkTuningsAsync());
    });

    /// <summary>Confirms the precise before/after value and changes only the selected control.</summary>
    public async Task SetAsync(NetworkTuningChoice choice, string? value)
    {
        if (!CanWrite || !choice.IsAvailable || choice.State.Value == value) return;
        var expected = choice.State.Value;
        var response = await _dialogs.ShowConfirmationAsync(new ConfirmationRequest
        {
            Title = choice.Title,
            Message = choice.Description + "\n\n" + string.Format(Text("OptimizeNetwork_ConfirmChange"), choice.FormatValue(expected), choice.FormatValue(value)),
            ConfirmButtonText = Text("Button_Continue"), CancelButtonText = Text("Button_Cancel")
        });
        if (!response.Confirmed) return;
        await ExecuteAsync(async () =>
        {
            var completed = false;
            try
            {
                ApplyStates(await _service.SetNetworkTuningAsync(choice.Definition.Id, value, expected));
                completed = true;
                if (!IsError) Status = Text("OptimizeNetwork_SettingApplied");
            }
            finally
            {
                // Failed writes may be partial; require a fresh read before another edit.
                if (!completed) choice.Update(choice.State with { IsAvailable = false });
            }
        }, write: true);
    }

    private void ApplyStates(IReadOnlyList<NetworkTuningState> states)
    {
        foreach (var choice in Choices)
            choice.Update(states.Single(state => state.Id == choice.Definition.Id));
        var tcp = Choices.Single(choice => choice.Definition.Id == "tcp-auto-tuning");
        var rss = Choices.Single(choice => choice.Definition.Id == "offload-rss");
        State = tcp.IsAvailable && rss.IsAvailable && tcp.State.Value != null
            ? new(tcp.State.Value, rss.IsOn) : null;
        var unavailable = Choices.Count(choice => !choice.IsAvailable);
        if (unavailable > 0)
        {
            IsError = true;
            Status = string.Format(Text("OptimizeNetwork_UnavailableCount"), unavailable);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEnhance))]
    private async Task EnhanceAsync()
    {
        if (!CanEnhance) return;
        var response = await _dialogs.ShowConfirmationAsync(new ConfirmationRequest
        {
            Title = EnhanceLabel, Message = EnhanceDescription,
            ConfirmButtonText = Text("Button_Continue"), CancelButtonText = Text("Button_Cancel")
        });
        if (!response.Confirmed) return;
        await ExecuteAsync(async () =>
        {
            State = await _service.EnhanceNetworkAsync();
            ApplyStates(await _service.ReadNetworkTuningsAsync());
            if (!IsError) Status = Text("OptimizeNetwork_Applied");
        }, write: true);
    }

    protected override void NotifyActions()
    {
        OnPropertyChanged(nameof(CanEnhance));
        RefreshCommand.NotifyCanExecuteChanged(); EnhanceCommand.NotifyCanExecuteChanged();
    }
    partial void OnStateChanged(NetworkPerformanceState? value) => EnhanceCommand.NotifyCanExecuteChanged();

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (string.IsNullOrEmpty(e.PropertyName))
        {
            foreach (var choice in Choices) choice.RefreshText();
            foreach (var group in Groups) group.RefreshText();
        }
    }
}
