using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace OpenDock
{
    // One way for things to come and go, everywhere: a card springs up from slightly smaller as it fades in (the
    // library's spring: quick, one soft overshoot), and shrinks a touch as it fades out. `origin` is where it grows
    // from, as a fraction of its size (0.5, 0 = from the top middle, under a menu; 0.5, 1 = from the bottom, over the Dock).
    static class Motion
    {
        public static void Appear(UIElement e, Point origin, double from = 0.9)
        {
            if (e == null) return;
            var s = new ScaleTransform(from, from);
            e.RenderTransformOrigin = origin;
            e.RenderTransform = s;
            var spring = new Spring();
            var grow = new DoubleAnimation(from, 1, TimeSpan.FromSeconds(spring.Seconds)) { EasingFunction = spring };
            s.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            s.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)));
        }

        public static void Disappear(UIElement e, Action done, double to = 0.95)
        {
            if (e == null) { if (done != null) done(); return; }
            var s = e.RenderTransform as ScaleTransform;
            if (s == null || s.IsFrozen) { s = new ScaleTransform(1, 1); e.RenderTransform = s; }
            var t = TimeSpan.FromMilliseconds(140);
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            s.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, t) { EasingFunction = ease });
            s.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, t) { EasingFunction = ease });
            var fade = new DoubleAnimation(0, t);
            if (done != null) fade.Completed += delegate { done(); };
            e.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }
}
