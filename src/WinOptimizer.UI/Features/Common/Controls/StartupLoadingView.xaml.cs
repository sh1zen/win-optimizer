using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Helpers;

namespace WinOptimizer.UI.Features.Common.Controls;

/// <summary>Displays startup progress before services and the application shell are initialized.</summary>
public sealed partial class StartupLoadingView : UserControl
{
    /// <summary>Creates the loader without resolving services or reading preferences.</summary>
    public StartupLoadingView()
    {
        InitializeComponent();
        LoadingLogo.Source = new BitmapImage
        {
            DecodePixelWidth = 256,
            DecodePixelHeight = 256,
            DecodePixelType = DecodePixelType.Logical,
            UriSource = new Uri(AppBranding.ImageUri)
        };
    }

    internal TextBlock StatusText => LoadingStatusText;

    /// <summary>Applies the saved language once protected storage and localization are ready.</summary>
    internal void ApplyLocalization(ILocalizationService localization)
    {
        LoadingTitleText.Text = localization.GetString("App_Title");
        LoadingTaglineText.Text = localization.GetString("App_Tagline");
        LoadingStatusText.Text = localization.GetString("Loading_PreparingApp");
        FlowDirection = localization.IsRightToLeft
            ? Microsoft.UI.Xaml.FlowDirection.RightToLeft
            : Microsoft.UI.Xaml.FlowDirection.LeftToRight;
    }
}
