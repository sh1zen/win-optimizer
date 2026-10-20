using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.UI.Features.Common.Helpers;
using WinOptimizer.UI.Features.Common.Models;
using WinOptimizer.UI.Features.Optimize.ViewModels;

namespace WinOptimizer.UI.Features.Common.Controls;

/// <summary>Presents technical cards through the shared dialog builder and synchronizes the setting's open state.</summary>
public sealed partial class TechnicalDetailsButton : UserControl
{
    public static readonly DependencyProperty SettingProperty = DependencyProperty.Register(
        nameof(Setting), typeof(SettingItemViewModel), typeof(TechnicalDetailsButton), new PropertyMetadata(null, OnSettingChanged));

    private ContentDialog? _dialog;
    private readonly FrameworkElement _content;
    private bool _showing;

    public SettingItemViewModel? Setting
    {
        get => (SettingItemViewModel?)GetValue(SettingProperty);
        set => SetValue(SettingProperty, value);
    }

    public TechnicalDetailsButton()
    {
        InitializeComponent();
        // Transfer the named XAML body out of its resource owner before hosting it in a dialog.
        _content = (FrameworkElement)Resources["DetailsContent"];
        Resources.Remove("DetailsContent");
        Loaded += Control_Loaded;
        Unloaded += Control_Unloaded;
    }

    /// <summary>Moves the state subscription when a recycled card receives another setting.</summary>
    private static void OnSettingChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (TechnicalDetailsButton)sender;
        if (args.OldValue is SettingItemViewModel previous)
        {
            previous.PropertyChanged -= control.Setting_PropertyChanged;
            previous.IsTechnicalDetailsExpanded = false;
        }
        control._dialog?.Hide();
        if (control.IsLoaded && args.NewValue is SettingItemViewModel setting)
        {
            setting.PropertyChanged += control.Setting_PropertyChanged;
            control.SynchronizeDialog();
        }
    }

    private void Control_Loaded(object sender, RoutedEventArgs args)
    {
        if (Setting is { } setting) setting.PropertyChanged += Setting_PropertyChanged;
        SynchronizeDialog();
    }

    private void Control_Unloaded(object sender, RoutedEventArgs args)
    {
        if (Setting is { } setting)
        {
            setting.PropertyChanged -= Setting_PropertyChanged;
            setting.IsTechnicalDetailsExpanded = false;
        }
        _dialog?.Hide();
    }

    private void InfoButton_Click(object sender, RoutedEventArgs args) => Setting?.ToggleTechnicalDetails();

    private void Setting_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SettingItemViewModel.IsTechnicalDetailsExpanded)) SynchronizeDialog();
        else if (_dialog is { } dialog && Setting is { } setting)
        {
            if (args.PropertyName == nameof(SettingItemViewModel.Name)) dialog.Title = setting.Name;
            if (args.PropertyName == nameof(SettingItemViewModel.CloseTechnicalDetailsLabel)) dialog.CloseButtonText = setting.CloseTechnicalDetailsLabel;
        }
    }

    /// <summary>Serializes native dialogs and discards requests whose card closed or unloaded while queued.</summary>
    private async void SynchronizeDialog()
    {
        if (Setting is not { IsTechnicalDetailsExpanded: true } setting)
        {
            _dialog?.Hide();
            return;
        }
        if (_showing || !IsLoaded) return;
        _showing = true;
        try
        {
            await PopupHandler.RunModalAsync(async () =>
            {
                if (!IsLoaded || !ReferenceEquals(Setting, setting) || !setting.IsTechnicalDetailsExpanded || XamlRoot is not { } root)
                    return ContentDialogResult.None;
                var content = _content;
                var dialog = PopupHandler.CreateDialog(new PopupDialogOptions
                {
                    Title = setting.Name, Content = content,
                    CloseButtonText = setting.CloseTechnicalDetailsLabel, DefaultButton = ContentDialogButton.Close
                });
                _dialog = dialog;
                var maximumWidth = (double)Microsoft.UI.Xaml.Application.Current.Resources["Popup.MaxWidth"];
                dialog.Resources["ContentDialogMaxWidth"] = maximumWidth;
                // Reserve native padding/frame (50 DIPs), window margins, title and footer; only the cards scroll.
                void ResizeContent()
                {
                    content.Width = Math.Max(0, Math.Min(maximumWidth, root.Size.Width - 48) - 50);
                    content.MaxHeight = Math.Max(0, root.Size.Height - 240);
                }
                void Resize(XamlRoot sender, XamlRootChangedEventArgs args) => ResizeContent();
                ResizeContent();
                root.Changed += Resize;
                try { return await PopupHandler.ShowDialogAsync(dialog, root); }
                finally
                {
                    root.Changed -= Resize;
                    dialog.Content = null;
                    _dialog = null;
                }
            });
        }
        finally
        {
            setting.IsTechnicalDetailsExpanded = false;
            _showing = false;
            if (!ReferenceEquals(Setting, setting)) SynchronizeDialog();
        }
    }
}
