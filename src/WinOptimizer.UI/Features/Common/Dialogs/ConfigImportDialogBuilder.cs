using WinOptimizer.UI.Features.Common.Models;
using WinOptimizer.UI.Features.Common.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Helpers;

namespace WinOptimizer.UI.Features.Common.Dialogs;

/// <summary>
/// Encapsulates the UI-building logic for the config import options dialog.
/// Extracted from <see cref="Services.DialogService.ShowConfigImportOptionsDialogAsync"/>
/// to keep DialogService focused on dialog lifecycle management.
/// </summary>
internal class ConfigImportDialogBuilder
{
    private readonly ILocalizationService _localization;

    // Selection state
    private ImportOption? _selectedOption;
    private Button? _selectedCardButton;
    private Border? _selectedBgBorder;
    private Border? _selectedAccentBorder;

    // Controls needed for ExtractResult
    private ContentDialog _dialog = null!;
    private RadioButton _winAppsUninstallRadio = null!;
    private RadioButton _winAppsSelectOnlyRadio = null!;
    private RadioButton _extAppsUninstallRadio = null!;
    private RadioButton _extAppsSelectOnlyRadio = null!;
    private CheckBox _themeWallpaperCheckbox = null!;
    private CheckBox _cleanTaskbarCheckbox = null!;
    private CheckBox _cleanStartMenuCheckbox = null!;

    public ConfigImportDialogBuilder(ILocalizationService localization)
    {
        _localization = localization;
    }

