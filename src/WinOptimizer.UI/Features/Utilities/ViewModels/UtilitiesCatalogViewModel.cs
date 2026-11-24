using CommunityToolkit.Mvvm.ComponentModel;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Interfaces;

namespace WinOptimizer.UI.Features.Utilities.ViewModels;

/// <summary>Provides localized headings for the desktop extension overview.</summary>
public sealed class UtilitiesCatalogViewModel : ObservableObject, IDisposable
{
    private readonly ILocalizationService _localization;
    private readonly IDispatcherService _dispatcher;
    public string Heading => _localization.GetString("Nav_Functionalities");
    public string Description => _localization.GetString("Utilities_Description");
    public string PreviewTitle => _localization.GetString("QuickPreview_Title");
    public string PreviewDescription => _localization.GetString("QuickPreview_Description");
    public string PreviewShortcutLabel => _localization.GetString("QuickPreview_Shortcut");
    public string RefreshLabel => _localization.GetString("Button_Refresh");
    public UtilitiesCatalogViewModel(ILocalizationService localization, IDispatcherService dispatcher)
    { _localization = localization; _dispatcher = dispatcher; _localization.LanguageChanged += OnLanguageChanged; }
    private void OnLanguageChanged(object? sender, EventArgs e) => _dispatcher.RunOnUIThread(() => OnPropertyChanged(string.Empty));
    public void Dispose() => _localization.LanguageChanged -= OnLanguageChanged;
}
