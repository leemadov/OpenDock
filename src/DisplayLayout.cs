using System;
using System.Linq;
using System.Windows;

namespace OpenDock
{
    // Screen coordinates stay in Windows' system-DPI coordinate space, just like WPF in this app.
    static class DisplayLayout
    {
        // During sleep, a driver reset or a monitor swap Windows can briefly report no displays: keep using the
        // last one we saw (or the primary screen's size) instead of throwing.
        static Sys.Display last;
        public static Sys.Display Selected
        {
            get
            {
                var screens = Sys.Screens();
                if (screens.Count == 0) return last ?? Fallback();
                return last = screens.FirstOrDefault(s => s.Device == Settings.DisplayDevice) ?? screens.FirstOrDefault(s => s.Primary) ?? screens[0];
            }
        }

        static Sys.Display Fallback()
        {
            int w = (int)Math.Round(SystemParameters.PrimaryScreenWidth * App.Dpi), h = (int)Math.Round(SystemParameters.PrimaryScreenHeight * App.Dpi);
            var r = new Native.RECT { Left = 0, Top = 0, Right = w, Bottom = h };
            return new Sys.Display { Device = "", Primary = true, Width = w, Height = h, Bounds = r, Work = r };
        }

        public static Rect Dip(Native.RECT r)
        {
            return new Rect(r.Left / App.Dpi, r.Top / App.Dpi, (r.Right - r.Left) / App.Dpi, (r.Bottom - r.Top) / App.Dpi);
        }

        public static Sys.Display ForDevice(string device)
        {
            return Sys.Screens().FirstOrDefault(s => s.Device == device) ?? Selected;
        }

        public static Sys.Display Active { get { return App.Dock == null ? Selected : App.Dock.Screen; } }
        public static Rect ActiveBounds { get { return Dip(Active.Bounds); } }
        public static Rect ActiveWorkArea { get { return Dip(Active.Work); } }

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
