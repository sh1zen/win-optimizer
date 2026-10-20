using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace WinOptimizer.UI.Features.Common.Helpers;

/// <summary>Hides a scrolling control's own scrollbars after the configured idle delay.</summary>
public static class ScrollBarAutoHide
{
    public static readonly DependencyProperty HideAfterProperty = DependencyProperty.RegisterAttached(
        "HideAfter", typeof(TimeSpan), typeof(ScrollBarAutoHide),
        new PropertyMetadata(TimeSpan.Zero, OnHideAfterChanged));

    private static readonly DependencyProperty ControllerProperty = DependencyProperty.RegisterAttached(
        "Controller", typeof(object), typeof(ScrollBarAutoHide), new PropertyMetadata(null));

    public static TimeSpan GetHideAfter(DependencyObject element) => (TimeSpan)element.GetValue(HideAfterProperty);
    public static void SetHideAfter(DependencyObject element, TimeSpan value) => element.SetValue(HideAfterProperty, value);

    private static void OnHideAfterChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not Control host || host is not (ScrollViewer or ListViewBase))
            throw new ArgumentException("Scrollbar auto-hide requires a ScrollViewer or ListViewBase.", nameof(element));
        var delay = (TimeSpan)args.NewValue;
        if (delay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(args), "The scrollbar idle delay cannot be negative.");

        (host.GetValue(ControllerProperty) as Controller)?.Dispose();
        host.ClearValue(ControllerProperty);
        if (delay > TimeSpan.Zero)
            host.SetValue(ControllerProperty, new Controller(host, delay));
    }

    /// <summary>Owns input subscriptions, the idle timer and template scrollbar state for one host.</summary>
    private sealed class Controller : IDisposable
    {
        private readonly Control _host;
        private readonly DispatcherTimer _timer;
        private readonly Dictionary<ScrollBar, double> _scrollBars = new();
        private readonly HashSet<uint> _pressedPointers = new();
        private ScrollViewer? _viewer;

        public Controller(Control host, TimeSpan delay)
        {
            _host = host;
            _timer = new DispatcherTimer { Interval = delay };
            _timer.Tick += OnIdle;
            _host.Loaded += OnLoaded;
            _host.Unloaded += OnUnloaded;
            if (_host.IsLoaded)
                Attach();
        }

        private void OnLoaded(object sender, RoutedEventArgs args) => Attach();
        private void OnUnloaded(object sender, RoutedEventArgs args) => Detach();

        private void Attach()
        {
            if (_viewer != null) return;
            _host.ApplyTemplate();
            _viewer = FindViewer(_host)
                ?? throw new InvalidOperationException("The scrolling control template does not contain a ScrollViewer.");
            _viewer.ApplyTemplate();
            CollectScrollBars(_viewer);
            _viewer.ViewChanged += OnViewChanged;
            _host.AddHandler(UIElement.PointerEnteredEvent, new PointerEventHandler(OnPointerActivity), true);
            _host.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnPointerActivity), true);
            _host.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnPointerActivity), true);
            _host.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPointerPressed), true);
            _host.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnPointerReleased), true);
            _host.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnPointerReleased), true);
            _host.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnPointerReleased), true);
            _host.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), true);
            _host.GotFocus += OnFocus;
            ShowForActivity();
        }

        private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs args) => ShowForActivity();
        private void OnPointerActivity(object sender, PointerRoutedEventArgs args) => ShowForActivity();
        private void OnKeyDown(object sender, KeyRoutedEventArgs args) => ShowForActivity();
        private void OnFocus(object sender, RoutedEventArgs args) => ShowForActivity();

        private void OnPointerPressed(object sender, PointerRoutedEventArgs args)
        {
            // Scrollbar thumbs capture their pointer through release/capture loss.
            // Content presses can end outside the host, so they must not suspend hiding.
            for (var current = args.OriginalSource as DependencyObject; current != null && current != _host;
                 current = VisualTreeHelper.GetParent(current))
            {
                if (current is not ScrollBar bar || !_scrollBars.ContainsKey(bar)) continue;
                _pressedPointers.Add(args.Pointer.PointerId);
                break;
            }
            ShowForActivity();
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs args)
        {
            _pressedPointers.Remove(args.Pointer.PointerId);
            ShowForActivity();
        }

        /// <summary>Restarts the full idle interval after input or scrolling, including inertial scrolling.</summary>
        private void ShowForActivity()
        {
            if (_viewer == null) return;
            _timer.Stop();
            foreach (var (bar, opacity) in _scrollBars)
                bar.Opacity = opacity;
            if (_pressedPointers.Count == 0)
                _timer.Start();
        }

        private void OnIdle(object? sender, object args)
        {
            _timer.Stop();
            if (_pressedPointers.Count > 0) return;
            foreach (var bar in _scrollBars.Keys)
                bar.Opacity = 0;
        }

        private static ScrollViewer? FindViewer(DependencyObject element)
        {
            if (element is ScrollViewer viewer) return viewer;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
                if (FindViewer(VisualTreeHelper.GetChild(element, i)) is { } childViewer)
                    return childViewer;
            return null;
        }

        /// <summary>Visits template chrome only, leaving nested scrolling content independent.</summary>
        private void CollectScrollBars(DependencyObject element)
        {
            if (element is ScrollContentPresenter) return;
            if (element is ScrollBar bar)
            {
                _scrollBars.Add(bar, bar.Opacity);
                return;
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
                CollectScrollBars(VisualTreeHelper.GetChild(element, i));
        }

        private void Detach()
        {
            _timer.Stop();
            if (_viewer == null) return;
            _viewer.ViewChanged -= OnViewChanged;
            _host.RemoveHandler(UIElement.PointerEnteredEvent, new PointerEventHandler(OnPointerActivity));
            _host.RemoveHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnPointerActivity));
            _host.RemoveHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnPointerActivity));
            _host.RemoveHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPointerPressed));
            _host.RemoveHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnPointerReleased));
            _host.RemoveHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnPointerReleased));
            _host.RemoveHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnPointerReleased));
            _host.RemoveHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown));
            _host.GotFocus -= OnFocus;
            foreach (var (bar, opacity) in _scrollBars)
                bar.Opacity = opacity;
            _scrollBars.Clear();
            _pressedPointers.Clear();
            _viewer = null;
        }

        public void Dispose()
        {
            Detach();
            _timer.Tick -= OnIdle;
            _host.Loaded -= OnLoaded;
            _host.Unloaded -= OnUnloaded;
        }
    }
}
