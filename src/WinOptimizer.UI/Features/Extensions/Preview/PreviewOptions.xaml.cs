using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Infrastructure.Features.Extensions;

namespace WinOptimizer.UI.Features.Extensions.Preview;

/// <summary>Presents Preview's confirmed shortcut, sign-in and background settings.</summary>
public sealed partial class PreviewOptions : UserControl
{
    private bool _confirmedShortcut;
    private bool _confirmedStartup;
    private int _confirmedOpacity = PreviewExtensionSettings.DefaultOpacity;
    private uint? _opacityPointer;
    private bool _opacityCommitQueued;

    public event EventHandler? ShortcutChanged;
    public event EventHandler? StartupChanged;
    public event EventHandler? BackgroundOpacityChanged;
    public bool ShortcutEnabled => Shortcut.IsOn;
    public bool StartsWithWindows => Startup.IsOn;
    public int BackgroundOpacity => (int)OpacitySlider.Value;

    public PreviewOptions()
    {
        InitializeComponent();
        OpacitySlider.Minimum = PreviewExtensionSettings.MinimumOpacity;
        OpacitySlider.Maximum = PreviewExtensionSettings.MaximumOpacity;
        OpacitySlider.AddHandler(PointerPressedEvent, new PointerEventHandler(OnOpacityPointerPressed), true);
        OpacitySlider.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnOpacityPointerFinished), true);
        OpacitySlider.AddHandler(PointerCanceledEvent, new PointerEventHandler(OnOpacityPointerFinished), true);
        OpacitySlider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnOpacityPointerFinished), true);
        ShowOpacity(PreviewExtensionSettings.DefaultOpacity);
    }

    public void Localize(ILocalizationService localization)
    {
        var shortcut = localization.GetString("QuickPreview_Shortcut");
        var startup = localization.GetString("QuickPreview_Startup");
        var opacity = localization.GetString("QuickPreview_BackgroundOpacity");
        ShortcutCard.Header = shortcut;
        StartupCard.Header = startup;
        OpacityCard.Header = opacity;
        AutomationProperties.SetName(Shortcut, shortcut);
        AutomationProperties.SetName(Startup, startup);
        AutomationProperties.SetName(OpacitySlider, opacity);
    }

    public void ShowShortcut(bool enabled)
    {
        _confirmedShortcut = enabled;
        Shortcut.IsOn = enabled;
    }

    public void ShowStartup(bool enabled)
    {
        _confirmedStartup = enabled;
        Startup.IsOn = enabled;
    }

    public void ShowOpacity(int opacity)
    {
        _confirmedOpacity = opacity;
        OpacitySlider.Value = opacity;
    }

    private void OnShortcutToggled(object sender, RoutedEventArgs args)
    {
        if (Shortcut.IsOn != _confirmedShortcut) ShortcutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnStartupToggled(object sender, RoutedEventArgs args)
    {
        if (Startup.IsOn != _confirmedStartup) StartupChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnOpacityChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        OpacityValue.Text = string.Create(CultureInfo.CurrentUICulture, $"{BackgroundOpacity}/{PreviewExtensionSettings.MaximumOpacity}");
        QueueOpacityCommit();
    }

    private void OnOpacityPointerPressed(object sender, PointerRoutedEventArgs args) =>
        _opacityPointer ??= args.Pointer.PointerId;

    private void OnOpacityPointerFinished(object sender, PointerRoutedEventArgs args)
    {
        if (_opacityPointer != args.Pointer.PointerId) return;
        _opacityPointer = null;
        QueueOpacityCommit();
    }

    /// <summary>Lets the slider handle the current input before saving a completed adjustment.</summary>
    private void QueueOpacityCommit()
    {
        if (_opacityCommitQueued || _opacityPointer.HasValue || BackgroundOpacity == _confirmedOpacity) return;
        _opacityCommitQueued = DispatcherQueue.TryEnqueue(() =>
        {
            _opacityCommitQueued = false;
            if (!_opacityPointer.HasValue && BackgroundOpacity != _confirmedOpacity)
                BackgroundOpacityChanged?.Invoke(this, EventArgs.Empty);
        });
    }
}
