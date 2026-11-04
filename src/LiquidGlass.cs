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
    // Apple's Liquid Glass, worked out on the CPU once each time a panel opens: what's behind a piece of glass is
    // lightly blurred and a little more saturated, and near the rim it bends -- content from just past the edge is
    // pulled in, red and blue splitting slightly, the way the thick edge of a lens refracts.
    static class Liquid
    {
        // `src` is a Bgr32 capture (sw x sh); `r`, `radius` and `band` are in its pixels. Returns the glass for `r`.
        public static BitmapSource Lens(byte[] src, int sw, int sh, Rect r, double radius, double band)
        {
            int w = Math.Max(1, (int)Math.Round(r.Width)), h = Math.Max(1, (int)Math.Round(r.Height));
            var dst = new byte[w * h * 4];
            double hx = r.Width / 2, hy = r.Height / 2, cx = r.X + hx, cy = r.Y + hy;
            radius = Math.Min(radius, Math.Min(hx, hy));
            band = Math.Max(1, band);
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                {
                    double px = r.X + i + 0.5, py = r.Y + j + 0.5, dx = px - cx, dy = py - cy;
                    // distance in from the rounded edge, and which way is out
                    double qx = Math.Abs(dx) - (hx - radius), qy = Math.Abs(dy) - (hy - radius), nx, ny, outside;
                    if (qx > 0 && qy > 0) { double l = Math.Sqrt(qx * qx + qy * qy); outside = l - radius; nx = qx / l; ny = qy / l; }
                    else if (qx > qy) { outside = qx - radius; nx = 1; ny = 0; }
                    else { outside = qy - radius; nx = 0; ny = 1; }
                    if (dx < 0) nx = -nx;
                    if (dy < 0) ny = -ny;
                    double depth = -outside, sx = px, sy = py, split = 0, lift = 0;
                    if (depth < band)
                    {
                        double t = 1 - Math.Max(0, depth) / band;   // 1 at the rim, 0 where the bend ends
                        t *= t;
                        sx += nx * t * band * 0.85; sy += ny * t * band * 0.85;
                        split = t * 1.4; lift = t * 20;
                    }
                    double red = Sample(src, sw, sh, sx + nx * split, sy + ny * split, 2);
                    double green = Sample(src, sw, sh, sx, sy, 1);
                    double blue = Sample(src, sw, sh, sx - nx * split, sy - ny * split, 0);
                    double lum = 0.2126 * red + 0.7152 * green + 0.0722 * blue;
                    int o = (j * w + i) * 4;
                    dst[o + 2] = Clamp(lum + (red - lum) * 1.35 + lift);
                    dst[o + 1] = Clamp(lum + (green - lum) * 1.35 + lift);
                    dst[o] = Clamp(lum + (blue - lum) * 1.35 + lift);
                    dst[o + 3] = 255;
                }
            var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, dst, w * 4);
            b.Freeze();
            return b;
        }

        static double Sample(byte[] s, int w, int h, double x, double y, int c)
        {
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
    }

    // One piece of glass in a panel: its own refracted slice of the backdrop under a tint, a specular rim that
    // catches the light at two opposite corners, a soft sheen across the top. It lifts under the cursor, presses
    // in when clicked and wobbles when switched. Switched on, it turns solid: white with a coloured icon over dark
    // glass, the colour with a white icon over light glass.
    class GlassTile : Border
    {
        public Rect At;
        public readonly double Radius;
        public readonly Grid Body = new Grid();
        public readonly ScaleTransform Zoom = new ScaleTransform(1, 1);
        public Brush Glass;
        public bool Active, Quiet, Interactive;
        public Color Accent = Color.FromRgb(0x0A, 0x84, 0xFF);
        public TextBlock Icon;
        readonly Border hover;
        bool painted, shownActive;

        public GlassTile(Rect at, double radius)
        {
            At = at; Radius = radius; Width = at.Width; Height = at.Height;
            CornerRadius = new CornerRadius(radius);
            BorderThickness = new Thickness(1.2);
            SetResourceReference(BorderBrushProperty, "CcRim");
            RenderTransform = Zoom; RenderTransformOrigin = new Point(0.5, 0.5);
            var sheen = new Border { CornerRadius = new CornerRadius(Math.Max(0, radius - 1)), IsHitTestVisible = false };
            sheen.SetResourceReference(BackgroundProperty, "CcSheen");
            hover = new Border { CornerRadius = new CornerRadius(Math.Max(0, radius - 1)), Opacity = 0, IsHitTestVisible = false };
            hover.SetResourceReference(BackgroundProperty, "CcHover");
            var g = new Grid();
            g.Children.Add(sheen); g.Children.Add(hover); g.Children.Add(Body);
            Child = g;
            MouseEnter += delegate { hover.Opacity = 1; if (Interactive) ScaleTo(1.045, 420, true); };
            MouseLeave += delegate { hover.Opacity = 0; if (Interactive) ScaleTo(1, 460, true); };
            PreviewMouseLeftButtonDown += delegate { if (Interactive) ScaleTo(0.94, 110, false); };
            PreviewMouseLeftButtonUp += delegate { if (Interactive) ScaleTo(IsMouseOver ? 1.045 : 1, 520, true); };
        }

        public void ScaleTo(double to, int ms, bool spring)
        {
            IEasingFunction ease = spring ? (IEasingFunction)new ElasticEase { Oscillations = 1, Springiness = 6, EasingMode = EasingMode.EaseOut }
                                          : new CubicEase { EasingMode = EasingMode.EaseOut };
            var time = TimeSpan.FromMilliseconds(ms);
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
                var ease = new ElasticEase { Oscillations = 2, Springiness = 5, EasingMode = EasingMode.EaseOut };
                var time = TimeSpan.FromMilliseconds(560);
                double to = IsMouseOver && Interactive ? 1.045 : 1;
                Zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.88, to, time) { EasingFunction = ease });
                Zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.88, to, time) { EasingFunction = ease });
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
            const int scale = 2;   // half resolution: sharp enough to read as clear glass, cheap to bend
            double d = App.Dpi, k = d / scale;
            byte[] px = null; int pw = 0, ph = 0;
            if (Glass.Exclude(hwnd))
            {
                long sum = 0;
                var shot = Glass.Behind((int)Math.Round(Left * d), (int)Math.Round(Top * d), (int)Math.Round(Width * d), (int)Math.Round(Height * d), 3, ref sum, scale);
                if (shot != null) { pw = shot.PixelWidth; ph = shot.PixelHeight; px = new byte[pw * ph * 4]; shot.CopyPixels(px, pw * 4, 0); }
            }
            Palette(px != null ? Bright(px) : Wallpaper.TopIsLight);
            var tint = (Brush)Resources["CcTint"];
            foreach (var t in tiles)
            {
                var r = t.At;
                t.Glass = px != null
                    ? Glass.Slice(Liquid.Lens(px, pw, ph, new Rect(r.X * k, r.Y * k, r.Width * k, r.Height * k), t.Radius * k, Math.Min(16, t.Radius) * k), new Rect(0, 0, 1, 1), tint)
                    : Glass.WallpaperPane(tint, new Rect(Left + r.X, Top + r.Y, r.Width, r.Height));
                t.Paint(light);
            }
        }

        // Mostly bright, or a big bright patch (a white window under part of it): white text would get lost there.
        static bool Bright(byte[] px)
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
            string[] pale = { "#E6000000", "#8C000000", "#17000000", "#21000000", "#80000000", "#0D000000", "#A6F7F7F9", "#12000000" };
            for (int i = 0; i < keys.Length; i++) Resources[keys[i]] = Theme.B(bright ? pale[i] : dark[i]);
            Resources["CcTint"] = Theme.Tint(bright ? pale[6] : dark[6]);   // the Liquid Glass slider
            // The rim catches light at the top-left and bottom-right corners; the sheen fades down from the top.
            Resources["CcRim"] = Gradient(new Point(0, 0), new Point(1, 1), bright
                ? new[] { "#FFFFFFFF", "#99FFFFFF", "#14000000", "#80FFFFFF", "#F2FFFFFF" }
                : new[] { "#F2FFFFFF", "#40FFFFFF", "#0DFFFFFF", "#40FFFFFF", "#CCFFFFFF" }, new[] { 0, 0.22, 0.5, 0.78, 1 });
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
