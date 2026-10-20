using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Optimize.Interfaces;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Utilities.ViewModels;

namespace WinOptimizer.UI.Features.Optimize.ViewModels;

/// <summary>Owns a feature card's current restriction state and read availability.</summary>
public sealed partial class FeatureOptimizationChoice(WindowsFeatureOptimization feature, ILocalizationService localization) : ObservableObject
{
    public WindowsFeatureOptimization Feature { get; } = feature;
    public string Title => localization.GetString("OptimizeFeatures_" + Feature);
    public string Description => localization.GetString("OptimizeFeatures_" + Feature + "Description");
    [ObservableProperty] public partial bool IsOn { get; set; }
    [ObservableProperty] public partial bool IsAvailable { get; set; }

    /// <summary>Refreshes localized card text while retaining the loaded Windows state.</summary>
    public void RefreshText()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Description));
    }
}

/// <summary>Offers explicit feature operations through existing setting controls, without adding them to presets.</summary>
public partial class FeatureOptimizationViewModel(IOptimizationToolsService service, IDialogService dialogs,
    IWindowsVersionService windows, ILocalizationService localization, IApplicationModeService mode,
    IDispatcherService dispatcher, ITaskProgressService tasks)
    : UtilityViewModel(localization, mode, dispatcher, tasks, "Nav_Optimize")
{
    protected override string TitleKey => "OptimizeFeatures_Title";
    public IReadOnlyList<FeatureOptimizationChoice> Choices { get; } = Enum.GetValues<WindowsFeatureOptimization>()
        .Where(feature => feature != WindowsFeatureOptimization.Copilot || windows.IsWindows11())
        .Where(feature => feature != WindowsFeatureOptimization.ActivityHistory || !windows.IsWindows11())
        .Select(feature => new FeatureOptimizationChoice(feature, localization)).ToArray();

    [RelayCommand(CanExecute = nameof(IsIdle))]
    public Task RefreshAsync() => ExecuteAsync(async () =>
    {
        foreach (var choice in Choices) choice.IsAvailable = false;
        foreach (var choice in Choices)
        {
            var state = await Task.Run(() => service.ReadFeatureAsync(choice.Feature));
            choice.IsOn = state == true;
            choice.IsAvailable = state.HasValue;
        }
    });

    /// <summary>Shows the exact effects first, then uses the shared task/mode/error lifecycle for the write.</summary>
    public async Task SetAsync(FeatureOptimizationChoice choice, bool isOn)
    {
        if (!CanWrite || !choice.IsAvailable || choice.IsOn == isOn) return;
        var response = await dialogs.ShowConfirmationAsync(new ConfirmationRequest
        {
            Title = choice.Title, Message = isOn ? choice.Description : Text("OptimizeFeatures_RestoreDescription"),
            ConfirmButtonText = Text("Button_Continue"), CancelButtonText = Text("Button_Cancel")
        });
        if (!response.Confirmed) return;
        await ExecuteAsync(async () =>
        {
            try
            {
                await service.SetFeatureAsync(choice.Feature, isOn);
            }
            finally
            {
                // A failed action may have applied part of a group; always read the resulting state.
                choice.IsAvailable = false;
                var state = await Task.Run(() => service.ReadFeatureAsync(choice.Feature));
                choice.IsOn = state == true;
                choice.IsAvailable = state.HasValue;
            }
            if (!choice.IsAvailable || choice.IsOn != isOn)
                throw new InvalidOperationException(Text("OptimizeFeatures_Unconfirmed"));
            Status = Text("OptimizeFeatures_Applied");
        }, write: true);
    }

    protected override void NotifyActions() => RefreshCommand.NotifyCanExecuteChanged();

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (string.IsNullOrEmpty(e.PropertyName))
            foreach (var choice in Choices) choice.RefreshText();
    }
}
