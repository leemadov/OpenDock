using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenDock
{
    // macOS 26's Liquid Glass knobs: press a switch or grab a slider and its knob swells into a clear capsule of glass
    // that magnifies the track under it and bends the page around the track in at its rim (the white bands along its
    // inside top and bottom), with a fine bright rim and a soft shadow; let go and it settles back into the plain knob.
    // Drawn over the control (an adorner) only while it's being touched, so it costs nothing the rest of the time.
    static class GlassKnobs
    {
        public static void Init()
        {
            EventManager.RegisterClassHandler(typeof(CheckBox), UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((s, e) => Swell(s as CheckBox, true)), true);
            EventManager.RegisterClassHandler(typeof(CheckBox), ToggleButton.CheckedEvent, new RoutedEventHandler((s, e) => Toggled(s as CheckBox)), true);
            EventManager.RegisterClassHandler(typeof(CheckBox), ToggleButton.UncheckedEvent, new RoutedEventHandler((s, e) => Toggled(s as CheckBox)), true);
            EventManager.RegisterClassHandler(typeof(CheckBox), UIElement.LostMouseCaptureEvent, new MouseEventHandler((s, e) => Settle(s as CheckBox, 260)), true);
            EventManager.RegisterClassHandler(typeof(Thumb), Thumb.DragStartedEvent, new DragStartedEventHandler((s, e) => SwellThumb(s as Thumb)), true);
            EventManager.RegisterClassHandler(typeof(Thumb), Thumb.DragCompletedEvent, new DragCompletedEventHandler((s, e) => SettleThumb(s as Thumb)), true);
        }

        static FrameworkElement SwitchKnob(CheckBox c)
        {
            if (c == null || c.Template == null) return null;
            return c.Template.FindName("Knob", c) as FrameworkElement;
        }

        static void Swell(CheckBox c, bool on)
        {
            var knob = SwitchKnob(c);
            if (knob == null) return;
            var g = KnobGlass.For(c, knob, 1.9, 1.5);
            if (g != null) g.Target = 1;
        }

        // toggled: stays glass while it travels, then settles
        static void Toggled(CheckBox c)
        {
            var knob = SwitchKnob(c);
            if (knob == null || !c.IsLoaded || !c.IsMouseOver) return;
            var g = KnobGlass.For(c, knob, 1.9, 1.5);
            if (g == null) return;
            g.Target = 1;
            g.SettleAfter(380);
        }

        static void Settle(CheckBox c, int ms)
        {
            var knob = SwitchKnob(c);
            if (knob == null) return;
            var g = KnobGlass.Existing(c);
            if (g != null) g.SettleAfter(ms);
        }

        static void SwellThumb(Thumb t)
        {
            var slider = t == null ? null : t.TemplatedParent as Slider;
            if (slider == null) { var tr = t == null ? null : VisualTreeHelper.GetParent(t) as Track; slider = tr == null ? null : tr.TemplatedParent as Slider; }
            if (slider == null) return;
            var g = KnobGlass.For(slider, t, 1.8, 1.3);
            if (g != null) g.Target = 1;
        }

        static void SettleThumb(Thumb t)
        {
            var tr = t == null ? null : VisualTreeHelper.GetParent(t) as Track;
            var slider = tr == null ? null : tr.TemplatedParent as Slider;
            var g = slider == null ? null : KnobGlass.Existing(slider);
            if (g != null) g.SettleAfter(120);
        }
    }

    class KnobGlass : Adorner
    {
        readonly FrameworkElement knob;
        readonly double growX, growY;
        double grow, knobOpacity;
        public double Target;
        DateTime settleAt = DateTime.MaxValue;
        bool running;
        ImageSource lens;
        Rect lensRect;

        static readonly DependencyProperty GlassProperty = DependencyProperty.RegisterAttached("KnobGlass", typeof(KnobGlass), typeof(KnobGlass));

        public static KnobGlass Existing(UIElement host) { return host == null ? null : host.GetValue(GlassProperty) as KnobGlass; }

        public static KnobGlass For(Control host, FrameworkElement knob, double gx, double gy)
        {
            var g = Existing(host);
            if (g != null) return g;
            var layer = AdornerLayer.GetAdornerLayer(host);
            if (layer == null) return null;
            g = new KnobGlass(host, knob, gx, gy);
            layer.Add(g);
            host.SetValue(GlassProperty, g);
            g.Start();
            return g;
        }

        KnobGlass(Control host, FrameworkElement knob, double gx, double gy) : base(host)
        {
            this.knob = knob; growX = gx; growY = gy;
            knobOpacity = knob.Opacity;
            IsHitTestVisible = false;
        }

        public void SettleAfter(int ms) { settleAt = DateTime.UtcNow.AddMilliseconds(ms); }

        void Start()
        {
            if (running) return;
            running = true;
            CompositionTarget.Rendering += Frame;
        }

        void Stop()
        {
            running = false;
            CompositionTarget.Rendering -= Frame;
            knob.Opacity = knobOpacity;
            var host = (UIElement)AdornedElement;
            host.ClearValue(GlassProperty);
            var layer = AdornerLayer.GetAdornerLayer(host);
            if (layer != null) layer.Remove(this);
        }

        void Frame(object sender, EventArgs e)
        {
            try
            {
                if (!AdornedElement.IsVisible) { Stop(); return; }
                if (DateTime.UtcNow >= settleAt) { Target = 0; settleAt = DateTime.MaxValue; }
                // eased toward the target: quick to swell, a touch softer to settle
                grow += (Target - grow) * (Target > grow ? 0.32 : 0.22);
                if (Target == 0 && grow < 0.02) { Stop(); return; }
                knob.Opacity = knobOpacity * Math.Max(0, 1 - grow * 1.6);   // the plain knob gives way to the glass
                Bend();
                InvalidateVisual();
            }
            catch (Exception ex) { App.Log(ex); Stop(); }
        }

        // The capsule: centred on the knob, grown by up to growX / growY.
        Rect Capsule()
        {
            var host = (UIElement)AdornedElement;
            var k = knob.TransformToAncestor(host).TransformBounds(new Rect(0, 0, knob.ActualWidth, knob.ActualHeight));
            double w = k.Width * (1 + (growX - 1) * grow), h = k.Height * (1 + (growY - 1) * grow);
            w = Math.Max(w, h);   // a capsule, never taller than wide
            return new Rect(k.X + k.Width / 2 - w / 2, k.Y + k.Height / 2 - h / 2, w, h);
        }

        // What's behind the capsule (the knob hidden), slightly magnified, through GlassDock's lens.
        void Bend()
        {
            var host = (FrameworkElement)AdornedElement;
            var win = Window.GetWindow(host);
            var root = win == null ? null : win.Content as Visual;
            if (root == null || grow < 0.05) { lens = null; return; }
            var r = Capsule();
            double pull = r.Height * 0.5, m = Math.Ceiling(pull) + 2;
            var area = new Rect(r.X - m, r.Y - m, r.Width + 2 * m, r.Height + 2 * m);
            // (positions measured from the window: a content's picture is drawn at its offset inside the window)
            var inRoot = host.TransformToAncestor(win).TransformBounds(area);
            var src = PresentationSource.FromVisual(host);
            double k = src == null ? 1 : src.CompositionTarget.TransformToDevice.M11;
            var rootEl = root as FrameworkElement;
            if (rootEl == null || rootEl.ActualWidth < 2) { lens = null; return; }
            // the window's content drawn as it is right now, with the knob hidden, then cropped to the area under the
            // capsule -- magnified a little around its centre (a lens is thicker in the middle)
            var off = rootEl.TransformToAncestor(win).Transform(new Point(0, 0));
            int rw = (int)Math.Ceiling((off.X + rootEl.ActualWidth) * k), rh = (int)Math.Ceiling((off.Y + rootEl.ActualHeight) * k);
            var rtb = new RenderTargetBitmap(rw, rh, 96 * k, 96 * k, PixelFormats.Pbgra32);
            double was = knob.Opacity;
            knob.Opacity = 0;
            var bgv = new DrawingVisual();
            using (var dc = bgv.RenderOpen()) dc.DrawRectangle((win.Background as SolidColorBrush) ?? Brushes.White, null, new Rect(0, 0, rw / k, rh / k));
            rtb.Render(bgv);
            rtb.Render(root);
            knob.Opacity = was;
            // the area under the capsule, sampled from that picture (anything beyond the window's edge takes the
            // nearest edge pixel), magnified a little around its centre -- a lens is thicker in the middle
            double zoom = 1 + 0.08 * grow;
            double vw = inRoot.Width / zoom, vh = inRoot.Height / zoom, vx = inRoot.X + (inRoot.Width - vw) / 2, vy = inRoot.Y + (inRoot.Height - vh) / 2;
            int pw = Math.Max(2, (int)Math.Ceiling(area.Width * k)), ph = Math.Max(2, (int)Math.Ceiling(area.Height * k));
            var all = new byte[rw * rh * 4]; rtb.CopyPixels(all, rw * 4, 0);
            var px = new byte[pw * ph * 4];
            for (int j = 0; j < ph; j++)
            {
                int sy = Math.Min(rh - 1, Math.Max(0, (int)((vy + (j + 0.5) * vh / ph) * k)));
                for (int i = 0; i < pw; i++)
                {
                    int sx = Math.Min(rw - 1, Math.Max(0, (int)((vx + (i + 0.5) * vw / pw) * k)));
                    int o = (sy * rw + sx) * 4, d = (j * pw + i) * 4;
                    px[d] = all[o]; px[d + 1] = all[o + 1]; px[d + 2] = all[o + 2]; px[d + 3] = 255;
                }
            }
            var lr = new Rect(m * k, m * k, r.Width * k, r.Height * k);
            // Lee's ray-traced glass (Lens3D): domed front, hollowed back, rounded rim, n = 1.5, studio-sky reflections
            lens = Lens3D.Render(px, pw, ph, lr, r.Height / 2 * k, 0.025, 0);
            lensRect = r;
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (grow < 0.02) return;
            var r = lens != null ? lensRect : Capsule();
            double rad = r.Height / 2;
            // a soft shadow under the glass
            for (int i = 6; i >= 1; i--)
            {
                var s = r; s.Inflate(i * 0.9, i * 0.9); s.Offset(0, 1.5 + i * 0.5);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb((byte)(7 * grow), 0, 0, 0)), null, s, rad + i, rad + i);
            }
            dc.PushOpacity(Math.Min(1, grow * 1.4));
            if (lens != null) dc.DrawImage(lens, r);
            else dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), null, r, rad, rad);
            dc.Pop();
        }
    }
}
