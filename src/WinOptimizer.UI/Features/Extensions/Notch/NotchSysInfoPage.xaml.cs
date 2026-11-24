using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace WinOptimizer.UI.Features.Extensions.Notch;

/// <summary>Edits the selected readings and compact pins for the Notch Sys info section.</summary>
public sealed partial class NotchSysInfoPage : Page
{
    public NotchExtension ViewModel { get; private set; } = null!;

    public NotchSysInfoPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs args)
    {
        base.OnNavigatedTo(args);
        ViewModel = (NotchExtension)args.Parameter;
    }
}
