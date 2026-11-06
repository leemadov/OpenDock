using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace OpenDock
{
    // Liquid Glass, ported from Kyant's AndroidLiquidGlass ("backdrop", Apache-2.0): the same signed-distance
    // rounded rectangle, the same circular lens profile and the same seven-sample dispersion, done on the CPU
    // from a capture of what's behind the glass.
    //
    //  - Vibrancy: what's behind is 1.5x more saturated.
    //  - Lens: within `height` of the edge, each pixel looks inward along the edge's normal by
    //    amount * (1 - sqrt(1 - x^2)), x = 1 - depth/height -- nothing in the middle, a strong bend at the rim,
    //    like the curved edge of a thick lens. Normals are taken on a rounder shape (1.5x the corner radius) so
    //    corners bend smoothly; `depth` adds a pull toward the centre.
    //  - Dispersion: seven samples spread along the bend (red, orange, yellow, green, cyan, blue, purple),
    //    strongest at the corners, mixed back into RGB with the library's weights.
    static class Liquid
    {
        // `src` is a Bgr32 capture (sw x sh); `r`, `radius`, `height` and `amount` are in its pixels.
        public static BitmapSource Lens(byte[] src, int sw, int sh, Rect r, double radius, double height, double amount, bool dispersion, double depth = 0, double saturation = 1.5)
        {
            int w = Math.Max(1, (int)Math.Round(r.Width)), h = Math.Max(1, (int)Math.Round(r.Height));
            var dst = new byte[w * h * 4];
            double hx = r.Width / 2, hy = r.Height / 2, cx = r.X + hx, cy = r.Y + hy;
            radius = Math.Max(0, Math.Min(radius, Math.Min(hx, hy)));
            double gradRadius = Math.Min(radius * 1.5, Math.Min(hx, hy));
            height = Math.Max(0.5, height);
            System.Threading.Tasks.Parallel.For(0, h, j =>
            {
                var c = new double[3];
                for (int i = 0; i < w; i++)
                {
                    double px = r.X + i + 0.5, py = r.Y + j + 0.5, ux = px - cx, uy = py - cy;
                    double sd = SdRoundRect(ux, uy, hx, hy, radius);
                    double sx = px, sy = py, dx = 0, dy = 0;
                    bool bent = -sd < height;
                    if (bent)
                    {
                        sd = Math.Min(sd, 0);
                        double x = 1 + sd / height;                       // 1 at the rim, 0 where the bend ends
                        // Real glass: the curved rim bends light from just *outside* the glass into view, so what's behind
                        // the edge wraps around it (Apple's look). The library's circular profile gives the sharp pull at
                        // the very rim; a gentler x^2 term carries the bend further in, as on Apple's thick glass.
                        double d = (0.6 * (1 - Math.Sqrt(Math.Max(0, 1 - x * x))) + 0.4 * x * x) * amount;
                        double gx, gy;
                        GradRoundRect(ux, uy, hx, hy, gradRadius, out gx, out gy);
                        if (depth != 0) { double l = Math.Sqrt(ux * ux + uy * uy); if (l > 1e-6) { gx += depth * ux / l; gy += depth * uy / l; } }
                        double gl = Math.Sqrt(gx * gx + gy * gy);
                        if (gl > 1e-6) { gx /= gl; gy /= gl; }
                        sx = px + d * gx; sy = py + d * gy;
                        if (dispersion)
                        {
                            double k = (ux * uy) / (hx * hy);                // strongest toward the corners
                            dx = d * gx * k; dy = d * gy * k;
                        }
                    }
                    if (bent && dispersion && (dx != 0 || dy != 0))
                    {
                        // red, orange, yellow, green, cyan, blue, purple -- weights from the library
                        double rr = 0, gg = 0, bb = 0;
                        rr += S(src, sw, sh, sx + dx, sy + dy, 2) / 3.5;
                        rr += S(src, sw, sh, sx + dx * 2 / 3, sy + dy * 2 / 3, 2) / 3.5; gg += S(src, sw, sh, sx + dx * 2 / 3, sy + dy * 2 / 3, 1) / 7.0;
                        rr += S(src, sw, sh, sx + dx / 3, sy + dy / 3, 2) / 3.5; gg += S(src, sw, sh, sx + dx / 3, sy + dy / 3, 1) / 3.5;
                        gg += S(src, sw, sh, sx, sy, 1) / 3.5;
                        gg += S(src, sw, sh, sx - dx / 3, sy - dy / 3, 1) / 3.5; bb += S(src, sw, sh, sx - dx / 3, sy - dy / 3, 0) / 3.0;
                        bb += S(src, sw, sh, sx - dx * 2 / 3, sy - dy * 2 / 3, 0) / 3.0;
                        rr += S(src, sw, sh, sx - dx, sy - dy, 2) / 7.0; bb += S(src, sw, sh, sx - dx, sy - dy, 0) / 3.0;
                        c[0] = bb; c[1] = gg; c[2] = rr;
                    }
                    else { c[0] = S(src, sw, sh, sx, sy, 0); c[1] = S(src, sw, sh, sx, sy, 1); c[2] = S(src, sw, sh, sx, sy, 2); }
                    // vibrancy: the library's colour matrix, luminance weights 0.213 / 0.715 / 0.072
                    double lum = 0.213 * c[2] + 0.715 * c[1] + 0.072 * c[0];
                    int o = (j * w + i) * 4;
                    dst[o] = Clamp(lum + (c[0] - lum) * saturation);
                    dst[o + 1] = Clamp(lum + (c[1] - lum) * saturation);
                    dst[o + 2] = Clamp(lum + (c[2] - lum) * saturation);
                    dst[o + 3] = 255;
                }
            });
            var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, dst, w * 4);
            b.Freeze();
            return b;
        }

        // Signed distance to a rounded rectangle centred on the origin (negative inside).
        public static double SdRoundRect(double x, double y, double hx, double hy, double radius)
        {
            double qx = Math.Abs(x) - (hx - radius), qy = Math.Abs(y) - (hy - radius);
            double ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
            return Math.Sqrt(ox * ox + oy * oy) - radius + Math.Min(Math.Max(qx, qy), 0);
        }

        // Its outward normal (the library's gradSdRoundedRect).
        public static void GradRoundRect(double x, double y, double hx, double hy, double radius, out double gx, out double gy)
        {
            double qx = Math.Abs(x) - (hx - radius), qy = Math.Abs(y) - (hy - radius);
            if (qx >= 0 || qy >= 0)
            {
                double mx = Math.Max(qx, 0), my = Math.Max(qy, 0), l = Math.Sqrt(mx * mx + my * my);
                if (l < 1e-9) { gx = 0; gy = 0; return; }
                gx = Math.Sign(x) * mx / l; gy = Math.Sign(y) * my / l;
            }
            else
            {
                double sx = qx >= qy ? 1 : 0;
                gx = Math.Sign(x) * sx; gy = Math.Sign(y) * (1 - sx);
            }
        }

        // Beyond the captured picture the glass sees it mirrored, not the edge pixel smeared into streaks.
        static double Mirror(double v, int n)
        {
            if (n <= 1) return 0.5;
            double period = 2.0 * n;
            v = v % period; if (v < 0) v += period;
            return v < n ? v : period - v;
        }

        static double S(byte[] s, int w, int h, double x, double y, int c)
        {
            if (x < 0 || x > w) x = Mirror(x, w);
            if (y < 0 || y > h) y = Mirror(y, h);
            x -= 0.5; y -= 0.5;
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
            double fx = x - x0, fy = y - y0;
            int x1 = Math.Max(0, Math.Min(w - 1, x0 + 1)), y1 = Math.Max(0, Math.Min(h - 1, y0 + 1));
            x0 = Math.Max(0, Math.Min(w - 1, x0)); y0 = Math.Max(0, Math.Min(h - 1, y0));
            double a = s[(y0 * w + x0) * 4 + c], b = s[(y0 * w + x1) * 4 + c], d = s[(y1 * w + x0) * 4 + c], e = s[(y1 * w + x1) * 4 + c];
            double top = a + (b - a) * fx, bottom = d + (e - d) * fx;
            return top + (bottom - top) * fy;
        }

        static byte Clamp(double v) { return (byte)(v < 0 ? 0 : v > 255 ? 255 : v + 0.5); }

        // How strong the lens is for a piece of glass `size` DIPs across its short side -- the library's own
        // choices scaled: 12/24 dp on a 48 dp button, 24/24 dp on a 64 dp bar.
        public static void Strength(double size, out double height, out double amount)
        {
            height = Math.Min(30, size * 0.3);
            amount = Math.Min(40, size * 0.45);
        }
    }

    // A spring, for WPF animations: the library's spring(dampingRatio 0.5, stiffness 300) -- quick, with one soft
    // overshoot. Play it over 0.6 s; by then it has settled.
    class Spring : EasingFunctionBase
    {
        public double Damping = 0.5, Stiffness = 300, Seconds = 0.6;
        protected override double EaseInCore(double t)
        {
            double w0 = Math.Sqrt(Stiffness), z = Damping, wd = w0 * Math.Sqrt(1 - z * z), s = t * Seconds;
            return 1 - Math.Exp(-z * w0 * s) * (Math.Cos(wd * s) + z * w0 / wd * Math.Sin(wd * s));
        }
        protected override Freezable CreateInstanceCore() { return new Spring(); }
    }

    // One piece of glass in a panel: its own refracted slice of the backdrop under a tint, a specular rim that
    // catches the light at two opposite corners, a soft sheen across the top, a faint inner shadow. Touched, it
    // behaves like the library's liquid buttons: pressed, it swells a little and a soft light follows the pointer;
    // dragged, it leans and stretches toward the pointer on an elastic band, then springs back.
    class GlassTile : Border
    {
        public Rect At;
        public readonly double Radius;
        public readonly Grid Body = new Grid();
        public readonly ScaleTransform Zoom = new ScaleTransform(1, 1);
        readonly TranslateTransform lean = new TranslateTransform();
        public Brush Glass;
        public bool Active, Quiet, Interactive;
        public Color Accent = Color.FromRgb(0x0A, 0x84, 0xFF);
        public TextBlock Icon;
        readonly Border hover, glow;
        readonly RadialGradientBrush light = new RadialGradientBrush { MappingMode = BrushMappingMode.Absolute };
        bool painted, shownActive, pressed;
        Point start;

        public GlassTile(Rect at, double radius)
        {
            At = at; Radius = radius; Width = at.Width; Height = at.Height;
            CornerRadius = new CornerRadius(radius);
            BorderThickness = new Thickness(1);
            SetResourceReference(BorderBrushProperty, "CcRim");
            var tg = new TransformGroup(); tg.Children.Add(Zoom); tg.Children.Add(lean);
            RenderTransform = tg; RenderTransformOrigin = new Point(0.5, 0.5);
            var inner = new CornerRadius(Math.Max(0, radius - 1));
            var sheen = new Border { CornerRadius = inner, IsHitTestVisible = false };
            sheen.SetResourceReference(BackgroundProperty, "CcSheen");
            // the library's inner shadow: a soft darkening just inside the top edge, as if the rim stood proud
            var shade = new Border { CornerRadius = inner, IsHitTestVisible = false,
                Background = new LinearGradientBrush(new GradientStopCollection { new GradientStop(Color.FromArgb(26, 0, 0, 0), 0), new GradientStop(Color.FromArgb(0, 0, 0, 0), Math.Min(0.45, 14 / Math.Max(1, at.Height))) }, new Point(0, 0), new Point(0, 1)) };
            hover = new Border { CornerRadius = inner, Opacity = 0, IsHitTestVisible = false };
            hover.SetResourceReference(BackgroundProperty, "CcHover");
            // the interactive highlight: white 8% all over plus a 15% glow, 1.5x the tile's short side across, under the pointer
            light.GradientStops.Add(new GradientStop(Color.FromArgb(38, 255, 255, 255), 0));
            light.GradientStops.Add(new GradientStop(Color.FromArgb(38, 255, 255, 255), 0.5));
            light.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1));
            light.RadiusX = light.RadiusY = Math.Min(at.Width, at.Height) * 1.5;
            var glowLayers = new Grid();
            glowLayers.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)) });
            glowLayers.Children.Add(new Border { Background = light });
            glow = new Border { CornerRadius = inner, Opacity = 0, IsHitTestVisible = false, Child = glowLayers, ClipToBounds = true };
            var g = new Grid();
            g.Children.Add(sheen); g.Children.Add(shade); g.Children.Add(hover); g.Children.Add(glow); g.Children.Add(Body);
            Child = g;
            MouseEnter += delegate { hover.Opacity = 1; };
            MouseLeave += delegate { hover.Opacity = 0; Release(); };
            PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (!Interactive) return;
                pressed = true; start = e.GetPosition(this);
                light.Center = light.GradientOrigin = start;
                double grow = 1 + 4 / Math.Max(1, Math.Min(At.Width, At.Height));
                Animate(Zoom, ScaleTransform.ScaleXProperty, grow); Animate(Zoom, ScaleTransform.ScaleYProperty, grow);
                glow.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(600)) { EasingFunction = new Spring() });
            };
            PreviewMouseMove += (s, e) =>
            {
                if (!pressed) return;
                var p = e.GetPosition(this);
                light.Center = light.GradientOrigin = p;
                // lean toward the pointer on an elastic band (tanh), stretching along the pull
                double ox = p.X - start.X, oy = p.Y - start.Y, maxOffset = Math.Min(At.Width, At.Height), maxDim = Math.Max(At.Width, At.Height);
                lean.BeginAnimation(TranslateTransform.XProperty, null); lean.BeginAnimation(TranslateTransform.YProperty, null);
                lean.X = maxOffset * Math.Tanh(0.05 * ox / maxOffset); lean.Y = maxOffset * Math.Tanh(0.05 * oy / maxOffset);
                double grow = 1 + 4 / Math.Max(1, maxOffset), stretch = 4 / Math.Max(1, At.Height), angle = Math.Atan2(oy, ox);
                Zoom.BeginAnimation(ScaleTransform.ScaleXProperty, null); Zoom.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                Zoom.ScaleX = grow + stretch * Math.Abs(Math.Cos(angle) * ox / maxDim) * Math.Min(1, At.Width / At.Height);
                Zoom.ScaleY = grow + stretch * Math.Abs(Math.Sin(angle) * oy / maxDim) * Math.Min(1, At.Height / At.Width);
            };
            PreviewMouseLeftButtonUp += delegate { Release(); };
        }

        void Release()
        {
            if (!pressed) return;
            pressed = false;
            Animate(Zoom, ScaleTransform.ScaleXProperty, 1); Animate(Zoom, ScaleTransform.ScaleYProperty, 1);
            Animate(lean, TranslateTransform.XProperty, 0); Animate(lean, TranslateTransform.YProperty, 0);
            glow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(600)) { EasingFunction = new Spring() });
        }

        static void Animate(Animatable target, DependencyProperty p, double to)
        {
            target.BeginAnimation(p, new DoubleAnimation(to, TimeSpan.FromMilliseconds(600)) { EasingFunction = new Spring() });
        }

        public void ScaleTo(double to, int ms, bool spring)
        {
            IEasingFunction ease = spring ? (IEasingFunction)new Spring() : new CubicEase { EasingMode = EasingMode.EaseOut };
            var time = TimeSpan.FromMilliseconds(spring ? 600 : ms);
            Zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, time) { EasingFunction = ease });
            Zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, time) { EasingFunction = ease });
        }

        public void Paint(bool light)
        {
            Background = !Active ? Glass : light ? new SolidColorBrush(Accent) : GlassPanel.White;
            if (Icon != null)
            {
                if (!Active) Icon.ClearValue(TextBlock.ForegroundProperty);
                else Icon.Foreground = light ? Brushes.White : new SolidColorBrush(Accent);
            }
            // A switch flipping: a quick squeeze and a wobble back, like a drop of liquid.
            if (painted && !Quiet && Active != shownActive)
            {
                var time = TimeSpan.FromMilliseconds(600);
                Zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.9, 1, time) { EasingFunction = new Spring() });
                Zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.9, 1, time) { EasingFunction = new Spring() });
            }
            painted = true; shownActive = Active;
        }
    }

    // A Liquid Glass panel under the menu bar -- Control Center, Wi-Fi, Sound, Notification Center. Its pieces
    // float over whatever is behind, each refracting its own part of it. It springs open from its menu bar item,
    // and a click anywhere else shrinks it away. One is open at a time, like macOS menus.
    abstract class GlassPanel : Window
    {
        protected const double U = 64, G = 12, Pad = 30;   // the transparent margin holds the whole soft shadow
        public static readonly Brush White = Theme.B("#F2FFFFFF");
        protected static readonly Color Blue = Color.FromRgb(0x0A, 0x84, 0xFF), Orange = Color.FromRgb(0xFF, 0x9F, 0x0A),
            Purple = Color.FromRgb(0x5E, 0x5C, 0xE6), Red = Color.FromRgb(0xFF, 0x3B, 0x30);
        protected const string PlayShape = "M0,0 L10,6 L0,12 Z", PauseShape = "M0,0 H3.6 V12 H0 Z M6.4,0 H10 V12 H6.4 Z",
            PreviousShape = "M11,0 L5,6 L11,12 Z M6,0 L0,6 L6,12 Z", NextShape = "M0,0 L6,6 L0,12 Z M5,0 L11,6 L5,12 Z";

        protected readonly Canvas canvas = new Canvas();
        protected readonly List<GlassTile> tiles = new List<GlassTile>();
        protected bool light;   // a bright backdrop (a white window behind): whiter glass and dark text, as macOS does
        readonly DropShadowEffect shadow = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 3, Opacity = 0.3, Color = Colors.Black };
        static GlassPanel current;
        FrameworkElement anchor;
        DateTime hiddenAt;
        bool closing;

        protected GlassPanel(string title, double width)
        {
            Title = title;
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
            ShowInTaskbar = false; Topmost = true; ResizeMode = ResizeMode.NoResize; ShowActivated = true;
            FontFamily = Theme.Font;
            Width = width; Height = 2 * Pad;
            SetResourceReference(ForegroundProperty, "CcFg");
            Palette(false);
            canvas.Effect = shadow;
            Content = canvas;
            Deactivated += delegate { if (!Outside.MenuOpen) Dismiss(false); };   // a menu it opened keeps it open
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Dismiss(false); };
            SourceInitialized += delegate { Native.MakeToolWindow(new WindowInteropHelper(this).Handle, false); };
        }

        // Builds what depends on the current state and sets the panel's height; runs before the backdrop is taken.
        protected virtual void Prepare() { }
        // Shows live values (switches, levels); runs once the panel knows whether it's over something bright.
        protected abstract void Refresh();

        // Opens under `from` (its menu bar item; null lines it up with the screen's right edge), or closes it.
        public void ToggleFrom(FrameworkElement from)
        {
            if (closing) return;                                                // this click is the one closing it
            if (IsVisible) { Dismiss(false); return; }
            if ((DateTime.UtcNow - hiddenAt).TotalMilliseconds < 350) return;   // ...or the one that just closed it
            if (current != null && current != this) current.Dismiss(true);
            current = this;
            anchor = from;
            foreach (var t in tiles) t.Quiet = true;
            Prepare();
            Place();
            Frost();
            Refresh();
            Show();
            Native.Activate(new WindowInteropHelper(this).Handle);
            Outside.Watch(this, () => Dismiss(false));
            AnimateIn();
        }

        public void Dismiss(bool instant)
        {
            if (!IsVisible || closing) return;
            closing = true;
            Action hide = () => { Hide(); hiddenAt = DateTime.UtcNow; closing = false; };
            if (instant) hide(); else AnimateOut(hide);
        }

        // Rebuilds the panel while it's open (new scan results, a row opening up) and re-takes its backdrop.
        protected void Relayout(Action build)
        {
            build();
            if (IsVisible && !closing) { Frost(); Refresh(); }
        }

        void Place()
        {
            var b = DisplayLayout.Dip(DisplayLayout.Selected.Bounds);
            double right = b.Right - 8;   // where the glass's right edge goes
            if (anchor != null && anchor.IsVisible)
                try { right = anchor.PointToScreen(new Point(anchor.ActualWidth, 0)).X / App.Dpi + 4; } catch { }
            right = Math.Min(right, b.Right - 8);
            Left = Math.Max(b.Left + 8 - Pad, right + Pad - Width);
            Top = b.Top + (Settings.ShowMenuBar ? MenuBarWindow.H : 0) + 6 - Pad;
        }

        // One capture as it opens (it doesn't move while open), refracted separately for every piece of glass.
        void Frost()
        {
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            const int scale = 1;   // full resolution and only a light frost: Apple's glass shows the backdrop crisply enough to see it bend
            double d = App.Dpi, k = d / scale;
            byte[] px = null; int pw = 0, ph = 0;
            if (Glass.Exclude(hwnd))
            {
                long sum = 0;
                var shot = Glass.Behind((int)Math.Round(Left * d), (int)Math.Round(Top * d), (int)Math.Round(Width * d), (int)Math.Round(Height * d), 2, ref sum, scale);
                if (shot != null) { pw = shot.PixelWidth; ph = shot.PixelHeight; px = new byte[pw * ph * 4]; shot.CopyPixels(px, pw * 4, 0); }
            }
            Palette(px != null ? Bright(px) : Wallpaper.TopIsLight);
            var tint = (Brush)Resources["CcTint"];
            foreach (var t in tiles)
            {
                var r = t.At;
                double lh, la;
                Liquid.Strength(Math.Min(r.Width, r.Height), out lh, out la);
                bool small = r.Width * r.Height < 140 * 140;   // dispersion on the small, round pieces, as the library does on its thumbs
                t.Glass = px != null
                    ? Glass.Slice(Liquid.Lens(px, pw, ph, new Rect(r.X * k, r.Y * k, r.Width * k, r.Height * k), t.Radius * k, lh * k, la * k, small), new Rect(0, 0, 1, 1), tint)
                    : Glass.WallpaperPane(tint, new Rect(Left + r.X, Top + r.Y, r.Width, r.Height));
                t.Paint(light);
            }
        }

        // Mostly bright, or a big bright patch (a white window under part of it): white text would get lost there.
        internal static bool Bright(byte[] px)
        {
            double sum = 0; int n = 0, bright = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                double l = 0.0722 * px[i] + 0.7152 * px[i + 1] + 0.2126 * px[i + 2];
                sum += l; n++;
                if (l > 200) bright++;
            }
            return n > 0 && (sum / n > 150 || bright > n * 0.35);
        }

        // Every colour in a panel comes from here, so one switch flips the whole thing between the two looks.
        void Palette(bool bright)
        {
            light = bright;
            string[] keys = { "CcFg", "CcDim", "CcInner", "CcTrack", "CcFill", "CcHover", "CcTint", "CcRow" };
            string[] dark = { "#F2FFFFFF", "#B3FFFFFF", "#33FFFFFF", "#40FFFFFF", "#F2FFFFFF", "#14FFFFFF", "#1FFFFFFF", "#1FFFFFFF" };
            string[] pale = { "#E6000000", "#8C000000", "#17000000", "#21000000", "#80000000", "#0D000000", "#40FFFFFF", "#12000000" };   // light glass stays see-through
            for (int i = 0; i < keys.Length; i++) Resources[keys[i]] = Theme.B(bright ? pale[i] : dark[i]);
            Resources["CcTint"] = Theme.Tint(bright ? pale[6] : dark[6]);   // the Liquid Glass slider
            // The rim catches light at the top-left and bottom-right corners; the sheen fades down from the top.
            Resources["CcRim"] = Gradient(new Point(0, 0), new Point(1, 1), bright
                ? new[] { "#E6FFFFFF", "#66FFFFFF", "#0AFFFFFF", "#66FFFFFF", "#D9FFFFFF" }
                : new[] { "#B3FFFFFF", "#40FFFFFF", "#05FFFFFF", "#40FFFFFF", "#A6FFFFFF" }, new[] { 0, 0.22, 0.5, 0.78, 1 });
            Resources["CcSheen"] = Gradient(new Point(0, 0), new Point(0, 1), bright
                ? new[] { "#80FFFFFF", "#1AFFFFFF", "#00FFFFFF" } : new[] { "#2EFFFFFF", "#0AFFFFFF", "#00FFFFFF" }, new[] { 0, 0.4, 1.0 });
            shadow.Opacity = bright ? 0.16 : 0.3;
        }

        static Brush Gradient(Point a, Point b, string[] colors, double[] at)
        {
            var g = new LinearGradientBrush { StartPoint = a, EndPoint = b };
            for (int i = 0; i < colors.Length; i++) g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(colors[i]), at[i]));
            g.Freeze();
            return g;
        }

        // ---------- animation ----------

        Point Origin { get { return new Point(Width - Pad, Pad); } }   // the corner nearest the menu bar item
        static double Distance(GlassTile t, Point p) { return (new Point(t.At.X + t.At.Width / 2, t.At.Y + t.At.Height / 2) - p).Length; }

        // The pieces spring out one after another, the nearest to the menu bar first.
        void AnimateIn()
        {
            double last = 0;
            foreach (var t in tiles)
            {
                double wait = Distance(t, Origin) * 0.3;
                last = Math.Max(last, wait);
                var delay = TimeSpan.FromMilliseconds(wait);
                var ease = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut };
                t.Opacity = 0; t.Zoom.ScaleX = t.Zoom.ScaleY = 0.6;
                t.Zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(460)) { BeginTime = delay, EasingFunction = ease });
                t.Zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(460)) { BeginTime = delay, EasingFunction = ease });
                t.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)) { BeginTime = delay });
            }
            var opened = tiles.ToList();
            Later(last + 480, () => { foreach (var t in opened) t.Quiet = false; });
        }

        // ...and shrink back toward it, the farthest first.
        void AnimateOut(Action done)
        {
            double far = tiles.Count == 0 ? 0 : tiles.Max(t => Distance(t, Origin));
            foreach (var t in tiles)
            {
                var delay = TimeSpan.FromMilliseconds((far - Distance(t, Origin)) * 0.12);
                var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
                var time = TimeSpan.FromMilliseconds(170);
                t.Zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.82, time) { BeginTime = delay, EasingFunction = ease });
                t.Zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.82, time) { BeginTime = delay, EasingFunction = ease });
                t.BeginAnimation(OpacityProperty, new DoubleAnimation(0, time) { BeginTime = delay, EasingFunction = ease });
            }
            Later(far * 0.12 + 190, done);
        }

        // ---------- building blocks ----------

        protected GlassTile Add(GlassTile t)
        {
            Canvas.SetLeft(t, t.At.X); Canvas.SetTop(t, t.At.Y);
            canvas.Children.Add(t);
            tiles.Add(t);
            return t;
        }

        protected void ClearTiles()
        {
            foreach (var t in tiles) canvas.Children.Remove(t);
            tiles.Clear();
        }

        // A switch inside a wide tile: white with a coloured icon over dark glass, filled with the colour over light.
        protected void Dot(Border dot, TextBlock glyph, bool on, Color color)
        {
            if (!on) { dot.SetResourceReference(Border.BackgroundProperty, "CcInner"); glyph.ClearValue(TextBlock.ForegroundProperty); return; }
            dot.Background = light ? new SolidColorBrush(color) : White;
            glyph.Foreground = light ? Brushes.White : new SolidColorBrush(color);
        }

        protected static TextBlock Glyph(string glyph, double size)
        {
            return new TextBlock { Text = glyph, FontFamily = Theme.Glyphs, FontSize = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }

        protected static TextBlock Text(string text, double size, FontWeight weight, bool dim = false)
        {
            var t = new TextBlock { Text = text ?? "", FontSize = size, FontWeight = weight, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            if (dim) t.SetResourceReference(TextBlock.ForegroundProperty, "CcDim");
            return t;
        }

        protected static TextBlock Section(string text)
        {
            var t = Text(text, 11.5, FontWeights.SemiBold, true);
            t.Margin = new Thickness(10, 4, 10, 4);
            return t;
        }

        protected static TextBlock Note(string text)
        {
            var t = Text(text, 12.5, FontWeights.Normal, true);
            t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None;
            t.Margin = new Thickness(10, 8, 10, 8);
            return t;
        }

        // A round icon for a list row: filled with its colour when it's the chosen one.
        protected static Border Badge(string glyph, bool on, Color color)
        {
            var b = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), VerticalAlignment = VerticalAlignment.Center };
            var g = Glyph(glyph, 13);
            if (on) { b.Background = new SolidColorBrush(color); g.Foreground = Brushes.White; }
            else b.SetResourceReference(Border.BackgroundProperty, "CcInner");
            b.Child = g;
            return b;
        }

        // A list row inside a tile: a round icon, a title with a status under it, something on the right.
        protected static Border Row(FrameworkElement icon, string title, string sub, FrameworkElement right, Action click)
        {
            var d = new DockPanel { Margin = new Thickness(8, 0, 10, 0) };
            if (icon != null) { icon.Margin = new Thickness(0, 0, 10, 0); DockPanel.SetDock(icon, Dock.Left); d.Children.Add(icon); }
            if (right != null) { right.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(right, Dock.Right); d.Children.Add(right); }
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(Text(title, 13, FontWeights.Medium));
            if (!string.IsNullOrEmpty(sub)) words.Children.Add(Text(sub, 11.5, FontWeights.Normal, true));
            d.Children.Add(words);
            var row = new Border { Height = 40, CornerRadius = new CornerRadius(10), Background = Brushes.Transparent, Child = d };
            if (click != null)
            {
                row.Cursor = Cursors.Hand;
                row.MouseEnter += delegate { row.SetResourceReference(Border.BackgroundProperty, "CcRow"); };
                row.MouseLeave += delegate { row.Background = Brushes.Transparent; };
                row.MouseLeftButtonUp += (s, e) => { e.Handled = true; click(); };
            }
            return row;
        }

        // A solid play / pause / skip button.
        protected static Border Transport(string shape, double height, Action click)
        {
            // Both sides set from the shape's own proportions: with only a height, a star-sized column could clip it.
            var data = Geometry.Parse(shape);
            var path = new System.Windows.Shapes.Path { Data = data, Stretch = Stretch.Fill, Height = height, Width = height * data.Bounds.Width / data.Bounds.Height,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            path.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "CcFg");
            var b = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand, MinWidth = height + 16, Height = height + 14, Child = path };
            b.MouseLeftButtonUp += (s, e) => { e.Handled = true; click(); };
            return b;
        }

        protected static void Later(double ms, Action a)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1, ms)) };
            t.Tick += delegate { t.Stop(); a(); };
            t.Start();
        }
    }

    // A macOS switch: a capsule that fills with blue, the knob sliding across on a spring.
    class Switch2 : Border
    {
        readonly Border knob = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2) };
        readonly TranslateTransform slide = new TranslateTransform();
        bool on;
        public event Action<bool> Changed;

        public Switch2()
        {
            Width = 40; Height = 24; CornerRadius = new CornerRadius(12); Cursor = Cursors.Hand; VerticalAlignment = VerticalAlignment.Center;
            knob.RenderTransform = slide;
            knob.Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = 0.25 };
            Child = knob;
            Set(false, false);
            MouseLeftButtonUp += (s, e) => { e.Handled = true; Set(!on, true); if (Changed != null) Changed(on); };
        }

        public void Set(bool value, bool animate)
        {
            on = value;
            double to = on ? 16 : 0;
            if (animate) slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(340)) { EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut } });
            else { slide.BeginAnimation(TranslateTransform.XProperty, null); slide.X = to; }
            if (on) Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)); else SetResourceReference(BackgroundProperty, "CcInner");
        }
    }
}
