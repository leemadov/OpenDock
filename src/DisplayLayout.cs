using System;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;

namespace OpenDock
{
    // Keep native coordinates in physical pixels and convert at each window's own monitor scale.
    static class DisplayLayout
    {
        static System.Collections.Generic.List<Sys.Display> lastScreens;
        public static System.Collections.Generic.List<Sys.Display> Screens()
        {
            var screens = Sys.Screens();
            if (screens.Count > 0) lastScreens = screens;
            return screens.Count > 0 ? screens : lastScreens ?? screens;
        }

        public static Sys.Display Selected
        {
            get
            {
                var screens = Screens();
                return screens.FirstOrDefault(s => s.Device == Settings.DisplayDevice) ?? screens.FirstOrDefault(s => s.Primary) ?? screens.First();
            }
        }

        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int kind, out uint x, out uint y);

        public static double Scale(Window window)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            try { uint dpi = hwnd == IntPtr.Zero ? 0 : GetDpiForWindow(hwnd); if (dpi > 0) return dpi / 96.0; } catch (EntryPointNotFoundException) { }
            return App.Dpi;
        }

        public static double MonitorScale(Sys.Display screen)
        {
            try { uint x, y; if (GetDpiForMonitor(screen.Monitor, 0, out x, out y) == 0 && x > 0) return x / 96.0; } catch (EntryPointNotFoundException) { }
            return App.Dpi;
        }

        public static Rect Pixels(Native.RECT r) { return new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top); }
        public static bool CoversScreen(Native.RECT screen, Native.RECT window, bool maximized, bool caption)
        {
            return !(maximized && caption) && window.Left <= screen.Left && window.Top <= screen.Top && window.Right >= screen.Right && window.Bottom >= screen.Bottom;
        }
        public static Rect WindowPixels(Window window)
        {
            Native.RECT r;
            return Native.GetWindowRect(new WindowInteropHelper(window).Handle, out r) ? Pixels(r) : Rect.Empty;
        }
        public static Rect Dip(Native.RECT r) { return Dip(r, App.Dpi); }
        public static Rect Dip(Native.RECT r, double scale)
        {
            return new Rect(r.Left / scale, r.Top / scale, (r.Right - r.Left) / scale, (r.Bottom - r.Top) / scale);
        }
        public static Point Local(Window window, Native.POINT point)
        {
            var bounds = WindowPixels(window); double scale = Scale(window);
            return new Point((point.X - bounds.Left) / scale, (point.Y - bounds.Top) / scale);
        }
        public static void Place(Window window, Rect pixels)
        {
            double scale = Scale(window);
            window.Width = pixels.Width / scale; window.Height = pixels.Height / scale;
            window.Left = pixels.Left / scale; window.Top = pixels.Top / scale;
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero) Native.SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(pixels.Left), (int)Math.Round(pixels.Top), (int)Math.Round(pixels.Width), (int)Math.Round(pixels.Height), 4 | Native.SWP_NOACTIVATE);
        }

        public static Sys.Display ForDevice(string device)
        {
            return Screens().FirstOrDefault(s => s.Device == device) ?? Selected;
        }

        public static Sys.Display Active { get { return App.Dock == null ? Selected : App.Dock.Screen; } }
        public static Rect ActiveBounds { get { var screen = Active; return Dip(screen.Bounds, MonitorScale(screen)); } }
        public static Rect ActiveWorkArea { get { var screen = Active; return Dip(screen.Work, MonitorScale(screen)); } }

        public static Sys.Display AtPixel(Point point)
        {
            return Screens().FirstOrDefault(s => Pixels(s.Bounds).Contains(point)) ?? Selected;
        }
        public static void Center(Window window, Sys.Display screen, double offset = 0)
        {
            double scale = MonitorScale(screen); var area = Pixels(screen.Work);
            double width = Math.Min(window.Width * scale, area.Width), height = Math.Min(window.Height * scale, area.Height);
            var at = Clamp(new Point(area.Left + (area.Width - width) / 2 + offset * scale, area.Top + (area.Height - height) / 2 + offset * scale), new Size(width, height), area);
            var target = new Rect(at, new Size(width, height));
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = target.Left / App.Dpi; window.Top = target.Top / App.Dpi;
            window.SourceInitialized += delegate { Place(window, target); };
        }

        public static Rect Bounds { get { return Dip(Selected.Bounds); } }
        public static Rect WorkArea { get { return Dip(Selected.Work); } }

        public static Point Clamp(Point p, Size size, Rect area)
        {
            return new Point(Math.Max(area.Left, Math.Min(area.Right - size.Width, p.X)),
                             Math.Max(area.Top, Math.Min(area.Bottom - size.Height, p.Y)));
        }

        // A dock is laid out along X, with its screen edge at Y=thickness. Rotate the coordinates,
        // not the visuals: icons, labels, menus and drag ghosts always remain upright.
        public static Point ToWindow(Point p, int position, double length, double thickness)
        {
            return position == 1 ? new Point(thickness - p.Y, p.X)
                 : position == 2 ? new Point(p.Y, p.X) : p;
        }

        public static Point FromWindow(Point p, int position, double length, double thickness)
        {
            return position == 1 ? new Point(p.Y, thickness - p.X)
                 : position == 2 ? new Point(p.Y, p.X) : p;
        }

        public static double FitScale(double length, double icons, double expansion)
        {
            return Math.Min(1, Math.Max(1, length - 24) / Math.Max(1, icons + expansion));
        }

        public static Point Beside(Point anchor, Size size, int position, Rect area)
        {
            var p = position == 1 ? new Point(anchor.X + 10, anchor.Y - size.Height / 2)
                  : position == 2 ? new Point(anchor.X - size.Width - 10, anchor.Y - size.Height / 2)
                  : new Point(anchor.X - size.Width / 2, anchor.Y - size.Height - 10);
            return Clamp(p, size, area);
        }
    }
}
