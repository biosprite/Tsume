using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Tsume.App.Theme;

namespace Tsume.App.Controls;

/// <summary>
/// A second screen that covers the whole panel, gliding in from the right.
/// (A <see cref="Sheet"/> only covers the bottom.)
/// </summary>
public sealed class Screen : ContentControl
{
    /// <summary>How far to the right of its resting place the screen starts.</summary>
    public const double StartsAside = 32;

    private readonly TranslateTransform _glide = new(StartsAside, 0);

    public Screen()
    {
        Focusable = false;
        Visibility = Visibility.Collapsed;
        Opacity = 0;
        RenderTransform = _glide;
    }

    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(
        nameof(IsOpen), typeof(bool), typeof(Screen),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, e) => ((Screen)d).Move((bool)e.NewValue)));

    public bool IsOpen { get => (bool)GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }

    /// <summary>
    /// Marks the control that takes focus when the screen opens; otherwise the first
    /// control in reading order would, which is usually the back button.
    /// </summary>
    public static readonly DependencyProperty TakesFocusProperty = DependencyProperty.RegisterAttached(
        "TakesFocus", typeof(bool), typeof(Screen), new PropertyMetadata(false));

    public static bool GetTakesFocus(DependencyObject element) => (bool)element.GetValue(TakesFocusProperty);
    public static void SetTakesFocus(DependencyObject element, bool value) => element.SetValue(TakesFocusProperty, value);

    /// <summary>
    /// True while another screen lies on top of this one: it slides left, fades,
    /// and is disabled until the screen above closes.
    /// </summary>
    public bool IsCovered
    {
        get;
        set
        {
            if (field == value) return;
            field = value;

            IsEnabled = !value;
            if (!IsOpen) return;

            this.Animate(OpacityProperty, value ? 0 : 1, value ? Motion.Quick : Motion.Smooth, value ? Motion.Depart : Motion.Glide);
            _glide.Animate(TranslateTransform.XProperty, value ? -StartsAside : 0,
                value ? Motion.Smooth : Motion.Arrive, value ? Motion.Depart : Motion.Glide);
        }
    }

    private void Move(bool open)
    {
        if (open)
        {
            Visibility = Visibility.Visible;

            this.Animate(OpacityProperty, IsCovered ? 0 : 1, Motion.Smooth, Motion.Settle);
            _glide.Animate(TranslateTransform.XProperty, IsCovered ? -StartsAside : 0, Motion.Arrive, Motion.Glide);

            // Focus once the screen has been laid out.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, FocusWhereAsked);
        }
        else
        {
            this.Animate(OpacityProperty, 0, Motion.Quick, Motion.Depart);
            _glide.Animate(TranslateTransform.XProperty, StartsAside, Motion.Smooth, Motion.Depart,
                then: () => { if (!IsOpen) Visibility = Visibility.Collapsed; });
        }
    }

    private void FocusWhereAsked()
    {
        if (!IsOpen || IsCovered) return;

        var first = new TraversalRequest(FocusNavigationDirection.First);

        // The mark may sit on a list: then its first row takes focus, or, if the list
        // is empty, whatever comes first on the screen.
        var taken = Marked(this) switch
        {
            null                      => false,
            { Focusable: true } chosen => chosen.Focus(),
            var list                  => list.MoveFocus(first),
        };

        if (!taken) MoveFocus(first);
    }

    /// <summary>
    /// Call after a screen's content changes (a list refilled, a button gone): if the focused
    /// control is no longer there, focus returns to where the screen first puts it.
    /// </summary>
    /// <param name="within">Anything on the screen in question.</param>
    /// <param name="unlessIn">
    /// Focus is left alone only if it is inside this. Use for a list that fills after its screen opened.
    /// </param>
    public static void KeepFocus(DependencyObject within, Visual? unlessIn = null) => Application.Current.Dispatcher.BeginInvoke(
        DispatcherPriority.Input, () =>
        {
            for (var at = within; at is not null; at = LogicalTreeHelper.GetParent(at) ?? VisualTreeHelper.GetParent(at))
            {
                if (at is not Screen screen) continue;

                var held = Keyboard.FocusedElement is UIElement { IsVisible: true, IsEnabled: true } now
                        && (unlessIn ?? screen).IsAncestorOf(now);

                if (!held) screen.FocusWhereAsked();
                return;
            }
        });

    /// <summary>The first control under <paramref name="parent"/> marked with <see cref="TakesFocusProperty"/>.</summary>
    private static UIElement? Marked(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is UIElement element && GetTakesFocus(element)) return element;
            if (Marked(child) is { } deeper) return deeper;
        }

        return null;
    }
}
