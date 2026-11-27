using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using FluentIcons.WinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Controls;
using WinOptimizer.UI.Features.Common.Helpers;

namespace WinOptimizer.UI.Features.Disks;

/// <summary>Lists the disk tools using the app's shared navigation cards.</summary>
public sealed partial class DisksPage : Page
{
    private readonly ILocalizationService _localization = App.Services.GetRequiredService<ILocalizationService>();

    public string Title => _localization.GetString("Nav_Disks");
    public string Description => _localization.GetString("Disks_Description");

    public DisksPage()
    {
        InitializeComponent();
        PageScrollHelper.Attach(this, PageScroll);
        foreach (var tool in DiskToolCatalog.Tools)
        {
            var card = new NavigationCard
            {
                Tag = tool,
                Icon = new FluentIcon { Icon = tool.GetIcon(), IconVariant = IconVariant.Regular, FontSize = 28 },
                Command = new RelayCommand(() => OpenTool(tool))
            };
            ToolCards.Children.Add(card);
        }

        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _localization.LanguageChanged += Language_Changed;
        RefreshText();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e) => _localization.LanguageChanged -= Language_Changed;
    private void Language_Changed(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(RefreshText);

    private void RefreshText()
    {
        Bindings.Update();
        foreach (var card in ToolCards.Children.OfType<NavigationCard>())
        {
            var tool = (DiskTool)card.Tag;
            card.Title = _localization.GetString(tool.TitleKey());
            card.Description = _localization.GetString(tool.DescriptionKey());
        }
    }

    private void OpenTool(DiskTool tool)
    {
        if (Frame is { } frame && ReferenceEquals(frame.Content, this))
            frame.Navigate(typeof(DiskToolPage), tool);
    }
}
