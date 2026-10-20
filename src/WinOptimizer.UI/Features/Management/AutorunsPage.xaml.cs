using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.UI.Features.Management.ViewModels;

namespace WinOptimizer.UI.Features.Management;

/// <summary>Loads startup controls on entry and binds mode changes to consent-gated inventory loading.</summary>
public sealed partial class AutorunsPage : Page
{
    public AutorunsViewModel ViewModel { get; }
    public AutorunsPage() : this(App.Services.GetRequiredService<AutorunsViewModel>()) { }
    public AutorunsPage(AutorunsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        // Keep the virtualized list finitely sized, with whole-page scrolling on short windows.
        PageRoot.Height = 1000;
        SizeChanged += (_, args) => PageRoot.Height = Math.Max(1000, args.NewSize.Height);
        Loaded += async (_, _) => await ViewModel.LoadAsync();
    }
}
