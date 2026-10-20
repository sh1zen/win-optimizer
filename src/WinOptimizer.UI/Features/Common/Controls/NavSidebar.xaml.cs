using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using WinOptimizer.UI.ViewModels;

namespace WinOptimizer.UI.Features.Common.Controls;

/// <summary>
/// Collapsible navigation sidebar containing NavButton controls.
/// </summary>
public sealed partial class NavSidebar : UserControl, INotifyPropertyChanged
{
    // Sidebar dimensions (matching NavigationView defaults)
    private const double ExpandedWidth = 80;
    private const double CompactWidth = 48;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<NavButtonClickedEventArgs>? ItemClicked;

    private Dictionary<string, NavButton>? _navButtons;

    #region Dependency Properties

    public static readonly DependencyProperty IsPaneOpenProperty =
        DependencyProperty.Register(
            nameof(IsPaneOpen),
            typeof(bool),
            typeof(NavSidebar),
            new PropertyMetadata(true, OnIsPaneOpenChanged));

    public static readonly DependencyProperty SelectedTagProperty =
        DependencyProperty.Register(
            nameof(SelectedTag),
            typeof(string),
            typeof(NavSidebar),
            new PropertyMetadata(null, OnSelectedTagChanged));

    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(
            nameof(ViewModel),
            typeof(MainWindowViewModel),
            typeof(NavSidebar),
            new PropertyMetadata(null));

    #endregion

    #region Properties

    /// <summary>
    /// Whether the sidebar pane is open (expanded) or closed (compact).
    /// </summary>
    public bool IsPaneOpen
    {
        get => (bool)GetValue(IsPaneOpenProperty);
        set => SetValue(IsPaneOpenProperty, value);
    }

    /// <summary>
    /// The currently selected navigation tag.
    /// </summary>
    public string? SelectedTag
    {
        get => (string?)GetValue(SelectedTagProperty);
        set => SetValue(SelectedTagProperty, value);
    }

    /// <summary>
    /// The ViewModel providing localized strings for nav buttons.
    /// </summary>
    public MainWindowViewModel? ViewModel
    {
        get => (MainWindowViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    /// <summary>
    /// Computed property: true when pane is closed (compact mode).
    /// </summary>
    public bool IsCompact => !IsPaneOpen;

    /// <summary>
    /// Computed property: actual width based on pane state.
    /// </summary>
    public double ActualSidebarWidth => IsPaneOpen ? ExpandedWidth : CompactWidth;

    /// <summary>
    /// Computed property: padding for nav panels based on pane state.
    /// </summary>
    public Thickness NavPanelPadding => IsPaneOpen ? new Thickness(5, 0, 5, 0) : new Thickness(4, 0, 4, 0);

    #endregion

    public NavSidebar()
    {
        this.InitializeComponent();
        InitializeNavButtonDictionary();

    }

    private void InitializeNavButtonDictionary()
    {
        _navButtons = new Dictionary<string, NavButton>
        {
            { "SoftwareApps", SoftwareAppsButton },
            { "Optimize", OptimizeButton },
            { "Cleaner", CleanerButton },
            { "Drivers", DriversButton },
            { "Functionalities", FunctionalitiesButton },
            { "Management", ManagementButton },
            { "Security", SecurityButton },
            { "Customize", CustomizeButton },
            { "AdvancedTools", AdvancedToolsButton },
            { "Settings", SettingsButton }
        };
    }

    #region Property Change Handlers

    private static void OnIsPaneOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is NavSidebar sidebar)
        {
            sidebar.NotifyPropertyChanged(nameof(IsCompact));
            sidebar.NotifyPropertyChanged(nameof(ActualSidebarWidth));
            sidebar.NotifyPropertyChanged(nameof(NavPanelPadding));
        }
    }

    private static void OnSelectedTagChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is NavSidebar sidebar)
        {
            sidebar.UpdateSelectionState();
        }
    }

    #endregion

    #region Event Handlers

    private void NavButton_Clicked(object sender, NavButtonClickedEventArgs e)
    {
        var tag = e.NavigationTag?.ToString();
        if (!string.IsNullOrEmpty(tag))
        {
            SelectedTag = tag;
            ItemClicked?.Invoke(this, e);
        }
    }

    #endregion

    #region Selection Management

    private void UpdateSelectionState()
    {
        if (_navButtons == null) return;

        foreach (var kvp in _navButtons)
        {
            kvp.Value.IsSelected = kvp.Key == SelectedTag;
        }
    }

    /// <summary>
    /// Sets the locked state for a specific navigation button.
    /// When locked, the button is dimmed, shows a lock icon, and blocks clicks.
    /// </summary>
    public void SetButtonLocked(string tag, bool isLocked, string? tooltip = null)
    {
        if (_navButtons != null && _navButtons.TryGetValue(tag, out var button))
        {
            button.IsLocked = isLocked;
            if (isLocked && !string.IsNullOrEmpty(tooltip))
            {
                ToolTipService.SetToolTip(button, tooltip);
            }
            else if (!isLocked)
            {
                ToolTipService.SetToolTip(button, null);
            }
        }
    }

    /// <summary>
    /// Gets a NavButton by its tag.
    /// </summary>
    /// <param name="tag">The navigation tag of the button to get.</param>
    /// <returns>The NavButton if found, null otherwise.</returns>
    public NavButton? GetButton(string tag)
    {
        if (_navButtons != null && _navButtons.TryGetValue(tag, out var button))
        {
            return button;
        }
        return null;
    }

    /// <summary>
    /// Sets badge value and status on the NavButton for the given tag.
    /// </summary>
    public void SetButtonBadge(string tag, int value, string status)
    {
        if (_navButtons != null && _navButtons.TryGetValue(tag, out var button))
        {
            button.BadgeValue = value;
            button.BadgeStatus = status;
        }
    }

    /// <summary>
    /// Clears all badges from all nav buttons.
    /// </summary>
    public void ClearAllBadges()
    {
        if (_navButtons == null) return;

        foreach (var button in _navButtons.Values)
        {
            button.BadgeValue = -1;
            button.BadgeStatus = string.Empty;
        }
    }

    #endregion

    private void NotifyPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
