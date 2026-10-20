using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.UI.Features.Common.Helpers;

namespace WinOptimizer.PopupTests;

/// <summary>Checks the production menu resource pattern with real compiled bindings and native actions.</summary>
public sealed partial class MenuFixture : UserControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(MenuFixture), new PropertyMetadata("Original"));
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public static readonly DependencyProperty SelectedProperty = DependencyProperty.Register(
        nameof(Selected), typeof(bool), typeof(MenuFixture), new PropertyMetadata(false));
    public bool Selected { get => (bool)GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }
    public int Clicks { get; private set; }
    public MenuFlyout Popup => (MenuFlyout)Anchor.Flyout;
    public MenuFlyoutItemBase OriginalAction => Action;

    public MenuFixture()
    {
        InitializeComponent();
        PopupHandler.AttachMenu(Anchor, ["Action"]);
    }

    private void Action_Click(object sender, RoutedEventArgs args) => Clicks++;
    public void Open() => PopupHandler.SetIsOpen(Anchor, true);
}
