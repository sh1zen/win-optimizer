using CommunityToolkit.Mvvm.ComponentModel;

namespace WinOptimizer.UI.Features.Utilities.ViewModels;

/// <summary>An editable, unexpanded segment of the environment editor's PATH draft.</summary>
public partial class EnvironmentPathEntryViewModel(string value) : ObservableObject
{
    [ObservableProperty] public partial string Value { get; set; } = value;
}
