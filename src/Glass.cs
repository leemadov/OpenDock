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
    // Frosted glass for OpenDock's own windows and menus: each one is left out of screen capture, so a capture of
    // its area shows what is behind it, which is shrunk, blurred and painted under a translucent tint.
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

        public static bool Exclude(IntPtr hwnd) { return Native.SetWindowDisplayAffinity(hwnd, ExcludeFromCapture); }

        static Brush Pane(ImageSource img, Brush tint)
        {
            var r = new Rect(0, 0, 1, 1);
            var g = new DrawingGroup();
            g.Children.Add(new ImageDrawing(img, r));
            g.Children.Add(new GeometryDrawing(tint, null, new RectangleGeometry(r)));
            var b = new DrawingBrush(g) { Stretch = Stretch.Fill };
            b.Freeze();
            return b;
        }

        // Windows (Finder, Settings, previews): live like the dock. Redrawn on every move or resize (so the glass
        // stays put under the window while you drag it), otherwise checked ~12 times a second, backing off to ~3 a
        // second while nothing behind changes. Without capture exclusion (older Windows) the window keeps its plain
        // background.
        public static void Attach(Window w, string tintKey)
        {
            long sum = 0;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            var since = System.Diagnostics.Stopwatch.StartNew();
            Action update = delegate
            {
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
            Action retint = delegate { sum = 0; };
            t.Tick += delegate { update(); };
            w.SourceInitialized += delegate { if (Exclude(new WindowInteropHelper(w).Handle)) t.Start(); };
            w.LocationChanged += moved;
            w.SizeChanged += delegate { moved(w, EventArgs.Empty); };
            w.IsVisibleChanged += delegate { t.Interval = TimeSpan.FromMilliseconds(80); };
            Theme.Changed += retint;
            w.Closed += delegate { t.Stop(); Theme.Changed -= retint; };
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
            if (panel == null || src == null || !panel.IsVisible || !Exclude(src.Handle)) return;
            var p = panel.PointToScreen(new Point(0, 0));
            long sum = 0;
            var img = Behind((int)p.X, (int)p.Y, (int)(panel.ActualWidth * App.Dpi), (int)(panel.ActualHeight * App.Dpi), 2, ref sum);
            if (img != null) panel.Background = Pane(img, (Brush)panel.FindResource("MenuGlass"));
        }
    }
}
