using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.UI.ViewModels;

namespace WinOptimizer.UI.Features.Common.Controls;

/// <summary>Presents update notifications without opening a popup or taking keyboard focus.</summary>
public sealed partial class UpdateToast : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(UpdateCheckViewModel), typeof(UpdateToast),
        new PropertyMetadata(null, OnViewModelChanged));

    private readonly DispatcherQueueTimer _dismissTimer;
    private UpdateCheckViewModel? _subscribedModel;

    public UpdateCheckViewModel? ViewModel
    {
        get => (UpdateCheckViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public UpdateToast()
    {
        _dismissTimer = DispatcherQueue.CreateTimer();
        _dismissTimer.Interval = TimeSpan.FromSeconds(8);
        _dismissTimer.IsRepeating = false;
        _dismissTimer.Tick += (_, _) =>
        {
            if (_subscribedModel is { IsUpdateToastTransient: true } viewModel) viewModel.DismissUpdateToast();
        };

        InitializeComponent();
        Loaded += (_, _) => Observe(ViewModel);
        Unloaded += (_, _) => Observe(null);
    }

    private static void OnViewModelChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var toast = (UpdateToast)sender;
        if (toast.IsLoaded) toast.Observe((UpdateCheckViewModel?)args.NewValue);
    }

    /// <summary>Observes new notices only while the toast is part of the shell.</summary>
    private void Observe(UpdateCheckViewModel? viewModel)
    {
        if (ReferenceEquals(_subscribedModel, viewModel)) return;
        if (_subscribedModel is not null) _subscribedModel.UpdateToastPresented -= RestartDismissTimer;
        _subscribedModel = viewModel;
        if (viewModel is not null) viewModel.UpdateToastPresented += RestartDismissTimer;
        RestartDismissTimer();
    }

    /// <summary>Times only the informational result that needs no attention or action.</summary>
    private void RestartDismissTimer()
    {
        _dismissTimer.Stop();
        if (_subscribedModel is { IsUpdateToastOpen: true, IsUpdateToastTransient: true })
            _dismissTimer.Start();
    }

    private void Notification_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (sender.IsOpen) return;
        _dismissTimer.Stop();
        ViewModel?.DismissUpdateToast();
    }
}
