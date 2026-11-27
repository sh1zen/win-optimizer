using FluentIcons.Common;
using FluentIcons.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Helpers;

namespace WinOptimizer.UI.Features.Common.Dialogs;

/// <summary>Presents the import source and the options applied to the chosen configuration.</summary>
public sealed partial class ConfigImportDialogContent : UserControl
{
    private readonly ILocalizationService _localization;

    internal ConfigImportDialogContent(ILocalizationService localization)
    {
        _localization = localization;
        InitializeComponent();

        Intro.Text = localization.GetString("Dialog_ImportOptions_Message");
        AddSource(Icon.FolderOpen, "Dialog_ImportConfig_Option_Own", ImportOption.ImportOwn);
        AddSource(Icon.Star, "Dialog_ImportConfig_Option_Recommended", ImportOption.ImportRecommended);
        AddSource(Icon.History, "Dialog_ImportConfig_Option_Backup", ImportOption.ImportBackup);
        AddSource(Icon.ArrowReset, "Dialog_ImportConfig_Option_Defaults", ImportOption.ImportWindowsDefaults);

        WindowsAppsLabel.Text = localization.GetString("Config_Import_Options_WindowsApps") ?? "Windows Apps";
        ExternalAppsLabel.Text = localization.GetString("Config_Import_Options_ExternalApps") ?? "External Apps";
        var uninstall = localization.GetString("Config_Import_Options_Uninstall") ?? "Uninstall";
        var selectOnly = localization.GetString("Config_Import_Options_SelectOnly") ?? "Select Only";
        SetAppAction(WindowsAppsUninstall, WindowsAppsLabel.Text, uninstall);
        SetAppAction(WindowsAppsSelectOnly, WindowsAppsLabel.Text, selectOnly);
        SetAppAction(ExternalAppsUninstall, ExternalAppsLabel.Text, uninstall);
        SetAppAction(ExternalAppsSelectOnly, ExternalAppsLabel.Text, selectOnly);
        SetDesktopOption(ThemeWallpaper, "Config_Import_Options_ThemeWallpaper", "Apply default wallpaper for theme");
        SetDesktopOption(CleanTaskbar, "Config_Import_Options_CleanTaskbar", "Clean Taskbar");
        SetDesktopOption(CleanStartMenu, "Config_Import_Options_CleanStartMenu", "Clean Start Menu");
    }

    internal ImportOption? SelectedOption { get; private set; }
    internal event Action? SourceSelected;

    /// <summary>Reads the selected actions when the dialog closes.</summary>
    internal ImportOptions GetOptions() => new()
    {
        ProcessWindowsAppsRemoval = WindowsAppsUninstall.IsChecked == true,
        ProcessExternalAppsRemoval = ExternalAppsUninstall.IsChecked == true,
        ApplyThemeWallpaper = ThemeWallpaper.IsChecked == true,
        ApplyCleanTaskbar = CleanTaskbar.IsChecked == true,
        ApplyCleanStartMenu = CleanStartMenu.IsChecked == true
    };

    /// <summary>Populates a radio card with a Fluent icon and localized text that can wrap.</summary>
    private void AddSource(Icon icon, string textKey, ImportOption option)
    {
        var title = _localization.GetString(textKey + "_Title");
        var description = _localization.GetString(textKey + "_Description");
        var layout = new Grid { ColumnSpacing = 12 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(new FluentIcon
        {
            Icon = icon, IconVariant = IconVariant.Regular, FontSize = 20,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, Style = (Style)Resources["ImportSourceTitleStyle"] });
        text.Children.Add(new TextBlock { Text = description, Style = (Style)Resources["ImportSourceDescriptionStyle"] });
        Grid.SetColumn(text, 1);
        layout.Children.Add(text);

        var card = new RadioButton
        {
            Content = layout, GroupName = "ImportSource", Style = (Style)Resources["ImportSourceCardStyle"]
        };
        AutomationProperties.SetName(card, $"{title}. {description}");
        card.Checked += (_, _) =>
        {
            SelectedOption = option;
            SourceSelected?.Invoke();
            DialogAccessibilityHelper.AnnounceToNarrator(card,
                $"{_localization.GetString("Accessibility_Selected") ?? "Selected"}: {title}", "ConfigOptionSelected");
        };
        Sources.Children.Add(card);
    }

    /// <summary>Labels the app removal choices with their category for Narrator.</summary>
    private static void SetAppAction(RadioButton radio, string category, string text)
    {
        radio.Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        var name = $"{category}: {text}";
        AutomationProperties.SetName(radio, name);
        radio.Checked += (_, _) => DialogAccessibilityHelper.AnnounceToNarrator(radio, name);
    }

    /// <summary>Labels a desktop option and announces changes to its checked state.</summary>
    private void SetDesktopOption(CheckBox checkBox, string key, string fallback)
    {
        var text = _localization.GetString(key) ?? fallback;
        checkBox.Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(checkBox, text);
        checkBox.Checked += (_, _) => DialogAccessibilityHelper.AnnounceToNarrator(
            checkBox, $"{text}: {_localization.GetString("Accessibility_Checked") ?? "Checked"}");
        checkBox.Unchecked += (_, _) => DialogAccessibilityHelper.AnnounceToNarrator(
            checkBox, $"{text}: {_localization.GetString("Accessibility_Unchecked") ?? "Unchecked"}");
    }
}
