using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinOptimizer.Infrastructure.Features.Extensions;

namespace WinOptimizer.UI.Features.Extensions.Notch;

/// <summary>Edits the same confirmed switch and module selection that the desktop window uses.</summary>
public sealed partial class NotchSettingsPage : Page
{
    public NotchExtension ViewModel { get; private set; } = null!;
    private CancellationTokenSource? _weatherSearch;
    private DesktopWeatherLocation? _weatherSelection;
    private bool _syncingPlacement;
    private IReadOnlyList<(string? Device, string Label)> _displayOptions = [];

    public NotchSettingsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            ViewModel.PropertyChanged += OnViewModelChanged;
            await ViewModel.LoadAsync();
            WeatherCityBox.Text = ViewModel.WeatherLocation?.Label ?? "";
            RefreshContentLabels();
        };
        Unloaded += (_, _) =>
        {
            ViewModel.PropertyChanged -= OnViewModelChanged;
            CancelWeatherSearch();
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs args)
    {
        base.OnNavigatedTo(args);
        ViewModel = args.Parameter as NotchExtension ?? App.Services.GetRequiredService<NotchExtension>();
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (string.IsNullOrEmpty(args.PropertyName)) RefreshContentLabels();
        else if (args.PropertyName == nameof(NotchExtension.Placement)) RefreshPlacementChoices();
    }

    private void RefreshContentLabels()
    {
        WeatherSetting.Header = ViewModel.Text("Notch_WeatherCity");
        WeatherSetting.Description = ViewModel.Text("Notch_WeatherCityDescription");
        WeatherCityBox.PlaceholderText = ViewModel.Text("Notch_WeatherCity");
        AutomationProperties.SetName(WeatherCityBox, WeatherCityBox.PlaceholderText);
        SaveWeatherCity.Content = ViewModel.Text("Notch_Save");
        ShapeSetting.Header = ViewModel.Text("Notch_Shape");
        DisplaySetting.Header = ViewModel.Text("Notch_Display");
        AutomationProperties.SetName(ShapeChoice, (string)ShapeSetting.Header);
        AutomationProperties.SetName(DisplayChoice, (string)DisplaySetting.Header);
        RefreshPlacementChoices(refreshLabels: true);
    }

    /// <summary>Shows the confirmed placement and refreshes displays after a topology change.</summary>
    private void RefreshPlacementChoices(bool refreshLabels = false)
    {
        _syncingPlacement = true;
        try
        {
            var placement = ViewModel.Placement;
            if (refreshLabels) ShapeChoice.ItemsSource = new[] { ViewModel.Text("Notch_Attached"), ViewModel.Text("Notch_Floating") };
            ShapeChoice.SelectedIndex = placement.Floating ? 1 : 0;
            var options = new List<(string? Device, string Label)> { (null, ViewModel.Text("Notch_PrimaryDisplay")) };
            int number = 0;
            foreach (var (area, device) in NotchFrame.DisplayDevices()
                .OrderBy(item => item.Area.OuterBounds.X).ThenBy(item => item.Area.OuterBounds.Y))
            {
                var bounds = area.OuterBounds;
                options.Add((device, ViewModel.DisplayLabel(++number, bounds.Width, bounds.Height)));
            }
            int selected = options.FindIndex(item => string.Equals(item.Device, placement.DisplayDevice, StringComparison.OrdinalIgnoreCase));
            if (selected < 0)
            {
                selected = options.Count;
                options.Add((placement.DisplayDevice, ViewModel.Text("Notch_DisplayUnavailable")));
            }
            if (!_displayOptions.SequenceEqual(options))
            {
                _displayOptions = options;
                DisplayChoice.Items.Clear();
                foreach (var (device, label) in options)
                    DisplayChoice.Items.Add(new ComboBoxItem { Content = label, Tag = device });
            }
            DisplayChoice.SelectedIndex = selected;
        }
        finally { _syncingPlacement = false; }
    }

    private void OnDisplayDropDownOpened(object sender, object args) => RefreshPlacementChoices();

    private async void OnPlacementChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_syncingPlacement || !ViewModel.CanEdit || ShapeChoice.SelectedIndex < 0 ||
            DisplayChoice.SelectedItem is not ComboBoxItem target) return;
        var requested = new NotchPlacement(ShapeChoice.SelectedIndex == 1, target.Tag as string);
        await ViewModel.SetPlacementAsync(requested);
        if (ViewModel.Placement != requested) RefreshPlacementChoices();
    }

    private async void OnWeatherSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        _weatherSelection = null;
        CancelWeatherSearch();
        sender.ItemsSource = null;
        SetWeatherStatus(null);
        var query = sender.Text.Trim();
        if (query.Length < 2) return;
        if (query.Length > DesktopPlaceSearch.MaximumQueryLength || query.Any(char.IsControl))
        {
            SetWeatherStatus("Notch_WeatherSelectLocation");
            return;
        }

        using var current = new CancellationTokenSource();
        _weatherSearch = current;
        var token = current.Token;
        try
        {
            await Task.Delay(280, token);
            SetWeatherStatus("Notch_WeatherSearching");
            var matches = await ViewModel.SearchWeatherPlacesAsync(query, token);
            if (token.IsCancellationRequested || sender.Text.Trim() != query) return;
            sender.ItemsSource = matches;
            sender.IsSuggestionListOpen = matches.Count > 0;
            SetWeatherStatus(matches.Count == 0 ? "Notch_WeatherNoResults" : null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or JsonException or InvalidOperationException or IOException)
        {
            if (!token.IsCancellationRequested) SetWeatherStatus("Notch_WeatherSearchUnavailable");
        }
        finally
        {
            if (ReferenceEquals(_weatherSearch, current)) _weatherSearch = null;
        }
    }

    private void OnWeatherSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        CancelWeatherSearch();
        _weatherSelection = args.SelectedItem as DesktopWeatherLocation;
        SetWeatherStatus(null);
    }

    private async void OnWeatherQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is DesktopWeatherLocation chosen)
        {
            _weatherSelection = chosen;
            sender.Text = chosen.Label;
        }
        await SaveWeatherLocationAsync(_weatherSelection);
    }

    private async void OnSaveWeatherCity(object sender, RoutedEventArgs args) => await SaveWeatherLocationAsync(_weatherSelection);

    private async Task SaveWeatherLocationAsync(DesktopWeatherLocation? selection)
    {
        var query = WeatherCityBox.Text.Trim();
        if (query.Length != 0 && selection?.Label != query)
        {
            if (query != ViewModel.WeatherLocation?.Label) SetWeatherStatus("Notch_WeatherSelectLocation");
            return;
        }
        CancelWeatherSearch();
        await ViewModel.SetWeatherLocationAsync(query.Length == 0 ? null : selection);
        if (ViewModel.HasError) return;
        WeatherCityBox.Text = ViewModel.WeatherLocation?.Label ?? "";
        WeatherCityBox.ItemsSource = null;
        WeatherCityBox.IsSuggestionListOpen = false;
        _weatherSelection = null;
        SetWeatherStatus(null);
    }

    private void CancelWeatherSearch()
    {
        var search = _weatherSearch;
        _weatherSearch = null;
        search?.Cancel();
    }

    private void SetWeatherStatus(string? key)
    {
        WeatherSearchStatus.Text = key is null ? "" : ViewModel.Text(key);
        WeatherSearchStatus.Visibility = key is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnEnabledToggled(object sender, RoutedEventArgs args)
    {
        var toggle = (ToggleSwitch)sender;
        if (toggle.IsOn == ViewModel.IsEnabled) return;
        await ViewModel.SetEnabledAsync(toggle.IsOn);
        toggle.IsOn = ViewModel.IsEnabled;
    }

    private async void OnStartupToggled(object sender, RoutedEventArgs args)
    {
        var toggle = (ToggleSwitch)sender;
        if (toggle.IsOn == ViewModel.StartWithWindows) return;
        await ViewModel.SetStartupAsync(toggle.IsOn);
        toggle.IsOn = ViewModel.StartWithWindows;
    }

    private async void OnOverviewToggled(object sender, RoutedEventArgs args)
    {
        var toggle = (ToggleSwitch)sender;
        if (toggle.IsOn == ViewModel.IsOverviewEnabled) return;
        await ViewModel.SetOverviewEnabledAsync(toggle.IsOn);
        toggle.IsOn = ViewModel.IsOverviewEnabled;
    }

    private async void OnSysInfoToggled(object sender, RoutedEventArgs args)
    {
        var toggle = (ToggleSwitch)sender;
        if (toggle.IsOn == ViewModel.IsSysInfoEnabled) return;
        await ViewModel.SetSysInfoEnabledAsync(toggle.IsOn);
        toggle.IsOn = ViewModel.IsSysInfoEnabled;
    }

    private void OnOpenSysInfo(object sender, RoutedEventArgs args) => Frame.Navigate(typeof(NotchSysInfoPage), ViewModel);
}
