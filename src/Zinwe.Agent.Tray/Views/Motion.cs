using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Zinwe.Agent.Tray.Views;

/// <summary>
/// The tray's small animations: short, no bounce, each one tells the member something changed. Nothing runs when
/// Windows has animations off (Settings &gt; Accessibility &gt; Visual effects) or in High Contrast: every helper just
/// leaves the element in its final state.
/// </summary>
internal static class Motion
{
    private static readonly IEasingFunction Out = new QuadraticEase { EasingMode = EasingMode.EaseOut };

    public static bool On => SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;

    private static DoubleAnimation Ease(double from, double to, int ms) =>
        new(from, to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Out, FillBehavior = FillBehavior.Stop };

    /// <summary>A new message arriving: fades in while it moves into place, optionally growing from its corner.</summary>
    public static void Arrive(FrameworkElement el, double fromX, double fromY, double fromScale = 1, int ms = 200)
    {
        if (!On)
        {
            return;
        }
        var move = new TranslateTransform();
        var grow = new ScaleTransform();
        var group = new TransformGroup();
        group.Children.Add(grow);
        group.Children.Add(move);
        el.RenderTransform = group;
        el.RenderTransformOrigin = new Point(1, 1);
        move.BeginAnimation(TranslateTransform.XProperty, Ease(fromX, 0, ms));
        move.BeginAnimation(TranslateTransform.YProperty, Ease(fromY, 0, ms));
        grow.BeginAnimation(ScaleTransform.ScaleXProperty, Ease(fromScale, 1, ms));
        grow.BeginAnimation(ScaleTransform.ScaleYProperty, Ease(fromScale, 1, ms));
        el.BeginAnimation(UIElement.OpacityProperty, Ease(0, 1, ms));
    }

    public static void FadeIn(UIElement el, int ms = 200)
    {
        if (On)
        {
            el.BeginAnimation(UIElement.OpacityProperty, Ease(0, 1, ms));
        }
    }

    /// <summary>"Sending…": breathes until the row is replaced.</summary>
    public static void Breathe(UIElement el)
    {
        if (On)
        {
            el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.5, 1, TimeSpan.FromMilliseconds(800))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
            });
        }
    }

    /// <summary>The send button waking up when there is something to send.</summary>
    public static void Pop(FrameworkElement el)
    {
        if (!On)
        {
            return;
        }
        var scale = new ScaleTransform();
        el.RenderTransform = scale;
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Ease(0.82, 1, 160));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, Ease(0.82, 1, 160));
    }

    /// <summary>The send arrow leaving upwards and coming back from below.</summary>
    public static void Fly(FrameworkElement el)
    {
        if (!On)
        {
            return;
        }
        var move = new TranslateTransform();
        el.RenderTransform = move;
        var y = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        y.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        y.KeyFrames.Add(new EasingDoubleKeyFrame(-16, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(135)), Out));
        y.KeyFrames.Add(new DiscreteDoubleKeyFrame(12, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140))));
        y.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300)), Out));
        var fade = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(135))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300))));
        move.BeginAnimation(TranslateTransform.YProperty, y);
        el.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>Folds a strip open or shut (the quick replies): height and opacity together.</summary>
    public static void Fold(FrameworkElement el, double height, double opacity)
    {
        if (!On || el.ActualHeight == height && el.Opacity == opacity)
        {
            el.BeginAnimation(FrameworkElement.HeightProperty, null);
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Height = height;
            el.Opacity = opacity;
            return;
        }
        var from = el.ActualHeight;
        var fromOpacity = el.Opacity;
        el.Height = height;
        el.Opacity = opacity;
        el.BeginAnimation(FrameworkElement.HeightProperty, Ease(from, height, 160));
        el.BeginAnimation(UIElement.OpacityProperty, Ease(fromOpacity, opacity, 120));
    }
}
