using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace WinOptimizer.UI.Features.Extensions.Notch;

public sealed partial class NotchWindow
{
    private readonly Border[] _overviewDropTargets;
    private (Pointer Pointer, NotchView View, RadioButton Tab, Point Origin)? _overviewPress;
    private bool _overviewDragging;
    private int _overviewHover = -1;

    /// <summary>Shows drop outlines in each saved slot, including unoccupied slots.</summary>
    private Border[] CreateOverviewDropTargets()
    {
        var targets = new Border[NotchOverviewLayout.SlotCount];
        for (int slot = 0; slot < targets.Length; slot++)
        {
            var target = new Border
            {
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
                BorderBrush = (Brush)Root.Resources["NotchMuted"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14)
            };
            AutomationProperties.SetAccessibilityView(target, AccessibilityView.Raw);
            Grid.SetRow(target, slot / NotchOverviewLayout.Columns);
            Grid.SetColumn(target, slot % NotchOverviewLayout.Columns);
            PageContent.Children.Add(target);
            targets[slot] = target;
        }
        return targets;
    }

    private void RegisterOverviewDrag()
    {
        foreach (var page in _views.Where(page => page.Page is not null))
            page.Tab.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnOverviewPointerPressed), true);
        Panel.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnOverviewPointerMoved), true);
        Panel.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnOverviewPointerReleased), true);
        Panel.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnOverviewPointerCanceled), true);
        Panel.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnOverviewPointerCanceled), true);
    }

    /// <summary>Takes over the tab's pointer so a click can still select it and a drag can stay in Overview.</summary>
    private void OnOverviewPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (!_expanded || _view != NotchView.Overview || _overviewPress is not null ||
            !_extension.CanEdit || sender is not RadioButton tab) return;
        var view = _views.First(page => page.Tab == tab).View;
        var point = args.GetCurrentPoint(Panel);
        if (args.Pointer.PointerDeviceType == PointerDeviceType.Mouse && !point.Properties.IsLeftButtonPressed) return;

        tab.ReleasePointerCapture(args.Pointer);
        if (!Panel.CapturePointer(args.Pointer)) return;
        _overviewPress = (args.Pointer, view, tab, point.Position);
        args.Handled = true;
    }

    private void OnOverviewPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_overviewPress is not { } press || press.Pointer.PointerId != args.Pointer.PointerId) return;
        args.Handled = true;
        MoveOverviewDrag(args, press.Tab, press.Origin);
    }

    private void MoveOverviewDrag(PointerRoutedEventArgs args, RadioButton source, Point origin)
    {
        if (!_overviewDragging)
        {
            var position = args.GetCurrentPoint(Panel).Position;
            if (Math.Abs(position.X - origin.X) < 5 && Math.Abs(position.Y - origin.Y) < 5) return;

            _overviewDragging = true;
            OverviewStatus.Visibility = Visibility.Collapsed;
            source.Opacity = 0.55;
            ArrangeOverview();
            PageContent.UpdateLayout();
        }
        SetOverviewHover(Array.FindIndex(_overviewDropTargets, target => IsOver(target, args)));
    }

    private void SetOverviewHover(int slot)
    {
        if (slot == _overviewHover) return;
        if (_overviewHover >= 0)
        {
            var previous = _overviewDropTargets[_overviewHover];
            previous.BorderBrush = (Brush)Root.Resources["NotchMuted"];
            previous.BorderThickness = new Thickness(1);
        }
        _overviewHover = slot;
        if (slot < 0) return;
        var target = _overviewDropTargets[slot];
        target.BorderBrush = (Brush)Root.Resources["NotchStatusValue"];
        target.BorderThickness = new Thickness(2);
    }

    private async void OnOverviewPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_overviewPress is not { } press || press.Pointer.PointerId != args.Pointer.PointerId) return;
        args.Handled = true;
        MoveOverviewDrag(args, press.Tab, press.Origin);
        int slot = _overviewHover;
        bool clicked = !_overviewDragging && IsOver(press.Tab, args);
        EndOverviewDrag();

        if (clicked) SelectView(press.View);
        if (slot < 0) return;
        await _extension.PlaceOverviewTileAsync(press.View, slot);
        if (_closed || !_expanded || _view != NotchView.Overview || !_extension.HasError) return;
        OverviewError.Text = _extension.Error;
        OverviewStatus.Visibility = Visibility.Visible;
    }

    private static bool IsOver(FrameworkElement element, PointerRoutedEventArgs args)
    {
        var position = args.GetCurrentPoint(element).Position;
        return position.X >= 0 && position.X < element.ActualWidth &&
            position.Y >= 0 && position.Y < element.ActualHeight;
    }

    private void OnOverviewPointerCanceled(object sender, PointerRoutedEventArgs args)
    {
        if (_overviewPress?.Pointer.PointerId == args.Pointer.PointerId) EndOverviewDrag();
    }

    /// <summary>Returns capture and layout to their resting state after a release or cancellation.</summary>
    private void EndOverviewDrag()
    {
        if (_overviewPress is not { } press) return;
        _overviewPress = null;
        press.Tab.Opacity = 1;
        Panel.ReleasePointerCapture(press.Pointer);
        if (_overviewDragging)
        {
            _overviewDragging = false;
            SetOverviewHover(-1);
            if (!_closed) ArrangeOverview();
        }
    }
}
