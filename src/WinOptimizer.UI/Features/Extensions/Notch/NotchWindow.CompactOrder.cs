using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace WinOptimizer.UI.Features.Extensions.Notch;

public sealed partial class NotchWindow
{
    private readonly Dictionary<NotchModule, FrameworkElement> _compactSlots;
    private readonly DispatcherQueueTimer _compactHold;
    private Pointer? _compactPointer;
    private NotchCompactDrag? _compactDrag;

    /// <summary>Moves existing controls so live readouts retain their state while the order changes.</summary>
    private void ApplyCompactOrder(IReadOnlyList<NotchModule> order)
    {
        var children = CompactReadouts.Children;
        for (int index = 0; index < order.Count; index++)
        {
            var item = _compactSlots[order[index]];
            // The handle stays first when the bar has no readouts.
            if (children[index + 1] == item) continue;
            children.Remove(item);
            children.Insert(index + 1, item);
        }
    }

    private void OnCompactPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (_compactPointer is not null || _expanded || !_extension.CanEdit) return;
        var point = args.GetCurrentPoint(CompactReadouts);
        if (args.Pointer.PointerDeviceType == PointerDeviceType.Mouse && !point.Properties.IsLeftButtonPressed) return;

        var items = _extension.CompactOrder.Where(slot => _compactSlots[slot].Visibility == Visibility.Visible).ToArray();
        if (items.Length < 2) return;
        var source = items.FirstOrDefault(slot => IsWithin(args.OriginalSource, _compactSlots[slot]));
        if (source == NotchModule.None || !CompactReadouts.CapturePointer(args.Pointer)) return;

        _compactPointer = args.Pointer;
        var targets = items.Select(slot =>
        {
            var item = _compactSlots[slot];
            var center = item.TransformToVisual(CompactReadouts).TransformPoint(new Point(item.ActualWidth / 2, 0));
            return (slot, center.X);
        }).ToArray();
        _compactDrag = new(source, point.Position, targets);
        _compactHold.Start();
        args.Handled = true;
    }

    private void OnCompactHold(DispatcherQueueTimer sender, object args)
    {
        if (_compactDrag is { } drag && drag.Hold()) _compactSlots[drag.Source].Opacity = 0.55;
    }

    private void OnCompactPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_compactPointer?.PointerId != args.Pointer.PointerId || _compactDrag is not { } drag) return;
        args.Handled = true;
        if (drag.Move(args.GetCurrentPoint(CompactReadouts).Position))
            ApplyCompactOrder(NotchCompactOrder.Move(_extension.CompactOrder, drag.Source, drag.Target) ?? _extension.CompactOrder);
        if (!drag.CanClick) _compactHold.Stop();
    }

    private async void OnCompactPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_compactPointer?.PointerId != args.Pointer.PointerId || _compactDrag is not { } drag) return;
        args.Handled = true;
        drag.Move(args.GetCurrentPoint(CompactReadouts).Position);
        var point = args.GetCurrentPoint(Grip).Position;
        bool onBar = point.Y >= 0 && point.Y <= Grip.ActualHeight;
        bool drop = drag.IsDragging && drag.Target != drag.Source && onBar &&
            _compactSlots[drag.Source].Visibility == Visibility.Visible &&
            _compactSlots[drag.Target].Visibility == Visibility.Visible;
        EndCompactDrag(restoreOrder: !drop);

        if (drop)
        {
            await _extension.MoveCompactSlotAsync(drag.Source, drag.Target);
            if (!_closed) ApplyCompactOrder(_extension.CompactOrder);
        }
        else if (drag.CanClick && onBar && point.X >= 0 && point.X <= Grip.ActualWidth)
            SetExpanded(true);
    }

    private void OnCompactPointerCanceled(object sender, PointerRoutedEventArgs args)
    {
        if (_compactPointer?.PointerId == args.Pointer.PointerId) EndCompactDrag();
    }

    /// <summary>Releases capture and restores confirmed order on cancellation.</summary>
    private void EndCompactDrag(bool restoreOrder = true)
    {
        if (_compactPointer is not { } pointer || _compactDrag is not { } drag) return;
        _compactPointer = null;
        _compactDrag = null;
        _compactHold.Stop();
        _compactSlots[drag.Source].Opacity = 1;
        CompactReadouts.ReleasePointerCapture(pointer);
        if (restoreOrder && drag.IsDragging) ApplyCompactOrder(_extension.CompactOrder);
    }
}
