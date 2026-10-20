using System.Collections.ObjectModel;
using System.ComponentModel;
using WinOptimizer.UI.Features.Common.Models;

namespace WinOptimizer.UI.Features.Common.Interfaces;

/// <summary>Presentation contract consumed by the shared section-page constructor.</summary>
public interface ISectionPageViewModel : INotifyPropertyChanged
{
    string PageTitle { get; }
    string PageDescription { get; }
    string SearchPlaceholder { get; }
    string SearchText { get; set; }
    string CurrentSectionKey { get; set; }
    bool IsLoading { get; }
    bool IsNotLoading { get; }
    bool IsInDetailPage { get; }
    ObservableCollection<SearchSuggestionItem> SearchSuggestions { get; }
    ISettingsFeatureViewModel? GetSectionViewModel(string sectionKey);
    Task InitializeAsync();
    void OnNavigatedFrom();
}
