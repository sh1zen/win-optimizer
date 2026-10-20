using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using WinOptimizer.UI.Features.Cleaner.ViewModels;

namespace WinOptimizer.UI.Features.Cleaner;

/// <summary>Settings surface for the same Cleaner choices used by the scan page.</summary>
public sealed partial class CleanerSettingsControl : UserControl
{
    public static readonly DependencyProperty SectionProperty = DependencyProperty.Register(nameof(Section),
        typeof(CleanerSettingsSection), typeof(CleanerSettingsControl),
        new PropertyMetadata(CleanerSettingsSection.All, (owner, _) => ((CleanerSettingsControl)owner).UpdateSection()));
    public CleanerSettingsSection Section
    {
        get => (CleanerSettingsSection)GetValue(SectionProperty);
        set => SetValue(SectionProperty, value);
    }
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(nameof(ViewModel),
        typeof(CleanerSettingsViewModel), typeof(CleanerSettingsControl), new PropertyMetadata(null));
    public CleanerSettingsViewModel ViewModel
    {
        get => (CleanerSettingsViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }
    public CleanerSettingsControl()
    {
        InitializeComponent();
        UpdateSection();
        Loaded += async (_, _) => await ViewModel.LoadAsync();
    }

    /// <summary>Shows one preference category while retaining its shared settings owner.</summary>
    private void UpdateSection()
    {
        if (BackupsSection is null) return;
        BackupsSection.Visibility = Visible(CleanerSettingsSection.Backups);
        RegistrySection.Visibility = Visible(CleanerSettingsSection.Registry);
        PrivacySection.Visibility = Visible(CleanerSettingsSection.Privacy);
        JunkSection.Visibility = Visible(CleanerSettingsSection.Junk);
        ShortcutsSection.Visibility = Visible(CleanerSettingsSection.Shortcuts);
        DiskSection.Visibility = Visible(CleanerSettingsSection.Disk);
    }

    private Visibility Visible(CleanerSettingsSection section) =>
        Section == CleanerSettingsSection.All || Section == section ? Visibility.Visible : Visibility.Collapsed;
}
