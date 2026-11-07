using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace OpenDock
{
    // Mac-style screenshots: Ctrl+Shift+3 takes the screen, Ctrl+Shift+4 the area you drag, Ctrl+Shift+5 shows a
    // toolbar (screen, a window, an area). The picture is saved to the Desktop ("Screenshot 2026-11-07 at 9.41.00.png")
    // and a thumbnail floats in the bottom right for a few seconds: click it to open the picture, drag it into an app.
    //
    // OpenDock's own glass is normally kept out of screen capture (Live glass); for the instant of a screenshot it's
    // let back in, so the Dock and the menu bar are in the picture as they are on a Mac.
    static class Screenshots
    {
        [DllImport("user32.dll")] static extern bool GetWindowDisplayAffinity(IntPtr h, out uint affinity);
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);

        public static void Screen()
        {
            var s = ScreenAtCursor();
            Shoot(() => Grab(s.Bounds.Left, s.Bounds.Top, s.Bounds.Right - s.Bounds.Left, s.Bounds.Bottom - s.Bounds.Top));
        }

        public static void Area() { new ShotOverlay(false).Begin(); }
        public static void Window() { new ShotOverlay(true).Begin(); }
        public static void Toolbar() { ShotToolbar.Toggle(); }

        internal static Sys.Display ScreenAtCursor()
        {
            Native.POINT p; Native.GetCursorPos(out p);
            return Sys.Screens().FirstOrDefault(d => p.X >= d.Bounds.Left && p.X < d.Bounds.Right && p.Y >= d.Bounds.Top && p.Y < d.Bounds.Bottom) ?? DisplayLayout.Selected;
        }

        // Takes the picture with OpenDock's glass in it, then saves it and shows the thumbnail.
        internal static void Shoot(Func<BitmapSource> take)
        {
            var lifted = new List<IntPtr>();
            foreach (Window w in Application.Current.Windows)
            {
                if (!w.IsVisible || w is ShotOverlay || w is ShotToolbar || w is ShotThumb) continue;
                var h = new WindowInteropHelper(w).Handle; uint a;
                if (h != IntPtr.Zero && GetWindowDisplayAffinity(h, out a) && a != 0 && Native.SetWindowDisplayAffinity(h, 0)) lifted.Add(h);
            }
            // a moment for the compositor to draw them into the screen's picture again
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(lifted.Count > 0 ? 90 : 10) };
            t.Tick += delegate
            {
                t.Stop();
                BitmapSource img = null;
                try { img = take(); } catch (Exception ex) { App.Log(ex); }
                foreach (var h in lifted) Glass.Exclude(h);
                if (img != null) Save(img);
            };
            t.Start();
        }

        internal static BitmapSource Grab(int x, int y, int w, int h) { return w < 2 || h < 2 ? null : Icons.CaptureScreen(x, y, w, h); }

        // One window, as it draws itself (even where others cover it), cut to its visible frame.
        internal static BitmapSource GrabWindow(IntPtr h)
        {
            Native.RECT wr, fr;
            if (!Native.GetWindowRect(h, out wr)) return null;
            if (Native.DwmGetWindowAttribute(h, 9, out fr, 16) != 0) fr = wr;
            int w = wr.Right - wr.Left, ht = wr.Bottom - wr.Top;
            if (w < 2 || ht < 2) return null;
            IntPtr screen = Native.GetDC(IntPtr.Zero), dc = Native.CreateCompatibleDC(screen), bmp = Native.CreateCompatibleBitmap(screen, w, ht);
            var old = Native.SelectObject(dc, bmp);
            try
            {
                if (!PrintWindow(h, dc, 2 /* PW_RENDERFULLCONTENT */)) return Grab(fr.Left, fr.Top, fr.Right - fr.Left, fr.Bottom - fr.Top);
                Native.SelectObject(dc, old);
                var crop = new Int32Rect(Math.Max(0, fr.Left - wr.Left), Math.Max(0, fr.Top - wr.Top), Math.Min(w, fr.Right - fr.Left), Math.Min(ht, fr.Bottom - fr.Top));
                if (crop.X + crop.Width > w) crop.Width = w - crop.X;
                if (crop.Y + crop.Height > ht) crop.Height = ht - crop.Y;
                var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, crop, BitmapSizeOptions.FromEmptyOptions());
                var img = new FormatConvertedBitmap(src, PixelFormats.Bgr32, null, 0); img.Freeze();
                return img;
            }
            finally { Native.SelectObject(dc, old); Native.DeleteObject(bmp); Native.DeleteDC(dc); Native.ReleaseDC(IntPtr.Zero, screen); }
        }

        static void Save(BitmapSource img)
        {
            try
            {
                string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                var now = DateTime.Now;
                string file = System.IO.Path.Combine(desk, "Screenshot " + now.ToString("yyyy-MM-dd") + " at " + now.ToString("H.mm.ss") + ".png");
                for (int n = 2; File.Exists(file); n++) file = System.IO.Path.Combine(desk, "Screenshot " + now.ToString("yyyy-MM-dd") + " at " + now.ToString("H.mm.ss") + " (" + n + ").png");
                var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(img));
                using (var fs = File.Create(file)) enc.Save(fs);
                ShotThumb.Show(img, file);
            }
            catch (Exception ex) { App.Log(ex); }
        }
    }

    // The overlay for picking an area (drag) or a window (click), over every display. Esc cancels.
    class ShotOverlay : Window
    {
        readonly bool windowMode;
        readonly Canvas canvas = new Canvas { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };
        readonly System.Windows.Shapes.Path dim = new System.Windows.Shapes.Path { Fill = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), IsHitTestVisible = false };
        readonly Rectangle box = new Rectangle { Stroke = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 }, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        readonly Border size = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 2, 7, 3), Background = new SolidColorBrush(Color.FromArgb(190, 20, 20, 24)), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        readonly TextBlock sizeText = new TextBlock { Foreground = Brushes.White, FontSize = 12 };
        readonly Rect all;
        Point? start;
        IntPtr hot;

        public ShotOverlay(bool windowMode)
        {
            this.windowMode = windowMode;
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize;
            FontFamily = Theme.Font; Title = "Screenshot";
            // all the displays at once
            var scr = Sys.Screens();
            int l = scr.Min(s => s.Bounds.Left), t = scr.Min(s => s.Bounds.Top), r = scr.Max(s => s.Bounds.Right), b = scr.Max(s => s.Bounds.Bottom);
            all = new Rect(l, t, r - l, b - t);
            var dip = DisplayLayout.Dip(new Native.RECT { Left = l, Top = t, Right = r, Bottom = b });
            Left = dip.Left; Top = dip.Top; Width = dip.Width; Height = dip.Height;
            size.Child = sizeText;
            canvas.Children.Add(dim); canvas.Children.Add(box); canvas.Children.Add(size);
            Content = canvas;
            Cursor = windowMode ? Cursors.Hand : Cursors.Cross;
            dim.Data = new RectangleGeometry(new Rect(0, 0, Width, Height));
            if (windowMode) dim.Visibility = Visibility.Collapsed;
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
            MouseLeftButtonDown += (s, e) => { if (!windowMode) { start = e.GetPosition(canvas); CaptureMouse(); } };
            MouseMove += (s, e) => { if (windowMode) Hover(); else if (start != null) Drag(e.GetPosition(canvas)); };
            MouseLeftButtonUp += (s, e) => Finish(e.GetPosition(canvas));
            MouseRightButtonUp += delegate { Close(); };
            SourceInitialized += delegate { Native.MakeToolWindow(new WindowInteropHelper(this).Handle, false); };
        }

        public void Begin() { Show(); Native.Activate(new WindowInteropHelper(this).Handle); Focus(); if (windowMode) Hover(); }

        void Drag(Point p)
        {
            var r = new Rect(start.Value, p);
            dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, Width, Height)), new RectangleGeometry(r));
            box.Visibility = Visibility.Visible; Canvas.SetLeft(box, r.X); Canvas.SetTop(box, r.Y); box.Width = r.Width; box.Height = r.Height;
            sizeText.Text = Math.Round(r.Width * App.Dpi) + " × " + Math.Round(r.Height * App.Dpi);
            size.Visibility = Visibility.Visible; Canvas.SetLeft(size, r.Right + 8); Canvas.SetTop(size, r.Bottom + 6);
        }

        // Window mode: the window under the pointer gets a blue wash, as on a Mac.
        void Hover()
        {
            Native.POINT p; Native.GetCursorPos(out p);
            var h = WindowAt(p);
            hot = h;
            Native.RECT fr;
            if (h == IntPtr.Zero || Native.DwmGetWindowAttribute(h, 9, out fr, 16) != 0) { box.Visibility = Visibility.Collapsed; return; }
            double d = App.Dpi;
            box.Visibility = Visibility.Visible; box.StrokeDashArray = null; box.Stroke = null;
            box.Fill = new SolidColorBrush(Color.FromArgb(70, 0x0A, 0x84, 0xFF));
            Canvas.SetLeft(box, (fr.Left - all.X) / d); Canvas.SetTop(box, (fr.Top - all.Y) / d); box.Width = (fr.Right - fr.Left) / d; box.Height = (fr.Bottom - fr.Top) / d;
        }

        // The front-most app window at a screen point (not this overlay, not the desktop).
        IntPtr WindowAt(Native.POINT p)
        {
            var me = new WindowInteropHelper(this).Handle; IntPtr found = IntPtr.Zero;
            Native.EnumWindows((h, l) =>
            {
                if (h == me || !Native.IsWindowVisible(h) || Native.IsIconic(h) || Native.IsCloaked(h) || Native.GetWindowTextLength(h) == 0) return true;
                int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
                if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return true;
                string cls = Native.ClassName(h); if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd") return true;
                Native.RECT r; if (!Native.GetWindowRect(h, out r)) return true;
                if (p.X < r.Left || p.X >= r.Right || p.Y < r.Top || p.Y >= r.Bottom) return true;
                found = h; return false;
            }, IntPtr.Zero);
            return found;
        }

        void Finish(Point p)
        {
            ReleaseMouseCapture();
            if (windowMode)
            {
                var h = hot; Close();
                if (h != IntPtr.Zero) Screenshots.Shoot(() => Screenshots.GrabWindow(h));
                return;
            }
            if (start == null) return;
            var r = new Rect(start.Value, p); start = null;
            Close();
            if (r.Width < 3 || r.Height < 3) return;
            double d = App.Dpi;
            int x = (int)Math.Round(all.X + r.X * d), y = (int)Math.Round(all.Y + r.Y * d), w = (int)Math.Round(r.Width * d), ht = (int)Math.Round(r.Height * d);
            Screenshots.Shoot(() => Screenshots.Grab(x, y, w, ht));
        }
    }

    // Ctrl+Shift+5: the toolbar at the bottom of the screen.
    class ShotToolbar : Window
    {
        static ShotToolbar open;
        public static void Toggle() { if (open != null) { open.Close(); return; } open = new ShotToolbar(); open.Show(); Native.Activate(new WindowInteropHelper(open).Handle); }   // stays until Esc, a choice, or Ctrl+Shift+5 again

        ShotToolbar()
        {
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight; FontFamily = Theme.Font; Title = "Screenshot";
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8) };
            row.Children.Add(Tool("", "Close", Close));
            row.Children.Add(new Border { Width = 1, Margin = new Thickness(6, 8, 6, 8), Background = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)) });
            row.Children.Add(Tool("", "Capture Entire Screen", () => { Close(); Screenshots.Screen(); }));
            row.Children.Add(Tool("", "Capture Selected Window", () => { Close(); Screenshots.Window(); }));
            row.Children.Add(Tool("", "Capture Selected Portion", () => { Close(); Screenshots.Area(); }));
            var pill = new Border { Child = row, CornerRadius = new CornerRadius(16), Margin = new Thickness(24),
                Background = new SolidColorBrush(Color.FromArgb(215, 30, 30, 34)), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
                Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.4 } };
            Content = pill;
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
            Closed += delegate { if (open == this) open = null; };
            Loaded += delegate
            {
                var b = DisplayLayout.Dip(Screenshots.ScreenAtCursor().Bounds);
                Left = b.Left + (b.Width - ActualWidth) / 2; Top = b.Bottom - ActualHeight - 110;
            };
            SourceInitialized += delegate { Native.MakeToolWindow(new WindowInteropHelper(this).Handle, false); };
        }

        static FrameworkElement Tool(string glyph, string tip, Action click)
        {
            var b = new Border { Width = 46, Height = 40, CornerRadius = new CornerRadius(10), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = tip, Margin = new Thickness(2, 0, 2, 0) };
            b.Child = new TextBlock { Text = glyph, FontFamily = Theme.Glyphs, FontSize = 19, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            b.MouseEnter += delegate { b.Background = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)); };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; };
            b.MouseLeftButtonUp += (s, e) => { e.Handled = true; click(); };
            return b;
        }
    }

    // The thumbnail that floats in after a screenshot: click to open, drag into an app, or let it slide away.
    class ShotThumb : Window
    {
        static ShotThumb last;
        readonly string file;
        readonly DispatcherTimer away = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        Point down; bool pressed;

        public static void Show(BitmapSource img, string file)
        {
            if (last != null) last.Close();
            last = new ShotThumb(img, file); last.Show();
        }

        ShotThumb(BitmapSource img, string file)
        {
            this.file = file;
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false; ShowActivated = false; ResizeMode = ResizeMode.NoResize;
            double w = 220, h = Math.Max(60, Math.Min(260, w * img.PixelHeight / Math.Max(1, img.PixelWidth)));
            var pic = new Border { Width = w, Height = h, CornerRadius = new CornerRadius(8), Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill }, BorderThickness = new Thickness(1.5),
                BorderBrush = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), Margin = new Thickness(20), Cursor = Cursors.Hand,
                Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 5, Direction = 270, Opacity = 0.4 }, ToolTip = System.IO.Path.GetFileName(file) };
            Content = pic; Width = w + 40; Height = h + 40;
            var b = DisplayLayout.Dip(Screenshots.ScreenAtCursor().Bounds);
            Left = b.Right - Width - 12; Top = b.Bottom - Height - 110;
            var slide = new TranslateTransform(Width, 0); pic.RenderTransform = slide;
            Loaded += delegate { slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(280)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } }); away.Start(); };
            away.Tick += delegate { away.Stop(); Leave(); };
            pic.MouseEnter += delegate { away.Stop(); };
            pic.MouseLeave += delegate { if (!pressed) away.Start(); };
            pic.MouseLeftButtonDown += (s, e) => { pressed = true; down = e.GetPosition(this); };
            pic.MouseMove += (s, e) =>
            {
                if (!pressed || e.LeftButton != MouseButtonState.Pressed) return;
                if ((e.GetPosition(this) - down).Length < 6) return;
                pressed = false;
                // drag the picture into Mail, a chat, a folder...
                DragDrop.DoDragDrop(pic, new DataObject(DataFormats.FileDrop, new[] { file }), DragDropEffects.Copy | DragDropEffects.Move);
                Leave();
            };
            pic.MouseLeftButtonUp += delegate { if (!pressed) return; pressed = false; Launcher.Open(file); Leave(); };
            Closed += delegate { if (last == this) last = null; };
            SourceInitialized += delegate { Native.MakeToolWindow(new WindowInteropHelper(this).Handle, true); };
        }

        void Leave()
        {
            var t = (TranslateTransform)((FrameworkElement)Content).RenderTransform;
            var a = new DoubleAnimation(Width, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            a.Completed += delegate { Close(); };
            t.BeginAnimation(TranslateTransform.XProperty, a);
        }
    }
}
