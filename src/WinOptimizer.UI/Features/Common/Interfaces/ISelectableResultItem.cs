using System.ComponentModel;
using WinOptimizer.UI.Features.Common.Models;

namespace WinOptimizer.UI.Features.Common.Interfaces;

/// <summary>Selection and detail contract for items displayed by the shared grouped results list.</summary>
public interface ISelectableResultItem : INotifyPropertyChanged
{
    bool ShowSelection { get; }
    bool CanClean { get; }
    bool IsSelected { get; set; }
    string Detail { get; }
    int DetailMaxLines { get; }
    ResultReport? Report { get; }
    string Size { get; }
}
