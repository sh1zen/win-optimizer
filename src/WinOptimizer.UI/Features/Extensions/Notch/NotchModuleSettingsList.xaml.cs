using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace WinOptimizer.UI.Features.Extensions.Notch;

/// <summary>Displays editable module selections and compact pins for a Notch settings section.</summary>
public sealed partial class NotchModuleSettingsList : UserControl
{
    public static readonly DependencyProperty OptionsProperty = DependencyProperty.Register(nameof(Options),
        typeof(IReadOnlyList<NotchModuleOption>), typeof(NotchModuleSettingsList),
        new PropertyMetadata(null, (sender, args) => ((NotchModuleSettingsList)sender).ModuleList.ItemsSource = args.NewValue));

    public IReadOnlyList<NotchModuleOption>? Options
    {
        get => (IReadOnlyList<NotchModuleOption>?)GetValue(OptionsProperty);
        set => SetValue(OptionsProperty, value);
    }

    public NotchModuleSettingsList() => InitializeComponent();

    private async void OnModuleToggled(object sender, RoutedEventArgs args)
    {
        var toggle = (ToggleSwitch)sender;
        if (toggle.Tag is not NotchModuleOption option || toggle.IsOn == option.IsEnabled) return;
        await option.SetEnabledAsync(toggle.IsOn);
        toggle.IsOn = option.IsEnabled;
    }

    private async void OnCompactPinToggled(object sender, RoutedEventArgs args)
    {
        var button = (ToggleButton)sender;
        var pinned = button.IsChecked == true;
        if (button.Tag is not NotchModuleOption option || pinned == option.IsPinned) return;
        await option.SetPinnedAsync(pinned);
        button.IsChecked = option.IsPinned;
    }
}
