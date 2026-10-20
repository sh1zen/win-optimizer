using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinOptimizer.UI.Features.Common.Controls;

/// <summary>Builds the common clickable category card, including optional title and status badges.</summary>
public sealed partial class NavigationCard : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(NavigationCard), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(NavigationCard), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(IconElement), typeof(NavigationCard), new PropertyMetadata(null));
    public static readonly DependencyProperty TitleAccessoryProperty = DependencyProperty.Register(
        nameof(TitleAccessory), typeof(UIElement), typeof(NavigationCard), new PropertyMetadata(null));
    public static readonly DependencyProperty BadgesProperty = DependencyProperty.Register(
        nameof(Badges), typeof(UIElement), typeof(NavigationCard), new PropertyMetadata(null));
    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(
        nameof(Actions), typeof(UIElement), typeof(NavigationCard), new PropertyMetadata(null));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public IconElement? Icon { get => (IconElement?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public UIElement? TitleAccessory { get => (UIElement?)GetValue(TitleAccessoryProperty); set => SetValue(TitleAccessoryProperty, value); }
    public UIElement? Badges { get => (UIElement?)GetValue(BadgesProperty); set => SetValue(BadgesProperty, value); }
    public UIElement? Actions { get => (UIElement?)GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }
    public event RoutedEventHandler? Click;

    public NavigationCard()
    {
        InitializeComponent();
        SizeChanged += (_, args) => Card.ContentAlignment = args.NewSize.Width < 720 && (Badges != null || Actions != null)
            ? CommunityToolkit.WinUI.Controls.ContentAlignment.Vertical
            : CommunityToolkit.WinUI.Controls.ContentAlignment.Right;
    }

    /// <summary>Cards without badges or actions reserve no footer space.</summary>
    public Visibility FooterVisibility(UIElement? badges, UIElement? actions) =>
        badges != null || actions != null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Forwards native card activation without introducing a second focus or command target.</summary>
    private void Card_Click(object sender, RoutedEventArgs e) => Click?.Invoke(this, e);
}
