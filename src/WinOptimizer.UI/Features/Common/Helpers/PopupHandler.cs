using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinOptimizer.UI.Features.Common.Models;

namespace WinOptimizer.UI.Features.Common.Helpers;

/// <summary>Constructs application popups and owns their shared frames, theme, queue and anchor lifetime.</summary>
public static class PopupHandler
{
    private static readonly SemaphoreSlim ModalQueue = new(1, 1);
    private static readonly DependencyProperty RegistrationProperty = DependencyProperty.RegisterAttached(
        "Registration", typeof(object), typeof(PopupHandler), new PropertyMetadata(null));
    private static readonly DependencyProperty OwnerProperty = DependencyProperty.RegisterAttached(
        "Owner", typeof(object), typeof(PopupHandler), new PropertyMetadata(null));

    public static readonly DependencyProperty ContentProperty = DependencyProperty.RegisterAttached(
        "Content", typeof(FrameworkElement), typeof(PopupHandler), new PropertyMetadata(null, OnContentChanged));
    public static FrameworkElement? GetContent(DependencyObject element) => (FrameworkElement?)element.GetValue(ContentProperty);
    public static void SetContent(DependencyObject element, FrameworkElement? value) => element.SetValue(ContentProperty, value);

    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.RegisterAttached(
        "IsOpen", typeof(bool), typeof(PopupHandler), new PropertyMetadata(false,
            (element, _) => (element.GetValue(RegistrationProperty) as AnchorRegistration)?.SynchronizeOpenState()));
    public static bool GetIsOpen(DependencyObject element) => (bool)element.GetValue(IsOpenProperty);
    public static void SetIsOpen(DependencyObject element, bool value) => element.SetValue(IsOpenProperty, value);

    public static readonly DependencyProperty PreferredWidthProperty = DependencyProperty.RegisterAttached(
        "PreferredWidth", typeof(double), typeof(PopupHandler), new PropertyMetadata(double.NaN,
            (element, _) => (element.GetValue(RegistrationProperty) as AnchorRegistration)?.Resize()));
    public static double GetPreferredWidth(DependencyObject element) => (double)element.GetValue(PreferredWidthProperty);
    public static void SetPreferredWidth(DependencyObject element, double value)
    {
        if (!double.IsNaN(value) && (!double.IsFinite(value) || value < 0)) throw new ArgumentOutOfRangeException(nameof(value));
        element.SetValue(PreferredWidthProperty, value);
    }

    public static readonly DependencyProperty DismissOnClickProperty = DependencyProperty.RegisterAttached(
        "DismissOnClick", typeof(bool), typeof(PopupHandler), new PropertyMetadata(false, OnDismissOnClickChanged));
    public static bool GetDismissOnClick(DependencyObject element) => (bool)element.GetValue(DismissOnClickProperty);
    public static void SetDismissOnClick(DependencyObject element, bool value) => element.SetValue(DismissOnClickProperty, value);

    public static readonly DependencyProperty DismissAcceleratorProperty = DependencyProperty.RegisterAttached(
        "DismissAccelerator", typeof(KeyboardAccelerator), typeof(PopupHandler), new PropertyMetadata(null, OnDismissAcceleratorChanged));
    public static KeyboardAccelerator? GetDismissAccelerator(DependencyObject element) => (KeyboardAccelerator?)element.GetValue(DismissAcceleratorProperty);
    public static void SetDismissAccelerator(DependencyObject element, KeyboardAccelerator? value) => element.SetValue(DismissAcceleratorProperty, value);

    /// <summary>Preserves modal preparation order and guarantees release after display or validation errors.</summary>
    public static async Task<T> RunModalAsync<T>(Func<Task<T>> action)
    {
        await ModalQueue.WaitAsync();
        try { return await action(); }
        finally { ModalQueue.Release(); }
    }

    /// <summary>Builds the shared dialog frame around feature-owned content and actions.</summary>
    public static ContentDialog CreateDialog(PopupDialogOptions options) => new ContentDialog
    {
        Title = options.Title, Content = options.Content,
        PrimaryButtonText = options.PrimaryButtonText, SecondaryButtonText = options.SecondaryButtonText,
        CloseButtonText = options.CloseButtonText, DefaultButton = options.DefaultButton,
        IsPrimaryButtonEnabled = options.IsPrimaryButtonEnabled, MinWidth = options.MinWidth,
        Style = Resource<Style>("Popup.Dialog")
    };

