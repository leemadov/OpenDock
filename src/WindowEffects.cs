using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace OpenDock
{
    // macOS window effects: the Genie (the window pours itself into its Dock icon), the plainer Scale, the zoom
    // when a window opens or maximizes, and the fade when it closes. A picture of the window plays the effect
    // in an overlay; the Genie bends that picture on a GPU mesh, so its curved sides stay smooth.
    //
    // The overlay is an ordinary window whose see-through parts Windows composites on the graphics card (its
    // frame extended over the whole client area). A WPF "AllowsTransparency" window would have to be copied
    // back from the GPU every frame, which is what made a full-screen effect crawl.
    static class WindowFx
    {
        [StructLayout(LayoutKind.Sequential)] struct MARGINS { public int l, r, t, b; }
        [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref MARGINS m);

        public static bool Busy;

        static double Clamp(double v) { return v < 0 ? 0 : v > 1 ? 1 : v; }
        static double Smooth(double t) { return t * t * (3 - 2 * t); }
        static double Lerp(double a, double b, double t) { return a + (b - a) * t; }
        static double EaseInOut(double t) { return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2; }

        // Where row `v` of the window (0 = top, 1 = bottom) is at progress `p` (0 = the window, 1 = in the icon):
        // its height on screen and its left and right edges. First the bottom of the window bends in toward the
        // icon, the sides curving like a bottle's neck; then everything slides down into it, each row narrowing
        // as it nears the icon -- macOS' Genie.
        public static void GenieRow(Rect win, Rect icon, double p, double v, out double y, out double left, out double right)
        {
            // Phase one: the bottom edge stretches down into the icon while the sides bend into a curved neck.
            // Phase two: the top follows, the whole picture pouring through the neck.
            double bend = Smooth(Clamp(p / 0.5));
            double bottom = Lerp(win.Bottom, icon.Bottom, EaseInOut(Clamp(p / 0.55)));
            double top = Lerp(win.Top, icon.Top, EaseInOut(Clamp((p - 0.25) / 0.75)));
            y = Lerp(top, bottom, v);
            // The neck's outline: the window's width at the top of the window, the icon's width at the icon,
            // an S-curve in between.
            double reach = Math.Max(1, icon.Top - win.Top);
            double f = bend * Smooth(Clamp((y - win.Top) / reach));
            left = Lerp(win.Left, icon.Left, f); right = Lerp(win.Right, icon.Right, f);
        }

        // One band of the Genie (rows v0..v1) as a rectangle -- for checks.
        public static Rect GenieSlice(Rect win, Rect icon, double p, double v0, double v1)
        {
            double y0, l0, r0, y1, l1, r1;
            GenieRow(win, icon, p, v0, out y0, out l0, out r0);
            GenieRow(win, icon, p, v1, out y1, out l1, out r1);
            double l = (l0 + l1) / 2, r = (r0 + r1) / 2;
            return new Rect(l, y0, Math.Max(0.5, r - l), Math.Max(0.5, y1 - y0));
        }

        // Minimizes (`restore` false) or restores a window picture between `win` and `icon` (screen DIPs).
        // `ready` runs once the picture is on screen (hide the real window then), `done` at the end.
        public static void Minimize(BitmapSource shot, Rect win, Rect icon, bool restore, bool genie, Action ready, Action done, Func<bool> landed = null)
        {
            // Genie needs the icon below the window (the usual bottom Dock); anywhere else it scales.
            if (genie && icon.Top < win.Top + win.Height * 0.5) genie = false;
            var area = Rect.Union(win, icon);
            area.Inflate(2, 2);
            if (!genie)
            {
                Play(area, 260, (dc, t) =>
                {
                    double p = restore ? 1 - t : t, e = Smooth(p);
                    var r = new Rect(Lerp(win.Left, icon.Left, e), Lerp(win.Top, icon.Top, e), Lerp(win.Width, icon.Width, e), Lerp(win.Height, icon.Height, e));
                    r.Offset(-area.Left, -area.Top);
                    dc.PushOpacity(1 - Clamp((p - 0.5) / 0.5) * 0.85);
                    Picture(dc, shot, r);
                    dc.Pop();
                }, null, ready, done, null, landed);
                return;
            }
            var mesh = new Mesh(shot, area, Math.Max(24, Math.Min(64, (int)(win.Height / 12))), 8);
            Play(area, 440, null, t =>
            {
                double p = restore ? 1 - t : t;
                mesh.Bend((double v, out double y, out double l, out double r) => GenieRow(win, icon, p, v, out y, out l, out r));
                mesh.View.Opacity = 1 - Clamp((p - 0.86) / 0.14) * 0.9;
            }, ready, done, mesh.View, landed);
        }

        // Opening: the window grows out of a slightly smaller, see-through copy of itself, with a touch of overshoot.
        public static void Open(BitmapSource shot, Rect win, Action done)
        {
            var area = win; area.Inflate(win.Width * 0.02, win.Height * 0.02);
            Play(area, 250, (dc, t) =>
            {
                double s = BackOut(t, 0.9), k = 0.88 + 0.12 * s;
                var r = new Rect(win.Left + win.Width * (1 - k) / 2, win.Top + win.Height * (1 - k) / 2, win.Width * k, win.Height * k);
                r.Offset(-area.Left, -area.Top);
                dc.PushOpacity(Clamp(t / 0.6));
                Picture(dc, shot, r);
                dc.Pop();
            }, null, null, done);
        }

        // A window picture with the corners Windows 11 rounds on the real one.
        static void Picture(DrawingContext dc, BitmapSource shot, Rect r)
        {
            dc.PushClip(new RectangleGeometry(r, 8, 8));
            dc.DrawImage(shot, r);
            dc.Pop();
        }

        static double BackOut(double t, double a) { double c = a + 1, u = t - 1; return 1 + c * u * u * u + a * u * u; }

        // Closing: the window sinks back a little as it fades away, like macOS.
        public static void Close(BitmapSource shot, Rect win)
        {
            Play(win, 200, (dc, t) =>
            {
                double e = t * t, s = 1 - 0.07 * e;
                var r = new Rect(win.Width * (1 - s) / 2, win.Height * (1 - s) / 2, win.Width * s, win.Height * s);
                dc.PushOpacity(1 - e);
                Picture(dc, shot, r);
                dc.Pop();
            }, null, null, null);
        }

        // Maximize / un-maximize: the picture grows (or shrinks) to the new frame.
        public static void Zoom(BitmapSource shot, Rect from, Rect to, Action done)
        {
            var area = Rect.Union(from, to);
            Play(area, 230, (dc, t) =>
            {
                double e = 1 - Math.Pow(1 - t, 3);
                var r = new Rect(Lerp(from.Left, to.Left, e), Lerp(from.Top, to.Top, e), Lerp(from.Width, to.Width, e), Lerp(from.Height, to.Height, e));
                r.Offset(-area.Left, -area.Top);
                Picture(dc, shot, r);
            }, null, null, done);
        }

        // An overlay over `area` that redraws every frame for `ms`. The clock starts with the first frame actually
        // on screen, so a slow first render can't eat the start of the effect.
        // `landed` (optional) keeps the last frame up until the real window is back on screen.
        static void Play(Rect area, double ms, Action<DrawingContext, double> draw, Action<double> step, Action ready, Action done, UIElement content = null, Func<bool> landed = null)
        {
            Busy = true;
            // On whole screen pixels, so the last frame lines up with the window exactly (no soft, half-pixel copy).
            double d = App.Dpi;
            double x0 = Math.Floor(area.Left * d) / d, y0 = Math.Floor(area.Top * d) / d;
            area = new Rect(x0, y0, Math.Ceiling(area.Right * d) / d - x0, Math.Ceiling(area.Bottom * d) / d - y0);
            var painter = draw != null ? new Painter { Draw = draw } : null;
            var ghost = new Window
            {
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = false, Background = Brushes.Transparent,
                ShowInTaskbar = false, ShowActivated = false, Topmost = true, WindowStartupLocation = WindowStartupLocation.Manual,
                Left = area.Left, Top = area.Top, Width = Math.Max(1, area.Width), Height = Math.Max(1, area.Height),
                Content = (object)content ?? painter
            };
            IntPtr h = IntPtr.Zero;
            ghost.SourceInitialized += delegate
            {
                h = new WindowInteropHelper(ghost).Handle;
                // A bare popup, see-through wherever nothing is drawn, composited by Windows on the GPU.
                int style = Native.GetWindowLong(h, Native.GWL_STYLE);
                Native.SetWindowLong(h, Native.GWL_STYLE, (style & ~0x00CF0000) | unchecked((int)0x80000000));
                Native.MakeToolWindow(h, true);
                var src = HwndSource.FromHwnd(h);
                src.CompositionTarget.BackgroundColor = Colors.Transparent;
                var m = new MARGINS { l = -1, r = -1, t = -1, b = -1 };
                DwmExtendFrameIntoClientArea(h, ref m);
                int v = 1; Native.DwmSetWindowAttribute(h, 3 /* no transitions */, ref v, 4);
                int corners = 1; Native.DwmSetWindowAttribute(h, 33 /* square corners */, ref corners, 4);
                int border = unchecked((int)0xFFFFFFFE); Native.DwmSetWindowAttribute(h, 34 /* no border */, ref border, 4);
                int cloak = 1; Native.DwmSetWindowAttribute(h, 13, ref cloak, 4);   // hidden until its first frame is drawn
                // Live glass samples the screen behind windows; it must never see this picture (a window would
                // wear a blurred copy of itself as its background).
                Native.SetWindowDisplayAffinity(h, 0x11 /* WDA_EXCLUDEFROMCAPTURE */);
                Native.SetWindowPos(h, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | 0x20 /* FRAMECHANGED */);
            };
            var clock = new System.Diagnostics.Stopwatch();
            int warm = 0;
            bool finished = false;
            EventHandler frame = null;
            Action<double> show = t => { if (painter != null) { painter.T = t; painter.InvalidateVisual(); } if (step != null) step(t); };
            frame = delegate
            {
                if (!clock.IsRunning)
                {
                    show(0);
                    if (++warm < 2) return;          // let the first frame reach the screen
                    int uncloak = 0; Native.DwmSetWindowAttribute(h, 13, ref uncloak, 4);
                    try { if (ready != null) ready(); } catch (Exception ex) { App.Log(ex); }
                    clock.Start();
                    return;
                }
                double t2 = Math.Min(1, clock.Elapsed.TotalMilliseconds / ms);
                show(t2);
                if (t2 < 1 || finished) return;
                finished = true;
                CompositionTarget.Rendering -= frame;
                try { if (done != null) done(); } catch (Exception ex) { App.Log(ex); }
                // a frame for the real window to appear before the picture goes (longer for another app's window)
                var close = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(landed != null ? 16 : 34) };
                var waited = System.Diagnostics.Stopwatch.StartNew();
                close.Tick += delegate
                {
                    bool back = true;
                    try { back = landed == null || landed(); } catch { }
                    if (!back && waited.ElapsedMilliseconds < 600) { close.Interval = TimeSpan.FromMilliseconds(16); return; }
                    if (landed != null && close.Interval.TotalMilliseconds < 30) { close.Interval = TimeSpan.FromMilliseconds(50); return; }   // and painted
                    close.Stop(); ghost.Close(); Busy = false;
                };
                close.Start();
            };
            ghost.Show();
            CompositionTarget.Rendering += frame;
        }

        class Painter : FrameworkElement
        {
            public Action<DrawingContext, double> Draw;
            public double T;
            public Painter() { RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality); }
            protected override void OnRender(DrawingContext dc) { Draw(dc, T); }
        }

        public delegate void Row(double v, out double y, out double left, out double right);

        // The window's picture on a grid of triangles the GPU stretches smoothly: rows down the window, a few
        // columns across so each row's stretch stays even.
        class Mesh
        {
            public readonly Viewport3D View;
            readonly MeshGeometry3D mesh = new MeshGeometry3D();
            readonly int rows, cols;
            readonly Rect area;

            public Mesh(BitmapSource shot, Rect area, int rows, int cols)
            {
                this.rows = rows; this.cols = cols; this.area = area;
                var tex = new PointCollection((rows + 1) * (cols + 1));
                for (int r = 0; r <= rows; r++)
                    for (int c = 0; c <= cols; c++) tex.Add(new Point((double)c / cols, (double)r / rows));
                var idx = new Int32Collection(rows * cols * 6);
                for (int r = 0; r < rows; r++)
                    for (int c = 0; c < cols; c++)
                    {
                        int a = r * (cols + 1) + c, b = a + 1, d = a + cols + 1, e = d + 1;
                        idx.Add(a); idx.Add(d); idx.Add(b);
                        idx.Add(b); idx.Add(d); idx.Add(e);
                    }
                mesh.TextureCoordinates = tex;
                mesh.TriangleIndices = idx;
                var brush = new ImageBrush(shot);
                RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);   // mipmaps: no shimmer as it shrinks
                RenderOptions.SetCachingHint(brush, CachingHint.Cache);
                var material = new DiffuseMaterial(brush);
                var model = new GeometryModel3D(mesh, material) { BackMaterial = material };
                var scene = new Model3DGroup();
                scene.Children.Add(new AmbientLight(Colors.White));
                scene.Children.Add(model);
                // An orthographic camera that maps the scene 1:1 onto the overlay (y flipped: screen y grows down).
                View = new Viewport3D
                {
                    Width = area.Width, Height = area.Height, ClipToBounds = false, IsHitTestVisible = false,
                    Camera = new OrthographicCamera(new Point3D(area.Width / 2, -area.Height / 2, 10), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), area.Width)
                };
                View.Children.Add(new ModelVisual3D { Content = scene });
            }

            public void Bend(Row row)
            {
                var pts = new Point3DCollection((rows + 1) * (cols + 1));
                for (int r = 0; r <= rows; r++)
                {
                    double y, l, rt;
                    row((double)r / rows, out y, out l, out rt);
                    for (int c = 0; c <= cols; c++) pts.Add(new Point3D(Lerp(l, rt, (double)c / cols) - area.Left, -(y - area.Top), 0));
                }
                mesh.Positions = pts;
            }
        }

        // A picture of a window's content at screen resolution.
        public static BitmapSource Snapshot(Window w)
        {
            double d = App.Dpi;
            var shot = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(w.ActualWidth * d)), Math.Max(1, (int)Math.Ceiling(w.ActualHeight * d)), 96 * d, 96 * d, PixelFormats.Pbgra32);
            shot.Render(w);
            shot.Freeze();
            return shot;
        }

        // Where a window really is on screen (DIPs); for a maximized one WPF's Left/Top report the restore spot.
        public static Rect Frame(Window w)
        {
            Native.RECT r;
            var h = new WindowInteropHelper(w).Handle;
            if (h != IntPtr.Zero && Native.DwmGetWindowAttribute(h, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out r, 16) == 0) return DisplayLayout.Dip(r);
            return new Rect(w.Left, w.Top, w.ActualWidth, w.ActualHeight);
        }
    }
}
