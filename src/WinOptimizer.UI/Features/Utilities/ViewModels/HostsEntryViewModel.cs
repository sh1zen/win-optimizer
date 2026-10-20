using CommunityToolkit.Mvvm.ComponentModel;
using WinOptimizer.Core.Features.Utilities;

namespace WinOptimizer.UI.Features.Utilities.ViewModels;

/// <summary>Projects a source mapping into one structured row; the editor owns its serialized draft.</summary>
public partial class HostsEntryViewModel : ObservableObject
{
    private readonly Func<string, string> _text;
    public HostsMapping Mapping { get; private set; }
    public string Address => Mapping.Address;
    public string HostNames => Mapping.HostNames;
    public string Description => Mapping.HostNames + (Mapping.Comment.Length == 0 ? "" : " — " + Mapping.Comment);
    public string EditLabel => _text("Utilities_HostsEditEntry");
    public string RemoveLabel => _text("Utilities_HostsRemoveEntry");
    [ObservableProperty] public partial bool IsEnabled { get; set; }
    [ObservableProperty] public partial bool CanEdit { get; set; }

    public HostsEntryViewModel(HostsMapping mapping, bool canEdit, Func<string, string> text)
    { _text = text; Mapping = mapping; IsEnabled = mapping.IsEnabled; CanEdit = canEdit; }

    /// <summary>Refreshes an existing row in place so toggles do not reset list scrolling or focus.</summary>
    public void Update(HostsMapping mapping, bool canEdit)
    {
        Mapping = mapping; IsEnabled = mapping.IsEnabled; CanEdit = canEdit;
        OnPropertyChanged(string.Empty);
    }
}
