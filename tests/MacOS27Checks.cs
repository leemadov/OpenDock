using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace OpenDock
{
    // The macOS 27 features' arithmetic: the Liquid Glass slider, tiling, Mission Control's grid, the Genie path.
    static class MacOS27Checks
    {
        static void Need(bool ok, string what) { if (!ok) throw new Exception(what); }
        static bool Near(double a, double b) { return Math.Abs(a - b) < 0.6; }

        public static void Run()
        {
            // Liquid Glass: 50 keeps the standard tint, 0 is nearly clear, 100 nearly solid, and it only ever rises.
            Need(Theme.GlassAlpha(0x99, 50) == 0x99, "glass slider changes the standard look");
            Need(Theme.GlassAlpha(0x99, 0) < 0x20, "clear glass is still tinted");
            Need(Theme.GlassAlpha(0x99, 100) >= 240, "tinted glass lets too much through");
            Need(Theme.GlassAlpha(0xF7, 100) == 0xF7, "tinted glass made an opaque tint lighter");
            for (int t = 0, last = -1; t <= 100; t += 5) { int a = Theme.GlassAlpha(0x60, t); Need(a >= last, "glass slider is not monotonic"); last = a; }

            // Tiling: halves meet with one gap between them, everything stays inside the margins.
            var work = new Rect(0, 30, 1920, 1000);
            var left = Tiling.Area(work, Tiling.Spot.Left, 8, new Size(800, 600));
            var right = Tiling.Area(work, Tiling.Spot.Right, 8, new Size(800, 600));
            Need(Near(left.Left, 8) && Near(left.Top, 38) && Near(left.Bottom, 1022), "left half misplaced");
            Need(Near(right.Left - left.Right, 8) && Near(right.Right, 1912), "halves don't meet at one gap");
            Need(Near(left.Width, right.Width), "halves differ in size");
            var br = Tiling.Area(work, Tiling.Spot.BottomRight, 8, new Size(800, 600));
            Need(Near(br.Right, 1912) && Near(br.Bottom, 1022) && Near(br.Width, left.Width), "bottom-right quarter misplaced");
            var center = Tiling.Area(work, Tiling.Spot.Center, 8, new Size(800, 600));
            Need(Near(center.Left, 560) && Near(center.Top, 230) && Near(center.Width, 800), "centring changed the size or missed the middle");
            var big = Tiling.Area(work, Tiling.Spot.Center, 8, new Size(5000, 5000));
            Need(big.Width <= 1904 && big.Height <= 984, "a huge window centred off the screen");

            // Mission Control: no overlaps, inside the area, proportions kept, never enlarged.
            var area = new Rect(60, 90, 1800, 870);
            var sizes = new List<Size> { new Size(1200, 800), new Size(800, 600), new Size(1600, 900), new Size(500, 700), new Size(300, 200) };
            var grid = MissionControl.Layout(sizes, area, 36);
            Need(grid.Count == sizes.Count, "a window went missing from Mission Control");
            for (int i = 0; i < grid.Count; i++)
            {
                var r = grid[i];
                Need(r.Left >= area.Left - 0.5 && r.Right <= area.Right + 0.5 && r.Top >= area.Top - 0.5 && r.Bottom <= area.Bottom + 0.5, "a window spills out of Mission Control");
                Need(Math.Abs(r.Width / r.Height - sizes[i].Width / sizes[i].Height) < 0.01, "Mission Control squashed a window");
                Need(r.Width <= sizes[i].Width + 0.5, "Mission Control enlarged a window");
                for (int j = i + 1; j < grid.Count; j++) { var o = Rect.Intersect(r, grid[j]); Need(o.IsEmpty || o.Width * o.Height < 1, "Mission Control windows overlap"); }
            }
            var one = MissionControl.Layout(new List<Size> { new Size(800, 600) }, area, 36);
            Need(Near(one[0].Width, 800) && Near(one[0].Left + 400, area.Left + area.Width / 2), "a single window isn't centred at its own size");

            // Genie: starts as the window, ends inside the icon, and the bottom pinches toward the icon first.
            var win = new Rect(400, 200, 800, 600); var icon = new Rect(900, 1000, 60, 60);
            var s0 = WindowFx.GenieSlice(win, icon, 0, 0, 1);
            Need(Near(s0.Left, win.Left) && Near(s0.Top, win.Top) && Near(s0.Width, win.Width) && Near(s0.Height, win.Height), "genie doesn't start at the window");
            var s1 = WindowFx.GenieSlice(win, icon, 1, 0.9, 1);
            Need(s1.Left >= icon.Left - 0.5 && s1.Right <= icon.Right + 0.5 && s1.Top >= icon.Top - 0.5 && s1.Bottom <= icon.Bottom + 0.5, "genie doesn't end in the icon");
            var topRow = WindowFx.GenieSlice(win, icon, 0.4, 0, 0.05); var bottomRow = WindowFx.GenieSlice(win, icon, 0.4, 0.95, 1);
            Need(bottomRow.Width < topRow.Width, "genie pinches the wrong end");

            Console.WriteLine("macOS 27: Liquid Glass slider, tiling, Mission Control layout and Genie path passed");
        }
    }
}
