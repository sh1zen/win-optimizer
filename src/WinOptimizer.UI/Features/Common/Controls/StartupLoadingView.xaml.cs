using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Helpers;
using WinOptimizer.UI.Features.Common.Helpers;

namespace WinOptimizer.UI.Features.Common.Controls;

/// <summary>Displays startup progress before services and the application shell are initialized.</summary>
public sealed partial class StartupLoadingView : UserControl
{
    /// <summary>Creates the loader without resolving services or reading preferences.</summary>
    public StartupLoadingView()
    {
        InitializeComponent();
        // Reserve two scaled text lines so ordinary status changes do not recenter the splash.
        LoadingStatusText.MinHeight = 40 * TextScaleHelper.Factor;
        LoadingLogo.Source = new BitmapImage
        {
            DecodePixelWidth = 256,
            DecodePixelHeight = 256,
            DecodePixelType = DecodePixelType.Logical,
            UriSource = new Uri(AppBranding.ImageUri)
        };
    }

    internal TextBlock StatusText => LoadingStatusText;
    internal Grid TitleBar => LoadingTitleBar;

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
