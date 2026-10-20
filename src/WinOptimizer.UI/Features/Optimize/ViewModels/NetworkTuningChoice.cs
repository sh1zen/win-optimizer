using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Optimize.Models;

namespace WinOptimizer.UI.Features.Optimize.ViewModels;

/// <summary>Localizes an explicit native choice without changing its Windows value.</summary>
public sealed class NetworkTuningOption(string value, ILocalizationService localization) : ObservableObject
{
    public string Value { get; } = value;
    public string Label => localization.GetString("OptimizeNetwork_Option_" + Value);
    public void RefreshText() => OnPropertyChanged(nameof(Label));
}

/// <summary>Presents the last native readback; edits never overwrite this baseline before confirmation.</summary>
public sealed partial class NetworkTuningChoice(NetworkTuningDefinition definition, ILocalizationService localization) : ObservableObject
{
    public NetworkTuningDefinition Definition { get; } = definition;
    public string Title => localization.GetString("OptimizeNetwork_" + Definition.Id + "_Title");
    public string Description => localization.GetString("OptimizeNetwork_" + Definition.Id + "_Description");
    public string ResetLabel => localization.GetString("OptimizeNetwork_ClearOverride");
    public string Placeholder => localization.GetString("OptimizeNetwork_NotConfigured");
    public IReadOnlyList<NetworkTuningOption> Options { get; } = definition.Input == NetworkTuningInput.Selection
        ? definition.Options.Select(value => new NetworkTuningOption(value, localization)).ToArray() : [];
    public NetworkTuningState State { get; private set; } = new(definition.Id, null, false);
    public bool IsUpdating { get; private set; }
    public bool IsAvailable => State.IsAvailable;
    public bool IsOn => State.Value == "Enabled";
    public double NumberValue => double.TryParse(State.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : double.NaN;
    public double Minimum => Definition.Minimum;
    public double Maximum => Definition.Maximum;
    public double Increment => Definition.Step;
    public NetworkTuningOption? SelectedOption => Options.FirstOrDefault(option => option.Value == State.Value);
    public Visibility ToggleVisibility => Visible(Definition.Input == NetworkTuningInput.Toggle);
    public Visibility SelectionVisibility => Visible(Definition.Input == NetworkTuningInput.Selection);
    public Visibility NumberVisibility => Visible(Definition.Input == NetworkTuningInput.Number);
    public Visibility ResetVisibility => Visible(Definition.Owner == NetworkTuningOwner.NameResolution && State.Value != null);
    public string CurrentLabel => !State.IsAvailable
        ? string.Format(localization.GetString("OptimizeNetwork_Unavailable"), State.ErrorMessage ?? localization.GetString("OptimizeNetwork_Unknown"))
        : string.Format(localization.GetString("OptimizeNetwork_Value"), FormatValue(State.Value));

    public string FormatValue(string? value) => value == null ? Placeholder
        : Definition.Input == NetworkTuningInput.Toggle ? localization.GetString(value == "Enabled" ? "Common_On" : "Common_Off")
        : Options.FirstOrDefault(option => option.Value == value)?.Label ?? value;

    /// <summary>Publishes one complete native snapshot to all control bindings.</summary>
    public void Update(NetworkTuningState state)
    {
        State = state;
        IsUpdating = true;
        try { OnPropertyChanged(string.Empty); }
        finally { IsUpdating = false; }
    }

    public void RefreshText()
    {
        foreach (var option in Options) option.RefreshText();
        Update(State);
    }
    private static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>Groups the same choice instances under localized native-owner headings.</summary>
public sealed class NetworkTuningGroup(NetworkTuningOwner owner, IReadOnlyList<NetworkTuningChoice> choices, ILocalizationService localization) : ObservableObject
{
    public string Title => localization.GetString("OptimizeNetwork_Group_" + owner);
    public IReadOnlyList<NetworkTuningChoice> Choices { get; } = choices;
    public void RefreshText() => OnPropertyChanged(nameof(Title));
}