    /// <summary>
    /// Builds the ContentDialog with all UI elements. The caller is responsible for
    /// showing the result through PopupHandler.
    /// </summary>
    public ContentDialog Build(XamlRoot xamlRoot)
    {
        bool isDark = (xamlRoot.Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark;

        _dialog = PopupHandler.CreateDialog(new PopupDialogOptions
        {
            Title = _localization.GetString("Dialog_ImportConfig_Title"),
            PrimaryButtonText = _localization.GetString("Button_Apply"),
            IsPrimaryButtonEnabled = false,
            CloseButtonText = _localization.GetString("Button_Cancel"),
            DefaultButton = ContentDialogButton.None,
            MinWidth = 500
        });

        // Card 1: Import own config - FolderOpen icon
        var ownIcon = new FluentIcons.WinUI.FluentIcon { Icon = FluentIcons.Common.Icon.FolderOpen, IconVariant = FluentIcons.Common.IconVariant.Regular, FontSize = 24, VerticalAlignment = VerticalAlignment.Center };
        var ownCard = CreateOptionCard(ownIcon,
            "Dialog_ImportConfig_Option_Own_Title",
            "Dialog_ImportConfig_Option_Own_Description",
            ImportOption.ImportOwn, isDark);

        // Card 2: Import recommended config - win-optimizer logo
        var recIcon = new Image
        {
            Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new System.Uri(AppBranding.ImageUri)),
            Width = 24,
            Height = 24,
            VerticalAlignment = VerticalAlignment.Center
        };
        var recCard = CreateOptionCard(recIcon,
            "Dialog_ImportConfig_Option_Recommended_Title",
            "Dialog_ImportConfig_Option_Recommended_Description",
            ImportOption.ImportRecommended, isDark);

        // Card 3: Import backup config - History icon
        var backupIcon = new FluentIcons.WinUI.FluentIcon { Icon = FluentIcons.Common.Icon.History, IconVariant = FluentIcons.Common.IconVariant.Regular, FontSize = 24, VerticalAlignment = VerticalAlignment.Center };
        var backupCard = CreateOptionCard(backupIcon,
            "Dialog_ImportConfig_Option_Backup_Title",
            "Dialog_ImportConfig_Option_Backup_Description",
            ImportOption.ImportBackup, isDark);

        // Card 4: Import Windows defaults - Refresh icon
        var defaultsIcon = new FluentIcons.WinUI.FluentIcon { Icon = FluentIcons.Common.Icon.ArrowReset, IconVariant = FluentIcons.Common.IconVariant.Regular, FontSize = 24, VerticalAlignment = VerticalAlignment.Center };
        var defaultsCard = CreateOptionCard(defaultsIcon,
            "Dialog_ImportConfig_Option_Defaults_Title",
            "Dialog_ImportConfig_Option_Defaults_Description",
            ImportOption.ImportWindowsDefaults, isDark, isLast: true);

        var (optionsPanel, importControls) = BuildImportOptionsGrid();
        _winAppsUninstallRadio = importControls.WinAppsUninstall;
        _winAppsSelectOnlyRadio = importControls.WinAppsSelectOnly;
        _extAppsUninstallRadio = importControls.ExtAppsUninstall;
        _extAppsSelectOnlyRadio = importControls.ExtAppsSelectOnly;
        _themeWallpaperCheckbox = importControls.ThemeWallpaper;
        _cleanTaskbarCheckbox = importControls.CleanTaskbar;
        _cleanStartMenuCheckbox = importControls.CleanStartMenu;

        SetImportControlsEnabled(true);

        var contentPanel = new StackPanel { Spacing = 0 };
        contentPanel.Children.Add(new TextBlock
        {
            Text = _localization.GetString("Dialog_ImportOptions_Message"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });
        contentPanel.Children.Add(ownCard);
        contentPanel.Children.Add(recCard);
        contentPanel.Children.Add(backupCard);
        contentPanel.Children.Add(defaultsCard);
        contentPanel.Children.Add(optionsPanel);

        var scrollViewer = new ScrollViewer
        {
            Padding = (Thickness)Microsoft.UI.Xaml.Application.Current.Resources["ScrollContentPadding"],
            Content = contentPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 480
        };

        _dialog.Content = scrollViewer;
        return _dialog;
    }

    /// <summary>
    /// Extracts the result from the dialog after it has been shown.
    /// Must be called after the dialog's ShowAsync returns.
    /// </summary>
    public (ImportOption? Option, ImportOptions Options) ExtractResult(ContentDialogResult dialogResult)
    {
        if (dialogResult != ContentDialogResult.Primary)
        {
            _selectedOption = null; // Cancel was clicked, discard any selection
        }

        var importOptions = new ImportOptions
        {
            ProcessWindowsAppsRemoval = _winAppsUninstallRadio.IsChecked == true,
            // Select Only leaves the operation flags unset and pre-selects apps.
            ProcessExternalAppsRemoval = _extAppsUninstallRadio.IsChecked == true,
            ApplyThemeWallpaper = _themeWallpaperCheckbox.IsChecked == true,
            ApplyCleanTaskbar = _cleanTaskbarCheckbox.IsChecked == true,
            ApplyCleanStartMenu = _cleanStartMenuCheckbox.IsChecked == true,
        };
        return (_selectedOption, importOptions);
    }

    private void SetImportControlsEnabled(bool enabled)
    {
        _winAppsUninstallRadio.IsEnabled = enabled;
        _winAppsSelectOnlyRadio.IsEnabled = enabled;
        _extAppsUninstallRadio.IsEnabled = enabled;
        _extAppsSelectOnlyRadio.IsEnabled = enabled;
        _themeWallpaperCheckbox.IsEnabled = enabled;
        _cleanTaskbarCheckbox.IsEnabled = enabled;
        _cleanStartMenuCheckbox.IsEnabled = enabled;
    }

    private Button CreateOptionCard(UIElement icon, string titleKey, string descKey, ImportOption option, bool isDark, bool isLast = false)
    {
        var titleText = _localization.GetString(titleKey);
        var descText = _localization.GetString(descKey);

        var textPanel = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        textPanel.Children.Add(new TextBlock
        {
            Text = titleText,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 14
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = descText,
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap
        });

        var contentPanel2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(12, 8, 12, 8) };
        contentPanel2.Children.Add(icon);
        contentPanel2.Children.Add(textPanel);

        // Layer 0: Background fill with subtle border
        var bgBorder = new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderBrush = new SolidColorBrush(isDark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4)
        };

