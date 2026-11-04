using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace OpenDock
{
    // Mission Control: every window on this display flies out of the way into a tidy grid of live pictures over
    // the dimmed wallpaper; click one to go to it. App Exposé is the same for one app's windows.
    class MissionControl : Window
    {
        [StructLayout(LayoutKind.Sequential)]
        struct THUMB_PROPS { public int dwFlags; public Native.RECT rcDestination, rcSource; public byte opacity; public bool fVisible, fSourceClientAreaOnly; }
        [DllImport("dwmapi.dll")] static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
        [DllImport("dwmapi.dll")] static extern int DwmUnregisterThumbnail(IntPtr thumb);
        [DllImport("dwmapi.dll")] static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref THUMB_PROPS p);

        class Item
        {
            public IntPtr Hwnd, Thumb;
            public Rect From, To, Source;          // From/To in this window's physical pixels; Source inside the window
            public string Title, App;
            public Border Ring; public StackPanel Label;
        }

        static MissionControl open;
        readonly List<Item> items = new List<Item>();
        readonly Canvas canvas = new Canvas();
        readonly Image wall = new Image { Stretch = Stretch.UniformToFill };
        readonly Border dim = new Border { Background = Brushes.Black, Opacity = 0 };
        readonly BlurEffect blur = new BlurEffect { Radius = 0, KernelType = KernelType.Gaussian };
        readonly System.Diagnostics.Stopwatch clock = new System.Diagnostics.Stopwatch();
        readonly Sys.Display screen;
        readonly bool appOnly;
        IntPtr hwnd;
        bool leaving, flying;
        Item hot, chosen;
        Action after;
        const double Fly = 340;

        // Opens Mission Control (app = zero) or App Exposé for `app`'s windows; a second call closes it.
        public static void Toggle(IntPtr app)
        {
            if (open != null) { open.Leave(null); return; }
            Native.POINT p; Native.GetCursorPos(out p);
            var screen = Sys.Screens().FirstOrDefault(s => p.X >= s.Bounds.Left && p.X < s.Bounds.Right && p.Y >= s.Bounds.Top && p.Y < s.Bounds.Bottom) ?? DisplayLayout.Selected;
            var wins = Collect(screen, app);
            if (wins.Count == 0) return;
            open = new MissionControl(screen, wins, app != IntPtr.Zero);
            open.Show();
            open.Activate();
        }

        // This display's windows, front to back -- or, for App Exposé, every window of that app.
        static List<IntPtr> Collect(Sys.Display screen, IntPtr app)
        {
            var all = App.Wins.Select(w => w.Hwnd).Concat(App.Finders.Where(f => f.IsVisible).Select(f => new WindowInteropHelper(f).Handle)).Distinct().ToList();
            var z = new Dictionary<IntPtr, int>(); int n = 0;
            Native.EnumWindows((h, l) => { z[h] = n++; return true; }, IntPtr.Zero);
            all = all.Where(h => Native.IsWindow(h) && Native.IsWindowVisible(h) && !Native.IsIconic(h)).OrderBy(h => z.ContainsKey(h) ? z[h] : int.MaxValue).ToList();
            if (app != IntPtr.Zero)
            {
                string key = KeyOf(app);
                return key == null ? new List<IntPtr> { app } : all.Where(h => KeyOf(h) == key).ToList();
            }
            return all.Where(h => Native.MonitorFromWindow(h, 2) == screen.Monitor).ToList();
        }

        static string KeyOf(IntPtr h)
        {
            var w = App.Wins.FirstOrDefault(x => x.Hwnd == h);
            if (w != null) return w.Key;
            if (App.Finders.Any(f => new WindowInteropHelper(f).Handle == h)) return "opendock:finder";
            return Windows.ExeOf(h);
        }

        MissionControl(Sys.Display screen, List<IntPtr> wins, bool appOnly)
        {
            this.screen = screen; this.appOnly = appOnly;
            Title = appOnly ? "App Exposé" : "Mission Control";
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            var b = DisplayLayout.Dip(screen.Bounds);
            Left = b.Left; Top = b.Top; Width = b.Width; Height = b.Height;
            Background = Brushes.Black;
            FontFamily = Theme.Font;
            wall.Source = WallpaperImage(screen);
            wall.Effect = blur;
            var root = new Grid();
            root.Children.Add(wall); root.Children.Add(dim); root.Children.Add(canvas);
            Content = root;
            foreach (var h in wins) items.Add(new Item { Hwnd = h });
            SourceInitialized += delegate
            {
                hwnd = new WindowInteropHelper(this).Handle;
                Native.MakeToolWindow(hwnd, false);
                int off = 1; Native.DwmSetWindowAttribute(hwnd, 3, ref off, 4);   // no Windows fade: we animate
                Setup();
            };
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Leave(null); else if (e.Key == Key.Enter && hot != null) Pick(hot); };
            MouseMove += (s, e) => Hover(e.GetPosition(this));
            MouseLeftButtonUp += (s, e) => { var it = At(e.GetPosition(this)); if (it != null) Pick(it); else Leave(null); };
            MouseRightButtonUp += delegate { Leave(null); };
            Deactivated += delegate { if (!leaving) Leave(null); };
            Closed += delegate { foreach (var it in items) if (it.Thumb != IntPtr.Zero) DwmUnregisterThumbnail(it.Thumb); if (open == this) open = null; };
        }

        // The wallpaper at full size for this display (the Dock's copy is a small blurred one).
        static string wallPath; static BitmapSource wallCache;
        static ImageSource WallpaperImage(Sys.Display s)
        {
            var sb = new StringBuilder(520);
            Native.SystemParametersInfo(0x73, 520, sb, 0);
            string path = sb.ToString();
            if (path == wallPath && wallCache != null) return wallCache;
            try
            {
                if (!File.Exists(path)) return Wallpaper.Blurred;
                var bi = new BitmapImage();
                bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.UriSource = new Uri(path); bi.DecodePixelWidth = Math.Max(640, s.Bounds.Right - s.Bounds.Left); bi.EndInit();
                bi.Freeze();
                wallPath = path; wallCache = bi;
                return bi;
            }
            catch { return Wallpaper.Blurred; }
        }

        void Setup()
        {
            double d = App.Dpi;
            int ox = screen.Bounds.Left, oy = screen.Bounds.Top;
            foreach (var it in items.ToList())
            {
                Native.RECT wr, fr;
                if (!Native.GetWindowRect(it.Hwnd, out wr) || DwmRegisterThumbnail(hwnd, it.Hwnd, out it.Thumb) != 0) { items.Remove(it); continue; }
                if (Native.DwmGetWindowAttribute(it.Hwnd, 9, out fr, 16) != 0) fr = wr;
                it.Source = new Rect(fr.Left - wr.Left, fr.Top - wr.Top, fr.Right - fr.Left, fr.Bottom - fr.Top);
                it.From = new Rect(fr.Left - ox, fr.Top - oy, fr.Right - fr.Left, fr.Bottom - fr.Top);
                it.Title = Native.WindowText(it.Hwnd);
                string name; ImageSource icon = null;
                var dock = App.Docks.FirstOrDefault();
                if (dock == null || !dock.Describe(it.Hwnd, out name, out icon)) name = App.Finders.Any(f => new WindowInteropHelper(f).Handle == it.Hwnd) ? "Finder" : null;
                it.App = name;
                if (icon == null && name == "Finder") icon = Art.Finder;
                it.Label = Label(icon, appOnly ? it.Title : name ?? it.Title);
                it.Ring = new Border { BorderThickness = new Thickness(3.5), CornerRadius = new CornerRadius(10), Opacity = 0, IsHitTestVisible = false };
                it.Ring.SetResourceReference(Border.BorderBrushProperty, "Accent");
                canvas.Children.Add(it.Ring); canvas.Children.Add(it.Label);
            }
            // The grid, below where the Spaces bar would be and above the Dock.
            var area = new Rect(0, 0, (screen.Bounds.Right - screen.Bounds.Left), (screen.Bounds.Bottom - screen.Bounds.Top));
            double top = (appOnly ? 60 : 90) * d, bottom = 120 * d, side = 60 * d;
            var targets = Layout(items.Select(i => i.From.Size).ToList(), new Rect(side, top, Math.Max(100, area.Width - 2 * side), Math.Max(100, area.Height - top - bottom)), 36 * d);
            for (int i = 0; i < items.Count; i++) items[i].To = targets[i];
            clock.Restart(); flying = true;
            CompositionTarget.Rendering += Frame;
            var time = TimeSpan.FromMilliseconds(Fly);
            dim.BeginAnimation(OpacityProperty, new DoubleAnimation(0.38, time));
            blur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(28, time));
        }

        // Rows and columns that make the windows as big as possible, in their front-to-back order, each kept to its
        // own proportions and never shown larger than it really is. Rects in the same units as `area`.
        public static List<Rect> Layout(List<Size> sizes, Rect area, double gap)
        {
            int n = sizes.Count;
            var result = new List<Rect>();
            if (n == 0) return result;
            int bestRows = 1; double bestScore = -1;
            for (int rows = 1; rows <= n; rows++)
            {
                int cols = (int)Math.Ceiling(n / (double)rows);
                double cw = (area.Width - (cols - 1) * gap) / cols, ch = (area.Height - (rows - 1) * gap) / rows;
                if (cw <= 0 || ch <= 0) continue;
                double score = sizes.Sum(s => { double k = Math.Min(1, Math.Min(cw / Math.Max(1, s.Width), ch / Math.Max(1, s.Height))); return s.Width * k * s.Height * k; });
                if (score > bestScore) { bestScore = score; bestRows = rows; }
            }
            int r2 = bestRows, c2 = (int)Math.Ceiling(n / (double)r2);
            double w = (area.Width - (c2 - 1) * gap) / c2, h = (area.Height - (r2 - 1) * gap) / r2;
            for (int i = 0; i < n; i++)
            {
                int row = i / c2, col = i % c2;
                int inRow = Math.Min(c2, n - row * c2);                         // the last row may be short: centre it
                double rowLeft = area.Left + (area.Width - (inRow * w + (inRow - 1) * gap)) / 2;
                double k = Math.Min(1, Math.Min(w / Math.Max(1, sizes[i].Width), h / Math.Max(1, sizes[i].Height)));
                double tw = sizes[i].Width * k, th = sizes[i].Height * k;
                double x = rowLeft + col * (w + gap) + (w - tw) / 2, y = area.Top + row * (h + gap) + (h - th) / 2;
                result.Add(new Rect(x, y, tw, th));
            }
            return result;
        }

        static StackPanel Label(ImageSource icon, string text)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, Opacity = 0, IsHitTestVisible = false };
            if (icon != null) p.Children.Add(new Image { Source = icon, Width = 28, Height = 28, Margin = new Thickness(0, 0, 6, 0) });
            var t = new TextBlock { Text = text ?? "", Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis };
            t.Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = 0.8 };
            p.Children.Add(t);
            return p;
        }

        void Frame(object s, EventArgs e)
        {
            double t = Math.Min(1, clock.Elapsed.TotalMilliseconds / Fly);
            double k = leaving ? 1 - Math.Pow(t, 2) : 1 - Math.Pow(1 - t, 3);   // ease out going, ease in coming back
            foreach (var it in items)
            {
                Rect r = Lerp(it.From, it.To, k);
                if (leaving && it == chosen) r = Lerp(it.From, it.To, k);
                Show(it, r);
            }
            if (t < 1) return;
            CompositionTarget.Rendering -= Frame;
            flying = false;
            if (leaving) { var a = after; Close(); if (a != null) a(); return; }
            foreach (var it in items) Decorate(it);
        }

        static Rect Lerp(Rect a, Rect b, double k)
        {
            return new Rect(a.X + (b.X - a.X) * k, a.Y + (b.Y - a.Y) * k, a.Width + (b.Width - a.Width) * k, a.Height + (b.Height - a.Height) * k);
        }

        void Show(Item it, Rect r)
        {
            var p = new THUMB_PROPS
            {
                dwFlags = 0x1 | 0x2 | 0x4 | 0x8,   // destination, source, opacity, visible
                rcDestination = new Native.RECT { Left = (int)r.Left, Top = (int)r.Top, Right = (int)r.Right, Bottom = (int)r.Bottom },
                rcSource = new Native.RECT { Left = (int)it.Source.Left, Top = (int)it.Source.Top, Right = (int)it.Source.Right, Bottom = (int)it.Source.Bottom },
                opacity = 255, fVisible = true
            };
            DwmUpdateThumbnailProperties(it.Thumb, ref p);
        }

        // Name under each picture; the hovered one gets a blue ring.
        void Decorate(Item it)
        {
            double d = App.Dpi;
            var r = new Rect(it.To.X / d, it.To.Y / d, it.To.Width / d, it.To.Height / d);
            Canvas.SetLeft(it.Ring, r.Left - 5); Canvas.SetTop(it.Ring, r.Top - 5);
            it.Ring.Width = r.Width + 10; it.Ring.Height = r.Height + 10;
            it.Label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(it.Label, r.Left + (r.Width - it.Label.DesiredSize.Width) / 2); Canvas.SetTop(it.Label, r.Bottom + 10);
            it.Label.BeginAnimation(OpacityProperty, new DoubleAnimation(appOnly || items.Count <= 1 ? 1 : 0.85, TimeSpan.FromMilliseconds(160)));
        }

        Item At(Point dip)
        {
            if (flying) return null;
            double d = App.Dpi;
            return items.FirstOrDefault(it => new Rect(it.To.X / d, it.To.Y / d, it.To.Width / d, it.To.Height / d).Contains(dip));
        }

        void Hover(Point p)
        {
            var it = At(p);
            if (it == hot) return;
            if (hot != null) hot.Ring.Opacity = 0;
            hot = it;
            if (hot != null) hot.Ring.Opacity = 1;
        }

        void Pick(Item it)
        {
            chosen = it;
            var h = it.Hwnd;
            Leave(() => Native.Activate(h));
        }

        // Everything flies back to where it was, then the overlay goes.
        void Leave(Action then)
        {
            if (leaving) return;
            leaving = true; after = then;
            foreach (var it in items) { it.Ring.Opacity = 0; it.Label.Opacity = 0; }
            var time = TimeSpan.FromMilliseconds(Fly * 0.8);
            dim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, time));
            blur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(0, time));
            // Running from where they are now (possibly mid-flight).
            double t = flying ? Math.Min(1, clock.Elapsed.TotalMilliseconds / Fly) : 1;
            double k = 1 - Math.Pow(1 - t, 3);
            foreach (var it in items) it.To = Lerp(it.From, it.To, k);
            if (!flying) CompositionTarget.Rendering += Frame;
            flying = true;
            clock.Restart();
        }
    }
}
