using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinOptimizer.UI.Features.Common.Controls;

/// <summary>Builds the shared heading, description and accent icon for application pages.</summary>
public sealed partial class PageHeader : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(PageHeader), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(PageHeader), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(Icon), typeof(PageHeader), new PropertyMetadata(Icon.Settings));
    public static readonly DependencyProperty DescriptionVisibilityProperty = DependencyProperty.Register(
        nameof(DescriptionVisibility), typeof(Visibility), typeof(PageHeader), new PropertyMetadata(Visibility.Visible));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public Icon Icon { get => (Icon)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public Visibility DescriptionVisibility { get => (Visibility)GetValue(DescriptionVisibilityProperty); set => SetValue(DescriptionVisibilityProperty, value); }

    public PageHeader() => InitializeComponent();
}
