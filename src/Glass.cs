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
    // Original live desktop sampling. Windows must exclude the sampling window from capture to avoid
    // recursively capturing its own pixels. Turning Live glass off uses capture-safe wallpaper frost.
    static class Glass
    {
        const uint ExcludeFromCapture = 0x11;   // WDA_EXCLUDEFROMCAPTURE, Windows 10 2004+

        // What is behind a screen rectangle (physical px), at 1/scale size and blurred. Null when it looks the
        // same as last time (sum), so callers can skip redrawing.
        public static BitmapSource Behind(int x, int y, int w, int h, int blur, ref long sum, int scale = 4)
        {
            if (w <= 0 || h <= 0) return null;
            int sw = Math.Max(1, w / scale), sh = Math.Max(1, h / scale);
            var px = Icons.CaptureScaled(x, y, w, h, sw, sh);
            if (px == null) return null;
            FillDesktop(px, x, y, w, h, sw, sh);
            long s = x * 7919L + y * 31L + w;
            for (int i = 0; i < px.Length; i += 4) s = s * 31 + BitConverter.ToInt32(px, i);
            if (s == sum) return null;
            sum = s;
            Icons.BoxBlur(px, sw, sh, blur, 3);   // three box passes ~ a gaussian
            var b = BitmapSource.Create(sw, sh, 96, 96, PixelFormats.Bgr32, null, px, sw * 4);
            b.Freeze();
            return b;
        }

        static System.Collections.Generic.List<Sys.Display> screens;
        static readonly System.Diagnostics.Stopwatch screensAge = new System.Diagnostics.Stopwatch();

        // Newer Windows builds draw the wallpaper where screen capture can't see it: the bare desktop comes back
        // pure black. Paint those pixels from the wallpaper we already loaded, so the glass still shows it.
        static void FillDesktop(byte[] px, int x, int y, int w, int h, int sw, int sh)
        {
            var wp = Wallpaper.Flat;
            if (wp == null) return;
            int k = 0;
            while (k < px.Length && (px[k] | px[k + 1] | px[k + 2]) != 0) k += 4;
            if (k >= px.Length) return;   // nothing blank: the usual case costs one pass
            if (screens == null || screensAge.ElapsedMilliseconds > 5000) { screens = Sys.Screens(); screensAge.Restart(); }
            int cx = x + w / 2, cy = y + h / 2;
            var d = screens.Find(s => cx >= s.Bounds.Left && cx < s.Bounds.Right && cy >= s.Bounds.Top && cy < s.Bounds.Bottom);
            if (d == null) return;
            var m = d.Bounds;
            int fw = Wallpaper.FlatW, fh = Wallpaper.FlatH;
            double mw = Math.Max(1, m.Right - m.Left), mh = Math.Max(1, m.Bottom - m.Top);
            for (int j = 0; j < sh; j++)
            {
                int fy = (int)((y + (j + 0.5) * h / sh - m.Top) * fh / mh);
                if (fy < 0 || fy >= fh) continue;
                for (int i = 0; i < sw; i++)
                {
                    int o = (j * sw + i) * 4;
                    if ((px[o] | px[o + 1] | px[o + 2]) != 0) continue;
                    int fx = (int)((x + (i + 0.5) * w / sw - m.Left) * fw / mw);
                    if (fx < 0 || fx >= fw) continue;
                    int f = (fy * fw + fx) * 4;
                    px[o] = wp[f]; px[o + 1] = wp[f + 1]; px[o + 2] = wp[f + 2];
                }
            }
        }

        public static bool Exclude(IntPtr hwnd)
        {
            bool ok = Native.SetWindowDisplayAffinity(hwnd, Settings.LiveGlass ? ExcludeFromCapture : 0);
            return Settings.LiveGlass && ok;
        }

        // The part of the (primary screen's) blurred wallpaper behind `area` (screen DIPs), under the tint.
        public static Brush WallpaperPane(Brush tint, Rect area)
        {
            double sw = Math.Max(1, SystemParameters.PrimaryScreenWidth), sh = Math.Max(1, SystemParameters.PrimaryScreenHeight);
            var r = new RectangleGeometry(new Rect(0, 0, 1, 1));
            var g = new DrawingGroup();
            if (Wallpaper.Blurred != null)
                g.Children.Add(new GeometryDrawing(new ImageBrush(Wallpaper.Blurred) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
                    Viewbox = new Rect(area.X / sw, area.Y / sh, Math.Max(0.001, area.Width / sw), Math.Max(0.001, area.Height / sh)) }, null, r));
            g.Children.Add(new GeometryDrawing(tint, null, r));
            var b = new DrawingBrush(g) { Stretch = Stretch.Fill };
            b.Freeze();
            return b;
        }

        // The part of a captured backdrop under one piece of a panel (`part` relative to the capture, 0..1), under the tint.
        public static Brush Slice(BitmapSource img, Rect part, Brush tint)
        {
            var r = new RectangleGeometry(new Rect(0, 0, 1, 1));
            var g = new DrawingGroup();
            g.Children.Add(new GeometryDrawing(new ImageBrush(img) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.RelativeToBoundingBox, Viewbox = part }, null, r));
            g.Children.Add(new GeometryDrawing(tint, null, r));
            var b = new DrawingBrush(g) { Stretch = Stretch.Fill };
            b.Freeze();
            return b;
        }

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
            long sum = 0;
            bool live = false;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            var since = System.Diagnostics.Stopwatch.StartNew();
            Action update = delegate
            {
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
            // Wallpaper frost: the slice of wallpaper behind the window, so it follows the window like glass.
            Action frost = delegate
            {
                Native.RECT rc;
                if (Native.GetWindowRect(new WindowInteropHelper(w).Handle, out rc)) w.Background = WallpaperPane((Brush)w.FindResource(tintKey), DisplayLayout.Dip(rc));
            };
            // Moves arrive faster than the screen refreshes; one redraw per ~frame is enough.
            EventHandler moved = delegate { if (since.ElapsedMilliseconds < 12) return; if (live) update(); else { since.Restart(); frost(); } };
            Action configure = delegate
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                if (hwnd == IntPtr.Zero) return;
                live = Exclude(hwnd);
                sum = 0;
                if (live) { t.Start(); update(); }
                else { t.Stop(); frost(); }
            };
            t.Tick += delegate { update(); };
            w.SourceInitialized += delegate { configure(); };
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
            var p = panel.PointToScreen(new Point(0, 0));
            if (!Exclude(src.Handle)) { panel.Background = WallpaperPane((Brush)panel.FindResource("MenuGlass"), new Rect(p.X / App.Dpi, p.Y / App.Dpi, panel.ActualWidth, panel.ActualHeight)); return; }
            long sum = 0;
            var img = Behind((int)p.X, (int)p.Y, (int)(panel.ActualWidth * App.Dpi), (int)(panel.ActualHeight * App.Dpi), 2, ref sum);
            if (img != null) panel.Background = Pane(img, (Brush)panel.FindResource("MenuGlass"));
        }
    }
}
