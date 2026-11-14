using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace OpenDock
{
    // Arrange Recent Windows: the windows you've been jumping between most, brought back (minimized ones too) and
    // laid out side by side -- inside a margin that leaves the wallpaper showing all round, and never smaller than a
    // usable size. If they won't all fit at that size, fewer of them are shown rather than any getting cramped.
    static class RecentArrange
    {
        const int MaxWindows = 4;
        const double HalfLifeMinutes = 4;                 // a switch 4 minutes ago counts half as much as one just now
        const double MinWidth = 640, MinHeight = 440;     // DIPs: still usable (a page of text, a toolbar, a sidebar)
        static readonly Dictionary<IntPtr, List<DateTime>> switches = new Dictionary<IntPtr, List<DateTime>>();
        static IntPtr last;

        public static void Start()
        {
            App.ForegroundChanged += h =>
            {
                h = Native.GetAncestor(h, 2);
                if (h == IntPtr.Zero || h == last || !App.Wins.Any(w => w.Hwnd == h && w.InternalKind == null)) return;
                last = h;
                List<DateTime> times;
                if (!switches.TryGetValue(h, out times)) switches[h] = times = new List<DateTime>();
                times.Add(DateTime.UtcNow);
                Prune();
            };
        }

        static void Prune()
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-30);
            foreach (var h in switches.Keys.ToList())
            {
                switches[h].RemoveAll(t => t < cutoff);
                if (switches[h].Count == 0 || !Native.IsWindow(h)) switches.Remove(h);
            }
        }

        static double Score(IntPtr h)
        {
            var now = DateTime.UtcNow;
            return switches[h].Sum(t => Math.Pow(0.5, (now - t).TotalMinutes / HalfLifeMinutes));
        }

        // The most-used windows first; ones that are gone or can't be moved are skipped.
        public static List<IntPtr> Ranked()
        {
            Prune();
            return switches.Keys.Where(h => Native.IsWindow(h) && App.Wins.Any(w => w.Hwnd == h))
                .OrderByDescending(Score).ToList();
        }

        public static void Arrange()
        {
            if (LockScreen.Active) return;
            var ranked = Ranked();
            if (ranked.Count == 0) { var f = Tiling.Front(); if (f != IntPtr.Zero) ranked.Add(f); else return; }

            // on the display you're working on
            var front = Tiling.Front();
            var screen = Sys.Screens().FirstOrDefault(d => d.Monitor == Native.MonitorFromWindow(front != IntPtr.Zero ? front : ranked[0], 2)) ?? DisplayLayout.Selected;
            var work = new Rect(screen.Work.Left, screen.Work.Top, screen.Work.Right - screen.Work.Left, screen.Work.Bottom - screen.Work.Top);

            int n = Math.Min(MaxWindows, ranked.Count);
            List<Rect> cells = null;
            for (; n >= 1; n--)
            {
                cells = Layout(work, n, App.Dpi);
                if (cells.All(c => c.Width >= MinWidth * App.Dpi && c.Height >= MinHeight * App.Dpi)) break;
            }
            if (n < 1) { n = 1; cells = Layout(work, 1, App.Dpi); }   // a tiny screen: one window, as big as the margins allow

            var chosen = ranked.Take(n).ToList();
            for (int i = 0; i < n; i++)
            {
                var h = chosen[i]; var c = cells[i];
                Native.RECT now;
                if (Native.GetWindowRect(h, out now) && !Native.IsZoomed(h) && !Native.IsIconic(h)) Tiling.Remember(h, now);   // Ctrl+Alt+Backspace puts it back
                Tiling.PlaceTile(h, new Native.RECT { Left = (int)Math.Round(c.Left), Top = (int)Math.Round(c.Top), Right = (int)Math.Round(c.Right), Bottom = (int)Math.Round(c.Bottom) });
            }
            // all of them in front, the one you use most on top (once the restores have happened)
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            t.Tick += delegate
            {
                t.Stop();
                for (int i = chosen.Count - 1; i >= 1; i--) Native.SetWindowPos(chosen[i], IntPtr.Zero, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | 0x4000 /* ASYNC */);
                Native.Activate(chosen[0]);
            };
            t.Start();
        }

        // The cells, inside a margin of wallpaper (about 6% of the screen on every side, at least 40 px) with gaps
        // between: one window centered; two side by side; three as one big on the left and two stacked on the right;
        // four in a grid. The most-used window gets the first (biggest) cell.
        public static List<Rect> Layout(Rect work, int n, double dpi)
        {
            double mx = Math.Max(40 * dpi, work.Width * 0.06), my = Math.Max(40 * dpi, work.Height * 0.06), gap = 16 * dpi;
            var a = new Rect(work.Left + mx, work.Top + my, Math.Max(1, work.Width - 2 * mx), Math.Max(1, work.Height - 2 * my));
            var list = new List<Rect>();
            if (n <= 1)
            {
                double w = a.Width * 0.82, h = a.Height;
                list.Add(new Rect(a.Left + (a.Width - w) / 2, a.Top, w, h));
            }
            else if (n == 2)
            {
                double w = (a.Width - gap) / 2;
                list.Add(new Rect(a.Left, a.Top, w, a.Height));
                list.Add(new Rect(a.Left + w + gap, a.Top, w, a.Height));
            }
            else if (n == 3)
            {
                double big = (a.Width - gap) * 0.56, small = a.Width - gap - big, h = (a.Height - gap) / 2;
                list.Add(new Rect(a.Left, a.Top, big, a.Height));
                list.Add(new Rect(a.Left + big + gap, a.Top, small, h));
                list.Add(new Rect(a.Left + big + gap, a.Top + h + gap, small, h));
            }
            else
            {
                double w = (a.Width - gap) / 2, h = (a.Height - gap) / 2;
                list.Add(new Rect(a.Left, a.Top, w, h));
                list.Add(new Rect(a.Left + w + gap, a.Top, w, h));
                list.Add(new Rect(a.Left, a.Top + h + gap, w, h));
                list.Add(new Rect(a.Left + w + gap, a.Top + h + gap, w, h));
            }
            return list;
        }
    }
}
