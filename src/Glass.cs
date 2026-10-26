using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace OpenDock
{
    // Native compositor blur by default, with wallpaper and opt-in capture compatibility fallbacks.
    static class Glass
    {
        const uint ExcludeFromCapture = 0x11;   // WDA_EXCLUDEFROMCAPTURE, Windows 10 2004+

        // What is behind a screen rectangle (physical px), at quarter size and blurred. Null when it looks the
        // same as last time (sum), so callers can skip redrawing.
        public static BitmapSource Behind(int x, int y, int w, int h, int blur, ref long sum)
        {
            if (w <= 0 || h <= 0) return null;
            int sw = Math.Max(1, w / 4), sh = Math.Max(1, h / 4);
            var px = Icons.CaptureScaled(x, y, w, h, sw, sh);
            if (px == null) return null;
            long s = x * 7919L + y * 31L + w;
            for (int i = 0; i < px.Length; i += 4) s = s * 31 + BitConverter.ToInt32(px, i);
            if (s == sum) return null;
            sum = s;
            Icons.BoxBlur(px, sw, sh, blur, 3);   // three box passes ~ a gaussian
            var b = BitmapSource.Create(sw, sh, 96, 96, PixelFormats.Bgr32, null, px, sw * 4);
            b.Freeze();
            return b;
        }

        public static bool Exclude(IntPtr hwnd)
        {
            bool ok = Native.SetWindowDisplayAffinity(hwnd, Settings.LiveGlass && Settings.GlassCompatibilityCapture ? ExcludeFromCapture : 0);
            return Settings.LiveGlass && Settings.GlassCompatibilityCapture && ok;
        }

        public static Brush WallpaperPane(Brush tint) { return Pane(Wallpaper.Blurred, tint); }

        static Brush Pane(ImageSource img, Brush tint)
        {
            var r = new Rect(0, 0, 1, 1);
            var g = new DrawingGroup();
            if (img != null) g.Children.Add(new ImageDrawing(img, r));
            g.Children.Add(new GeometryDrawing(tint, null, new RectangleGeometry(r)));
            var b = new DrawingBrush(g) { Stretch = Stretch.Fill };
            b.Freeze();
            return b;
        }

        // Wallpaper frost costs no capture loop. Optional live sampling redraws on moves and resizes,
        // and backs off while idle. Never sample our own window if capture exclusion failed.
        public static void Attach(Window w, string tintKey)
        {
            var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(w);
            var originalFrame = chrome == null ? new Thickness(0) : chrome.GlassFrameThickness;
            long sum = 0;
            bool live = false, compositor = false;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            var since = System.Diagnostics.Stopwatch.StartNew();
            Action update = delegate
            {
                if (compositor)
                {
                    if (w.IsVisible && w.WindowState != WindowState.Minimized) NativeGlass.Refresh(new WindowInteropHelper(w).Handle);
                    return;
                }
                if (!live) return;
                if (!w.IsVisible || w.WindowState == WindowState.Minimized) { t.Interval = TimeSpan.FromMilliseconds(300); return; }
                since.Restart();
                Native.RECT rc;
                Native.GetWindowRect(new WindowInteropHelper(w).Handle, out rc);
                var img = Behind(rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top, 2, ref sum);
                if (img == null) { t.Interval = TimeSpan.FromMilliseconds(Math.Min(300, t.Interval.TotalMilliseconds * 1.5)); return; }
                t.Interval = TimeSpan.FromMilliseconds(80);
                w.Background = Pane(img, (Brush)w.FindResource(tintKey));
            };
            // Moves arrive faster than the screen refreshes; one redraw per ~frame is enough.
            EventHandler moved = delegate { if (since.ElapsedMilliseconds >= 12) update(); };
            Action configure = delegate
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                if (hwnd == IntPtr.Zero) return;
                if (Settings.LiveGlass && !Settings.GlassCompatibilityCapture && NativeGlass.Apply(hwnd, true))
                {
                    live = false; compositor = true; t.Interval = TimeSpan.FromMilliseconds(100); t.Start();
                    w.Background = (Brush)w.FindResource(tintKey); NativeGlass.ConfigureClient(w); return;
                }
                compositor = false; NativeGlass.Apply(hwnd, false);
                if (chrome != null) chrome.GlassFrameThickness = originalFrame;
                live = Exclude(hwnd);
                sum = 0;
                if (live) { t.Start(); update(); }
                else { t.Stop(); w.Background = WallpaperPane((Brush)w.FindResource(tintKey)); }
            };
            t.Tick += delegate { update(); };
            w.SourceInitialized += delegate { configure(); };
            w.Loaded += delegate { configure(); };
            w.LocationChanged += moved;
            w.SizeChanged += delegate { moved(w, EventArgs.Empty); };
            w.IsVisibleChanged += delegate { t.Interval = TimeSpan.FromMilliseconds(80); };
            Theme.Changed += configure;
            Settings.Changed += configure;
            Wallpaper.Changed += configure;
            w.Closed += delegate { t.Stop(); Theme.Changed -= configure; Settings.Changed -= configure; Wallpaper.Changed -= configure; };
        }

        // Menus: frosted once as they open (they don't move while open), so they cost nothing afterwards.
        public static void Init()
        {
            EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((s, e) => Later((Control)s)));
            EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.SubmenuOpenedEvent, new RoutedEventHandler((s, e) =>
            {
                var mi = e.OriginalSource as MenuItem;
                if (mi != s) return;   // the event bubbles; handle it once, on the item that opened
                var p = mi.Template == null ? null : mi.Template.FindName("PART_Popup", mi) as Popup;
                if (p != null && p.Child is FrameworkElement) Later((FrameworkElement)p.Child);
            }));
        }

        static void Later(FrameworkElement root)
        {
            root.Dispatcher.BeginInvoke(new Action(() => Frost(root)), DispatcherPriority.Loaded);
        }

        static void Frost(FrameworkElement root)
        {
            var panel = root as Border ?? (VisualTreeHelper.GetChildrenCount(root) > 0 ? VisualTreeHelper.GetChild(root, 0) as Border : null);
            var src = PresentationSource.FromVisual(root) as HwndSource;
            if (panel == null || src == null || !panel.IsVisible) return;
            if (Settings.LiveGlass && !Settings.GlassCompatibilityCapture && NativeGlass.Apply(src.Handle, true))
            {
                src.CompositionTarget.BackgroundColor = Colors.Transparent;
                panel.SetResourceReference(Border.BackgroundProperty, "MenuGlass"); return;
            }
            NativeGlass.Apply(src.Handle, false);
            if (!Exclude(src.Handle)) { panel.Background = WallpaperPane((Brush)panel.FindResource("MenuGlass")); return; }
            var p = panel.PointToScreen(new Point(0, 0));
            long sum = 0;
            var img = Behind((int)p.X, (int)p.Y, (int)(panel.ActualWidth * src.CompositionTarget.TransformToDevice.M11), (int)(panel.ActualHeight * src.CompositionTarget.TransformToDevice.M22), 2, ref sum);
            if (img != null) panel.Background = Pane(img, (Brush)panel.FindResource("MenuGlass"));
        }
    }
}
