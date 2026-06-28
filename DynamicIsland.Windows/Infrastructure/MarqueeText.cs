using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
// This project pulls System.Drawing into scope (WinForms interop), so disambiguate the WPF types.
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using Size = System.Windows.Size;

namespace DynamicIsland.Windows.Infrastructure;

/// <summary>
/// A single-line text element that gently cycles (marquees) its content left-and-back when the text is
/// wider than the space available — so a long song title can still be read fully on the shrunken island.
/// When <see cref="Active"/> is false, or the text fits, it behaves like a normal ellipsis-trimmed label.
/// Built as a <see cref="Decorator"/> hosting one <see cref="TextBlock"/> so it needs no control template.
/// </summary>
public sealed class MarqueeText : Decorator
{
    private readonly TextBlock _text;
    private readonly TranslateTransform _offset = new();
    private bool _animating;

    public MarqueeText()
    {
        ClipToBounds = true;
        _text = new TextBlock
        {
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = _offset,
        };
        Child = _text;
    }

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(MarqueeText),
        new PropertyMetadata(string.Empty, (d, e) => { var m = (MarqueeText)d; m._text.Text = (string)e.NewValue; m.Refresh(); }));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    public static readonly DependencyProperty ActiveProperty = DependencyProperty.Register(
        nameof(Active), typeof(bool), typeof(MarqueeText),
        new PropertyMetadata(true, (d, _) => ((MarqueeText)d).Refresh()));
    public bool Active { get => (bool)GetValue(ActiveProperty); set => SetValue(ActiveProperty, value); }

    public static readonly DependencyProperty TextForegroundProperty = DependencyProperty.Register(
        nameof(TextForeground), typeof(Brush), typeof(MarqueeText),
        new PropertyMetadata(Brushes.White, (d, e) => ((MarqueeText)d)._text.Foreground = (Brush)e.NewValue));
    public Brush TextForeground { get => (Brush)GetValue(TextForegroundProperty); set => SetValue(TextForegroundProperty, value); }

    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(
        nameof(FontSize), typeof(double), typeof(MarqueeText),
        new PropertyMetadata(13d, (d, e) => { ((MarqueeText)d)._text.FontSize = (double)e.NewValue; ((MarqueeText)d).Refresh(); }));
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }

    public static readonly DependencyProperty FontFamilyProperty = DependencyProperty.Register(
        nameof(FontFamily), typeof(FontFamily), typeof(MarqueeText),
        new PropertyMetadata(System.Windows.SystemFonts.MessageFontFamily, (d, e) => { ((MarqueeText)d)._text.FontFamily = (FontFamily)e.NewValue; ((MarqueeText)d).Refresh(); }));
    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }

    public static readonly DependencyProperty FontWeightProperty = DependencyProperty.Register(
        nameof(FontWeight), typeof(FontWeight), typeof(MarqueeText),
        new PropertyMetadata(FontWeights.Normal, (d, e) => { ((MarqueeText)d)._text.FontWeight = (FontWeight)e.NewValue; ((MarqueeText)d).Refresh(); }));
    public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }

    private bool WillScroll(double available) => Active && available > 0 && _text.DesiredSize.Width - available > 1;

    protected override Size MeasureOverride(Size constraint)
    {
        _text.Measure(new Size(double.PositiveInfinity, constraint.Height));
        var natural = _text.DesiredSize;
        var width = double.IsInfinity(constraint.Width) ? natural.Width : Math.Min(natural.Width, constraint.Width);
        return new Size(width, natural.Height);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var scroll = WillScroll(arrangeSize.Width);
        _text.TextTrimming = scroll ? TextTrimming.None : TextTrimming.CharacterEllipsis;
        // When scrolling, lay the text out at its full natural width (overflow is clipped, then revealed by
        // the animation); otherwise constrain it so the ellipsis can appear.
        var w = scroll ? Math.Max(_text.DesiredSize.Width, arrangeSize.Width) : arrangeSize.Width;
        _text.Arrange(new Rect(0, 0, w, arrangeSize.Height));
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Refresh));
        return arrangeSize;
    }

    private void Refresh()
    {
        var available = ActualWidth;
        if (WillScroll(available))
        {
            var distance = _text.DesiredSize.Width - available + 26; // overflow + trailing gap
            const double speed = 34.0;                                // px/sec — calm, never distracting
            var scrollDur = TimeSpan.FromSeconds(Math.Max(0.4, distance / speed));
            var pause = TimeSpan.FromSeconds(1.5);

            var anim = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
            var t = TimeSpan.Zero;
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(t)));
            t += pause; anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(t)));
            t += scrollDur; anim.KeyFrames.Add(new EasingDoubleKeyFrame(-distance, KeyTime.FromTimeSpan(t), ease));
            t += pause; anim.KeyFrames.Add(new LinearDoubleKeyFrame(-distance, KeyTime.FromTimeSpan(t)));
            t += scrollDur; anim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(t), ease));
            _offset.BeginAnimation(TranslateTransform.XProperty, anim);
            _animating = true;
        }
        else if (_animating || _offset.X != 0)
        {
            _offset.BeginAnimation(TranslateTransform.XProperty, null);
            _offset.X = 0;
            _animating = false;
        }
    }
}