        // Layer 1: Accent border
        var accentBorder = new Border
        {
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(4)
        };

        var cardVisual = new Grid();
        cardVisual.Children.Add(bgBorder);
        cardVisual.Children.Add(accentBorder);
        cardVisual.Children.Add(contentPanel2);

        var cardButton = new Button
        {
            Content = cardVisual,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 0, isLast ? 0 : 6)
        };
        AutomationProperties.SetName(cardButton, $"{titleText}. {descText}");

        // Override Button's built-in hover/press visual states so only our custom bgBorder shows
        cardButton.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        cardButton.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        cardButton.Resources["ButtonBorderBrushPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        cardButton.Resources["ButtonBorderBrushPressed"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        cardButton.Click += (_, _) =>
        {
            // Re-click same card: no-op
            if (_selectedCardButton == cardButton) return;

            // Deselect previous card
            if (_selectedBgBorder != null)
                _selectedBgBorder.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            if (_selectedAccentBorder != null)
                _selectedAccentBorder.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

            // Select this card
            bgBorder.Background = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["SubtleFillColorTertiaryBrush"];
            accentBorder.BorderBrush = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["AccentFillColorDefaultBrush"];

            _selectedCardButton = cardButton;
            _selectedBgBorder = bgBorder;
            _selectedAccentBorder = accentBorder;
            _selectedOption = option;
            _dialog.IsPrimaryButtonEnabled = true;

            // Announce the selection to Narrator
            DialogAccessibilityHelper.AnnounceToNarrator(
                cardButton,
                $"{_localization.GetString("Accessibility_Selected") ?? "Selected"}: {titleText}",
                "ConfigOptionSelected");
        };

        cardButton.PointerEntered += (_, _) =>
        {
            if (_selectedCardButton != cardButton)
                bgBorder.Background = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        };

        cardButton.PointerExited += (_, _) =>
        {
            if (_selectedCardButton != cardButton)
                bgBorder.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        };

        // Show hover-like state on keyboard focus
        cardButton.GotFocus += (_, _) =>
        {
            if (_selectedCardButton != cardButton)
                bgBorder.Background = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        };
        cardButton.LostFocus += (_, _) =>
        {
            if (_selectedCardButton != cardButton)
                bgBorder.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        };

        return cardButton;
    }

    /// <summary>
    /// Record to hold references to import option controls for result extraction.
    /// </summary>
    private record ImportOptionControls(
        RadioButton WinAppsUninstall,
        RadioButton WinAppsSelectOnly,
        RadioButton ExtAppsUninstall,
        RadioButton ExtAppsSelectOnly,
        CheckBox ThemeWallpaper,
        CheckBox CleanTaskbar,
        CheckBox CleanStartMenu);

    private (StackPanel Panel, ImportOptionControls Controls) BuildImportOptionsGrid()
    {
        // Use a Grid so label and radio columns align across both rows
        var appsGrid = new Grid
        {
            Margin = new Thickness(0, 3, 0, 0),
            RowSpacing = 0,
            ColumnSpacing = 2
        };
        appsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        appsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        appsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        appsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        appsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var winAppsText = _localization.GetString("Config_Import_Options_WindowsApps") ?? "Windows Apps";
        var extAppsText = _localization.GetString("Config_Import_Options_ExternalApps") ?? "External Apps";
        var uninstallText = _localization.GetString("Config_Import_Options_Uninstall") ?? "Uninstall";
        var selectOnlyText = _localization.GetString("Config_Import_Options_SelectOnly") ?? "Select Only";

        // Row 0: Windows Apps
        var winAppsLabel = new TextBlock
        {
            Text = winAppsText,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(winAppsLabel, 0);
        Grid.SetColumn(winAppsLabel, 0);

        var winAppsUninstallRadio = CreateRadioButton(uninstallText, "WindowsApps", true, winAppsText, uninstallText, 0, 1);
        var winAppsSelectOnlyRadio = CreateRadioButton(selectOnlyText, "WindowsApps", false, winAppsText, selectOnlyText, 0, 2);

        // Row 1: External Apps
        var extAppsLabel = new TextBlock
        {
            Text = extAppsText,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(extAppsLabel, 1);
        Grid.SetColumn(extAppsLabel, 0);

        var extAppsUninstallRadio = CreateRadioButton(uninstallText, "ExternalApps", false, extAppsText, uninstallText, 1, 1);
        var extAppsSelectOnlyRadio = CreateRadioButton(selectOnlyText, "ExternalApps", true, extAppsText, selectOnlyText, 1, 2);

        appsGrid.Children.Add(winAppsLabel);
        appsGrid.Children.Add(winAppsUninstallRadio);
        appsGrid.Children.Add(winAppsSelectOnlyRadio);
        appsGrid.Children.Add(extAppsLabel);
        appsGrid.Children.Add(extAppsUninstallRadio);
        appsGrid.Children.Add(extAppsSelectOnlyRadio);

        // Customize action checkboxes
        var themeWallpaperCheckbox = CreateAccessibleCheckBox(
            _localization.GetString("Config_Import_Options_ThemeWallpaper") ?? "Apply default wallpaper for theme",
            isChecked: true, margin: new Thickness(0, 2, 0, 0));

        var cleanTaskbarCheckbox = CreateAccessibleCheckBox(
            _localization.GetString("Config_Import_Options_CleanTaskbar") ?? "Clean Taskbar",
            isChecked: true);

        var cleanStartMenuCheckbox = CreateAccessibleCheckBox(
            _localization.GetString("Config_Import_Options_CleanStartMenu") ?? "Clean Start Menu",
            isChecked: true);

        var optionsPanel = new StackPanel
        {
            Spacing = 0,
            Opacity = 1.0
        };
        optionsPanel.Children.Add(appsGrid);
        optionsPanel.Children.Add(themeWallpaperCheckbox);
        optionsPanel.Children.Add(cleanTaskbarCheckbox);
        optionsPanel.Children.Add(cleanStartMenuCheckbox);

        var controls = new ImportOptionControls(
            winAppsUninstallRadio, winAppsSelectOnlyRadio,
            extAppsUninstallRadio, extAppsSelectOnlyRadio,
            themeWallpaperCheckbox, cleanTaskbarCheckbox, cleanStartMenuCheckbox);

        return (optionsPanel, controls);
    }

    private RadioButton CreateRadioButton(string text, string groupName, bool isChecked, string categoryText, string optionText, int row, int column)
    {
        var radio = new RadioButton
        {
            Content = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
            GroupName = groupName,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsChecked = isChecked,
            IsEnabled = false,
            MinWidth = 0, MinHeight = 0,
            Padding = new Thickness(4, 0, 4, 0)
        };
        AutomationProperties.SetName(radio, $"{categoryText}: {optionText}");
        radio.Checked += (_, _) => DialogAccessibilityHelper.AnnounceToNarrator(radio, $"{categoryText}: {optionText}");
        Grid.SetRow(radio, row);
        Grid.SetColumn(radio, column);
        return radio;
    }

    private CheckBox CreateAccessibleCheckBox(string text, bool isChecked, Thickness? margin = null)
    {
        var checkBox = new CheckBox
        {
            Content = text,
            IsChecked = isChecked,
            IsEnabled = false,
            MinHeight = 0,
            Padding = new Thickness(4, 2, 4, 2)
        };
        if (margin.HasValue)
            checkBox.Margin = margin.Value;

        checkBox.Checked += (_, _) => DialogAccessibilityHelper.AnnounceToNarrator(
            checkBox,
            $"{text}: {_localization.GetString("Accessibility_Checked") ?? "Checked"}");
        checkBox.Unchecked += (_, _) => DialogAccessibilityHelper.AnnounceToNarrator(
            checkBox,
            $"{text}: {_localization.GetString("Accessibility_Unchecked") ?? "Unchecked"}");

        return checkBox;
    }
}
