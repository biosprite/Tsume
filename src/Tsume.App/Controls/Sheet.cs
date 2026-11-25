using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Tsume.App.Theme;

namespace Tsume.App.Controls;

/// <summary>
/// A card that rises over the panel from the bottom, dimming what is behind it.
/// Tapping the dimmed area closes it.
/// </summary>
public sealed class Sheet : ContentControl
{
    /// <summary>How far below its resting place the card starts.</summary>
    private const double StartsBelow = 36;

    private readonly TranslateTransform _rise = new(0, StartsBelow);
    private FrameworkElement? _scrim;
    private FrameworkElement? _card;

    public Sheet()
    {
        Focusable = false;
        Visibility = Visibility.Collapsed;
    }

    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(
        nameof(IsOpen), typeof(bool), typeof(Sheet),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, e) => ((Sheet)d).Move((bool)e.NewValue)));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(Sheet), new PropertyMetadata(""));

    public bool IsOpen { get => (bool)GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }

    /// <summary>Small heading at the top of the card.</summary>
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _scrim = GetTemplateChild("PART_Scrim") as FrameworkElement;
        _card = GetTemplateChild("PART_Card") as FrameworkElement;

        if (_card is not null) _card.RenderTransform = _rise;
        if (_scrim is not null) _scrim.MouseLeftButtonDown += (_, _) => IsOpen = false;
    }

    /// <summary>Stops any transition and puts the sheet straight into its closed resting state.</summary>
    public void SnapClosed()
    {
        if (_scrim is not null) _scrim.Snap(OpacityProperty, 0);
        if (_card is not null) _card.Snap(OpacityProperty, 0);
        _rise.Snap(TranslateTransform.YProperty, StartsBelow);
        Visibility = Visibility.Collapsed;
    }

    private void Move(bool open)
    {
        if (open) Visibility = Visibility.Visible;

        ApplyTemplate();
        if (_scrim is null || _card is null) return;

        if (open)
        {
            _scrim.Animate(OpacityProperty, 1, Motion.Smooth, Motion.Settle);
            _card.Animate(OpacityProperty, 1, Motion.Quick, Motion.Settle);
            _rise.Animate(TranslateTransform.YProperty, 0, Motion.Arrive, Motion.Glide);

            // Focus the first row once the card has been laid out.
            Dispatcher.BeginInvoke(DispatcherPriority.Input,
                () => { if (IsOpen) _card.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)); });
        }
        else
        {
            _scrim.Animate(OpacityProperty, 0, Motion.Smooth, Motion.Depart);
            _card.Animate(OpacityProperty, 0, Motion.Smooth, Motion.Depart);
            _rise.Animate(TranslateTransform.YProperty, StartsBelow, Motion.Smooth, Motion.Depart,
                then: () => { if (!IsOpen) Visibility = Visibility.Collapsed; });
        }
    }
}
