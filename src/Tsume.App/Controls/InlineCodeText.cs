using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Tsume.App.Theme;

namespace Tsume.App.Controls;

/// <summary>
/// Renders inline code/keycap pills (<c>`F12`</c>, <c>`#FF0000`</c>) and accent action badges
/// (<c>[[Capture Step]]</c>) inside a <see cref="TextBlock"/>.
/// </summary>
public static class InlineCodeText
{
    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas");
    private static readonly SolidColorBrush CodeFill = Freeze(Color.FromRgb(0x0B, 0x0E, 0x14));
    private static readonly SolidColorBrush CodeEdge = Freeze(Color.FromArgb(0x45, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush CodeInk = Freeze(Color.FromRgb(0xF4, 0xF6, 0xFA));

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached(
            "Text",
            typeof(string),
            typeof(InlineCodeText),
            new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject obj) => (string?)obj.GetValue(TextProperty);
    public static void SetText(DependencyObject obj, string? value) => obj.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block) return;

        block.Inlines.Clear();
        var text = e.NewValue as string;
        if (string.IsNullOrEmpty(text)) return;

        var i = 0;
        var plainStart = 0;
        var pillFontSize = Math.Max(10.5, block.FontSize - 1.5);

        while (i < text.Length)
        {
            // [[Action Button]] -> accent-tinted inline button badge
            if (i + 1 < text.Length && text[i] == '[' && text[i + 1] == '[')
            {
                var close = text.IndexOf("]]", i + 2, StringComparison.Ordinal);
                if (close > i + 2)
                {
                    FlushPlain(block, text, plainStart, i);
                    block.Inlines.Add(MakePill(text.Substring(i + 2, close - i - 2), pillFontSize, isAction: true));
                    i = close + 2;
                    plainStart = i;
                    continue;
                }
            }

            // `Code / Keycap` -> recessed dark monospace code label
            if (text[i] == '`')
            {
                var close = text.IndexOf('`', i + 1);
                if (close > i + 1)
                {
                    FlushPlain(block, text, plainStart, i);
                    block.Inlines.Add(MakePill(text.Substring(i + 1, close - i - 1), pillFontSize, isAction: false));
                    i = close + 1;
                    plainStart = i;
                    continue;
                }
            }

            i++;
        }

        FlushPlain(block, text, plainStart, text.Length);
    }

    private static void FlushPlain(TextBlock block, string text, int start, int end)
    {
        if (end <= start) return;

        var chunk = text.Substring(start, end - start).Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = chunk.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) block.Inlines.Add(new LineBreak());
            if (lines[i].Length > 0) block.Inlines.Add(new Run(lines[i]));
        }
    }

    private static InlineUIContainer MakePill(string label, double fontSize, bool isAction)
    {
        var border = new Border
        {
            Background = isAction ? Accent.Tint : CodeFill,
            BorderBrush = isAction ? Accent.Wash : CodeEdge,
            BorderThickness = isAction ? new Thickness(1) : new Thickness(1, 1, 1, 1.5),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 1.5, 6, 1.5),
            Margin = new Thickness(1, 0, 1, 0),
            SnapsToDevicePixels = true,
            Child = new TextBlock
            {
                Text = label,
                FontFamily = MonoFont,
                FontSize = fontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = isAction ? Accent.Brush : CodeInk,
                TextTrimming = TextTrimming.None,
                LineHeight = double.NaN,
                LineStackingStrategy = LineStackingStrategy.MaxHeight,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        return new InlineUIContainer(border)
        {
            BaselineAlignment = BaselineAlignment.Center,
        };
    }

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
