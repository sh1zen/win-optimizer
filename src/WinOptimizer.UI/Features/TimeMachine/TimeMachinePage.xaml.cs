using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace WinOptimizer.UI.Features.TimeMachine;

/// <summary>Hosts TimeMachine and cancels pending page work when navigating away.</summary>
public sealed partial class TimeMachinePage : Page
{
    public TimeMachineViewModel ViewModel { get; } = App.Services.GetRequiredService<TimeMachineViewModel>();

    public TimeMachinePage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.RefreshCommand.Execute(null);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.Cancel();
        base.OnNavigatedFrom(e);
    }

    /// <summary>Opens the date represented by the activated history card.</summary>
    private void DayCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TimeMachineDay day }) ViewModel.OpenDayCommand.Execute(day);
    }
}
