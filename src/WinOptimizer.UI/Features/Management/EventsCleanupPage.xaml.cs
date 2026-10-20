using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.UI.Features.Management.ViewModels;

namespace WinOptimizer.UI.Features.Management;

/// <summary>Loads event inventory only when the management tool is opened.</summary>
public sealed partial class EventsCleanupPage : Page
{
    public EventsCleanupViewModel ViewModel { get; }
    public EventsCleanupPage() : this(App.Services.GetRequiredService<EventsCleanupViewModel>()) { }
    public EventsCleanupPage(EventsCleanupViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.LoadAsync();
    }
}