    /// <summary>Applies the current host theme before feature-specific live updates begin.</summary>
    public static async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog, XamlRoot root,
        Action? beforeShow = null, Action? afterShow = null)
    {
        dialog.XamlRoot = root;
        dialog.RequestedTheme = (root.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default;
        var tint = dialog.RequestedTheme == ElementTheme.Dark
            ? Windows.UI.Color.FromArgb(255, 44, 44, 44) : Windows.UI.Color.FromArgb(255, 243, 243, 243);
        dialog.Background = new AcrylicBrush { TintColor = tint, TintOpacity = 0.65,
            TintLuminosityOpacity = 0.75, FallbackColor = tint };
        beforeShow?.Invoke();
        // WinUI reparents the native dialog body outside the ContentDialog's visual tree.
        var content = dialog.Content as FrameworkElement;
        content?.SetValue(OwnerProperty, dialog);
        try { return await dialog.ShowAsync(); }
        finally { content?.ClearValue(OwnerProperty); afterShow?.Invoke(); }
    }

    /// <summary>Transfers declared menu resources to the shared frame, retaining their bindings and actions.</summary>
    public static void AttachMenu(FrameworkElement anchor, IEnumerable<string> resourceKeys,
        FlyoutPlacementMode placement = FlyoutPlacementMode.BottomEdgeAlignedLeft)
    {
        RemoveRegistration(anchor);
        var menu = new MenuFlyout { Placement = placement };
        foreach (var key in resourceKeys)
        {
            var item = (MenuFlyoutItemBase)anchor.Resources[key];
            anchor.Resources.Remove(key);
            menu.Items.Add(item);
        }
        var registration = new AnchorRegistration(anchor, menu, null);
        anchor.SetValue(RegistrationProperty, registration);
        registration.SynchronizeOpenState();
    }

    public static ToolTip CreateToolTip(FrameworkElement anchor, object content)
    {
        var tooltip = new ToolTip { Content = content, Style = Resource<Style>("Popup.ToolTip") };
        tooltip.Opened += (_, _) => tooltip.RequestedTheme = anchor.ActualTheme;
        return tooltip;
    }

    public static void Hide(DependencyObject element)
    {
        for (DependencyObject? current = element; current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (current.GetValue(RegistrationProperty) is AnchorRegistration registration) { registration.Hide(); return; }
            if (current.GetValue(OwnerProperty) is FlyoutBase popup) { popup.Hide(); return; }
            if (current.GetValue(OwnerProperty) is ContentDialog owner) { owner.Hide(); return; }
            if (current is ContentDialog dialog) { dialog.Hide(); return; }
        }
        throw new InvalidOperationException("The control is not associated with an application popup.");
    }

    private static T Resource<T>(string key) => (T)Microsoft.UI.Xaml.Application.Current.Resources[key];

    private static void OnContentChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not FrameworkElement anchor) throw new ArgumentException("Popup content requires a button anchor.");
        RemoveRegistration(anchor);
        if (args.NewValue is FrameworkElement content)
        {
            var flyout = new Flyout { Content = content, Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
            var registration = new AnchorRegistration(anchor, flyout, content);
            anchor.SetValue(RegistrationProperty, registration);
            registration.SynchronizeOpenState();
        }
    }

    private static void RemoveRegistration(FrameworkElement anchor)
    {
        (anchor.GetValue(RegistrationProperty) as AnchorRegistration)?.Dispose();
        anchor.ClearValue(RegistrationProperty);
    }

    private static void OnDismissOnClickChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not Button button) throw new ArgumentException("Click dismissal requires a Button.");
        if ((bool)args.OldValue) button.Click -= Dismiss_Click;
        if ((bool)args.NewValue) button.Click += Dismiss_Click;
    }

    private static void Dismiss_Click(object sender, RoutedEventArgs args) => Hide((DependencyObject)sender);

    private static void OnDismissAcceleratorChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not UIElement target) throw new ArgumentException("Popup shortcuts require a UIElement.");
        if (args.OldValue is KeyboardAccelerator old) { old.Invoked -= DismissAccelerator_Invoked; target.KeyboardAccelerators.Remove(old); old.ClearValue(OwnerProperty); }
        if (args.NewValue is KeyboardAccelerator accelerator)
        {
            accelerator.SetValue(OwnerProperty, target);
            accelerator.Invoked += DismissAccelerator_Invoked;
            target.KeyboardAccelerators.Add(accelerator);
        }
    }

    private static void DismissAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    { args.Handled = true; Hide((DependencyObject)sender.GetValue(OwnerProperty)); }

    /// <summary>Scopes native flyout state, theme and sizing subscriptions to one actual placement target.</summary>
    private sealed class AnchorRegistration : IDisposable
    {
        private readonly FrameworkElement _anchor;
        private readonly FlyoutBase _popup;
        private readonly FrameworkElement? _content;
        private readonly double _maxWidth;
        private readonly double _maxHeight;
        private readonly double _width;
        private XamlRoot? _root;
        private bool _opening;

        public AnchorRegistration(FrameworkElement anchor, FlyoutBase popup, FrameworkElement? content)
        {
            _anchor = anchor; _popup = popup; _content = content;
            _maxWidth = content?.MaxWidth ?? double.PositiveInfinity;
            _maxHeight = content?.MaxHeight ?? double.PositiveInfinity;
            _width = content?.Width ?? double.NaN;
            if (content != null) content.SetValue(OwnerProperty, popup);
            SetNativeFlyout(popup);
            anchor.Loaded += Anchor_Loaded;
            anchor.Unloaded += Anchor_Unloaded;
            anchor.ActualThemeChanged += Anchor_ActualThemeChanged;
            popup.Opening += Opening;
            popup.Closed += Closed;
        }

        private void SetNativeFlyout(FlyoutBase? popup)
        {
            switch (_anchor)
            {
                case DropDownButton dropdown: dropdown.Flyout = popup; break;
                case Button button: button.Flyout = popup; break;
                default: throw new ArgumentException("Popup anchors must be Button or DropDownButton controls.");
            }
        }

        public void SynchronizeOpenState()
        {
            if (!GetIsOpen(_anchor)) { _popup.Hide(); return; }
            if (_opening || _popup.IsOpen || !_anchor.IsLoaded || _anchor.XamlRoot == null) return;
            Prepare();
            _popup.ShowAt(_anchor);
        }

        public void Hide() => _popup.Hide();
        private void Anchor_Loaded(object sender, RoutedEventArgs args) => SynchronizeOpenState();
        private void Anchor_Unloaded(object sender, RoutedEventArgs args) { _popup.Hide(); SetIsOpen(_anchor, false); DetachRoot(); }
        private void Anchor_ActualThemeChanged(FrameworkElement sender, object args) => ApplyTheme();

        private void Opening(object? sender, object args)
        {
            _opening = true;
            Prepare();
            SetIsOpen(_anchor, true);
            _root = _anchor.XamlRoot;
            if (_root != null) _root.Changed += Root_Changed;
        }

        private void Closed(object? sender, object args)
        { _opening = false; SetIsOpen(_anchor, false); DetachRoot(); }
        private void Root_Changed(XamlRoot sender, XamlRootChangedEventArgs args) { Resize(); ApplyTheme(); }

        private void Prepare()
        {
            if (_anchor.XamlRoot == null) throw new InvalidOperationException("Popup anchor is not attached to a window.");
            if (!_popup.IsOpen) _popup.XamlRoot = _anchor.XamlRoot;
            var style = new Style(_popup is MenuFlyout ? typeof(MenuFlyoutPresenter) : typeof(FlyoutPresenter))
            {
                BasedOn = Resource<Style>(_popup is MenuFlyout ? "Popup.MenuPresenter" : "Popup.FlyoutPresenter"),
                Setters = { new Setter(FrameworkElement.RequestedThemeProperty, _anchor.ActualTheme),
                    new Setter(FrameworkElement.MaxWidthProperty, AvailableSize(_anchor.XamlRoot).Width) }
            };
            if (_popup is MenuFlyout menu) menu.MenuFlyoutPresenterStyle = style;
            else if (_popup is Flyout flyout) flyout.FlyoutPresenterStyle = style;
            ApplyTheme(); Resize();
        }

        private void ApplyTheme()
        {
            if (_content == null)
            {
                if (_popup.IsOpen && _anchor.XamlRoot is { } root)
                    foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
                        SetMenuTheme(popup.Child, _anchor.ActualTheme);
                return;
            }
            _content.RequestedTheme = _anchor.ActualTheme;
            for (DependencyObject? current = _content; current != null; current = VisualTreeHelper.GetParent(current))
                if (current is FlyoutPresenter presenter) { presenter.RequestedTheme = _anchor.ActualTheme; break; }
        }

        private static void SetMenuTheme(DependencyObject element, ElementTheme theme)
        {
            if (element is MenuFlyoutPresenter presenter) presenter.RequestedTheme = theme;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
                SetMenuTheme(VisualTreeHelper.GetChild(element, index), theme);
        }

        public void Resize()
        {
            if (_content == null || _anchor.XamlRoot is not { } root) return;
            var padding = Resource<Thickness>("Popup.ContentPadding");
            var available = AvailableSize(root);
            _content.MaxWidth = Math.Min(_maxWidth, Math.Max(0, available.Width - padding.Left - padding.Right));
            _content.MaxHeight = Math.Min(_maxHeight, Math.Max(0, available.Height - padding.Top - padding.Bottom));
            var preferred = GetPreferredWidth(_anchor);
            _content.Width = double.IsNaN(preferred) ? _width : Math.Min(preferred, _content.MaxWidth);
        }

        private static Windows.Foundation.Size AvailableSize(XamlRoot root)
        {
            var margin = Resource<Thickness>("Popup.ViewportMargin");
            return new(Math.Max(0, Math.Min(Resource<double>("Popup.MaxWidth"), root.Size.Width - margin.Left - margin.Right)),
                Math.Max(0, root.Size.Height - margin.Top - margin.Bottom));
        }

        private void DetachRoot()
        { if (_root != null) _root.Changed -= Root_Changed; _root = null; }

        public void Dispose()
        {
            _popup.Hide(); SetIsOpen(_anchor, false); DetachRoot();
            _anchor.Loaded -= Anchor_Loaded; _anchor.Unloaded -= Anchor_Unloaded;
            _anchor.ActualThemeChanged -= Anchor_ActualThemeChanged;
            _popup.Opening -= Opening; _popup.Closed -= Closed;
            if (_content != null) _content.ClearValue(OwnerProperty);
            if (_popup is Flyout flyout) flyout.Content = null;
            if (_popup is MenuFlyout menu) menu.Items.Clear();
            SetNativeFlyout(null);
        }
    }
}
