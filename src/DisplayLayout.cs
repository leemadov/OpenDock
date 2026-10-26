using System;
using System.Linq;
using System.Windows;

namespace OpenDock
{
    // Screen coordinates stay in Windows' system-DPI coordinate space, just like WPF in this app.
    static class DisplayLayout
    {
        public static Sys.Display Selected
        {
            get
            {
                var screens = Sys.Screens();
                return screens.FirstOrDefault(s => s.Device == Settings.DisplayDevice) ?? screens.First(s => s.Primary);
            }
        }

        public static Rect Dip(Native.RECT r)
        {
            return new Rect(r.Left / App.Dpi, r.Top / App.Dpi, (r.Right - r.Left) / App.Dpi, (r.Bottom - r.Top) / App.Dpi);
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
