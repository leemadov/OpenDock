using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace OpenDock
{
    // Global shortcuts that belong to OpenDock (each with its own id on the hidden host window).
    static class Hotkeys
    {
        public static IntPtr Host;
        static readonly Dictionary<int, Action> actions = new Dictionary<int, Action>();
        const uint Ctrl = 0x2, Alt = 0x1, Shift = 0x4, NoRepeat = 0x4000;

        public static bool Handle(int id)
        {
            Action a;
            if (!actions.TryGetValue(id, out a)) return false;
            if (LockScreen.Active) return true;   // nothing gets past the lock screen: no Mission Control thumbnails, no screenshots
            try { a(); } catch (Exception ex) { App.Log(ex); }
            return true;
        }

        static void Set(int id, bool on, uint mods, uint vk, Action a)
        {
            if (Host == IntPtr.Zero) return;
            bool had = actions.ContainsKey(id);
            if (on && !had && Native.RegisterHotKey(Host, id, mods | NoRepeat, vk)) actions[id] = a;
            else if (!on && had) { Native.UnregisterHotKey(Host, id); actions.Remove(id); }
        }

        // Ctrl+Alt+← / → halves, Ctrl+Alt+Enter fills, Ctrl+Alt+Backspace puts it back; Ctrl+Alt+↑ is Mission
        // Control and Ctrl+Alt+↓ the front app's windows (macOS uses Ctrl+↑ / Ctrl+↓, which Windows apps need).
        public static void Sync()
        {
            bool on = Settings.TilingKeys;
            Set(10, on, Ctrl | Alt, 0x25, () => Tiling.Move(Tiling.Front(), Tiling.Spot.Left));
            Set(11, on, Ctrl | Alt, 0x27, () => Tiling.Move(Tiling.Front(), Tiling.Spot.Right));
            Set(12, on, Ctrl | Alt, 0x0D, () => Tiling.Move(Tiling.Front(), Tiling.Spot.Fill));
            Set(13, on, Ctrl | Alt, 0x08, () => Tiling.Move(Tiling.Front(), Tiling.Spot.Restore));
            Set(14, on, Ctrl | Alt, 0x26, () => MissionControl.Toggle(IntPtr.Zero));
            Set(15, on, Ctrl | Alt, 0x28, () => MissionControl.Toggle(Tiling.Front()));
            // Alt+` cycles the front app's windows (Cmd+` on a Mac); Alt+Shift+` goes the other way.
            Set(16, Settings.CycleWindowsKey, Alt, 0xC0, () => WindowCycle.Next(false));
            Set(17, Settings.CycleWindowsKey, Alt | Shift, 0xC0, () => WindowCycle.Next(true));
            // Mac screenshots (Cmd+Shift+3/4/5 there): the screen, an area, the toolbar
            Set(18, Settings.ScreenshotKeys, Ctrl | Shift, 0x33, Screenshots.Screen);
            Set(19, Settings.ScreenshotKeys, Ctrl | Shift, 0x34, Screenshots.Area);
            Set(20, Settings.ScreenshotKeys, Ctrl | Shift, 0x35, Screenshots.Toolbar);
        }
    }

    // macOS window tiling (Window > Move & Resize), for any app's window.
    static class Tiling
    {
        public enum Spot { Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight, Fill, Center, Restore }
        // macOS leaves a margin around tiled windows ("Tiled windows have margins" in Desktop & Dock)
        public static double Gap { get { return Settings.TileMargins ? 8 : 0; } }
        static readonly Dictionary<IntPtr, Native.RECT> before = new Dictionary<IntPtr, Native.RECT>();
        static readonly Dictionary<IntPtr, Native.RECT> tiled = new Dictionary<IntPtr, Native.RECT>();   // the visible frame each tiled window was given

        public static readonly Spot[] MoveSpots = { Spot.Left, Spot.Right, Spot.Top, Spot.Bottom, Spot.TopLeft, Spot.TopRight, Spot.BottomLeft, Spot.BottomRight };
        public static string Name(Spot s)
        {
            switch (s)
            {
                case Spot.TopLeft: return "Top Left"; case Spot.TopRight: return "Top Right";
                case Spot.BottomLeft: return "Bottom Left"; case Spot.BottomRight: return "Bottom Right";
                case Spot.Restore: return "Return to Previous Size";
                default: return s.ToString();
            }
        }

        // The window you're working in (not OpenDock's bars and panels).
        public static IntPtr Front()
        {
            var h = Native.GetAncestor(Native.GetForegroundWindow(), 2);
            if (h != IntPtr.Zero && Movable(h)) return h;
            return MenuBarWindow.TargetHandle();
        }

        internal static bool Movable(IntPtr h)
        {
            if (!Native.IsWindowVisible(h)) return false;
            int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
            if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return false;
            string cls = Native.ClassName(h);
            return cls != "Progman" && cls != "WorkerW" && cls != "Shell_TrayWnd";
        }

        // Where a spot is inside a work area, with macOS' margins (any units).
        public static Rect Area(Rect work, Spot s, double gap, Size current)
        {
            double x = work.Left + gap, y = work.Top + gap, w = work.Width - 2 * gap, h = work.Height - 2 * gap;
            double hw = (w - gap) / 2, hh = (h - gap) / 2;
            switch (s)
            {
                case Spot.Left: return new Rect(x, y, hw, h);
                case Spot.Right: return new Rect(x + hw + gap, y, hw, h);
                case Spot.Top: return new Rect(x, y, w, hh);
                case Spot.Bottom: return new Rect(x, y + hh + gap, w, hh);
                case Spot.TopLeft: return new Rect(x, y, hw, hh);
                case Spot.TopRight: return new Rect(x + hw + gap, y, hw, hh);
                case Spot.BottomLeft: return new Rect(x, y + hh + gap, hw, hh);
                case Spot.BottomRight: return new Rect(x + hw + gap, y + hh + gap, hw, hh);
                case Spot.Center:
                    double cw = Math.Min(current.Width, w), ch = Math.Min(current.Height, h);
                    return new Rect(work.Left + (work.Width - cw) / 2, work.Top + (work.Height - ch) / 2, cw, ch);
                default: return new Rect(x, y, w, h);
            }
        }

        public static void Move(IntPtr h, Spot s)
        {
            if (h == IntPtr.Zero || !Native.IsWindow(h)) return;
            Native.RECT now;
            if (!Native.GetWindowRect(h, out now)) return;
            if (s == Spot.Restore)
            {
                Native.RECT old;
                if (!before.TryGetValue(h, out old)) return;
                before.Remove(h); tiled.Remove(h);
                Place(h, old, false);
                return;
            }
            if (!before.ContainsKey(h) && !Native.IsZoomed(h)) before[h] = now;
            PlaceTile(h, Target(h, s, now));
        }

        // Where a spot puts this window, on the display it's on (physical px).
        public static Native.RECT Target(IntPtr h, Spot s, Native.RECT now)
        {
            var screen = Sys.Screens().FirstOrDefault(d => d.Monitor == Native.MonitorFromWindow(h, 2)) ?? DisplayLayout.Selected;
            var work = new Rect(screen.Work.Left, screen.Work.Top, screen.Work.Right - screen.Work.Left, screen.Work.Bottom - screen.Work.Top);
            Native.RECT frame;
            var size = Native.DwmGetWindowAttribute(h, 9, out frame, 16) == 0 ? new Size(frame.Right - frame.Left, frame.Bottom - frame.Top) : new Size(now.Right - now.Left, now.Bottom - now.Top);
            var r = Area(work, s, Gap * App.Dpi, size);
            return new Native.RECT { Left = (int)Math.Round(r.Left), Top = (int)Math.Round(r.Top), Right = (int)Math.Round(r.Right), Bottom = (int)Math.Round(r.Bottom) };
        }

        // The size a window had before it was tiled, so it can go back to it (dragging it out of its tile, or
        // Return to Previous Size). A window dragged into a tile keeps the size it had before the drag.
        public static void Remember(IntPtr h, Native.RECT r) { if (!before.ContainsKey(h)) before[h] = r; }
        public static bool Untile(IntPtr h, out Native.RECT old) { tiled.Remove(h); if (!before.TryGetValue(h, out old)) return false; before.Remove(h); return true; }
        public static bool TiledFrame(IntPtr h, out Native.RECT r) { foreach (var k in tiled.Keys.Where(k => !Native.IsWindow(k)).ToList()) { tiled.Remove(k); before.Remove(k); } return tiled.TryGetValue(h, out r); }
        public static void PlaceAt(IntPtr h, Native.RECT r) { Place(h, r, false); }
        public static void PlaceTile(IntPtr h, Native.RECT r) { tiled[h] = r; Place(h, r, true); }   // r = the visible frame

        // Moves a window so its visible frame lands on `r` (physical px). Windows 10/11 frames carry invisible
        // resize borders; `visible` adds them back so the edges line up with the margins.
        static void Place(IntPtr h, Native.RECT r, bool visible)
        {
            Action go = () =>
            {
                Native.RECT wr, fr;
                int l = 0, t = 0, rr = 0, b = 0;
                if (visible && Native.GetWindowRect(h, out wr) && Native.DwmGetWindowAttribute(h, 9, out fr, 16) == 0)
                { l = fr.Left - wr.Left; t = fr.Top - wr.Top; rr = wr.Right - fr.Right; b = wr.Bottom - fr.Bottom; }
                Native.SetWindowPos(h, IntPtr.Zero, r.Left - l, r.Top - t, r.Right - r.Left + l + rr, r.Bottom - r.Top + t + b, 0x4 /* NOZORDER */ | Native.SWP_NOACTIVATE | 0x4000 /* ASYNC */);
            };
            if (Native.IsZoomed(h) || Native.IsIconic(h))
            {
                Native.ShowWindow(h, Native.SW_RESTORE);
                var t2 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };   // the restore is asynchronous
                t2.Tick += delegate { t2.Stop(); go(); };
                t2.Start();
            }
            else go();
        }

        // Window > Arrange: the front window and the one behind it side by side (or stacked), like macOS 15.
        public static void Arrange(Spot first, Spot second)
        {
            var a = Front();
            var b = App.Wins.Select(w => w.Hwnd).FirstOrDefault(h => h != a && !Native.IsIconic(h) && Native.MonitorFromWindow(h, 2) == Native.MonitorFromWindow(a, 2));
            Move(a, first);
            if (b != IntPtr.Zero) Move(b, second);
        }

        public static void Quarters()
        {
            var a = Front();
            var others = App.Wins.Select(w => w.Hwnd).Where(h => h != a && !Native.IsIconic(h) && Native.MonitorFromWindow(h, 2) == Native.MonitorFromWindow(a, 2)).Take(3).ToList();
            others.Insert(0, a);
            var spots = new[] { Spot.TopLeft, Spot.TopRight, Spot.BottomLeft, Spot.BottomRight };
            for (int i = 0; i < others.Count; i++) Move(others[i], spots[i]);
        }
    }

    // Push the pointer into a screen corner and something happens -- Mission Control, the desktop, Launchpad...
    static class HotCorners
    {
        public static readonly string[] Actions = { "—", "Mission Control", "Application Windows", "Desktop", "Notification Center", "Control Center", "Launchpad", "Start Screen Saver", "Put Display to Sleep", "Lock Screen" };
        static DispatcherTimer timer;
        static int armed = -1;          // the corner the pointer is resting in
        static DateTime since;
        static bool fired;
        static List<Sys.Display> screens;
        static DateTime screensAt;

        public static void Sync()
        {
            bool any = Settings.CornerTopLeft + Settings.CornerTopRight + Settings.CornerBottomLeft + Settings.CornerBottomRight > 0;
            if (any && timer == null)
            {
                timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
                timer.Tick += delegate { Check(); };
            }
            if (timer == null) return;
            if (any) timer.Start(); else timer.Stop();
        }

        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);

        static void Check()
        {
            Native.POINT p;
            if (LockScreen.Active || !Native.GetCursorPos(out p)) return;
            // the displays, looked up every couple of seconds rather than 25 times a second
            if (screens == null || (DateTime.UtcNow - screensAt).TotalSeconds > 2) { screens = Sys.Screens(); screensAt = DateTime.UtcNow; }
            int corner = -1;
            foreach (var s in screens)
            {
                var b = s.Bounds;
                bool left = p.X <= b.Left + 1, right = p.X >= b.Right - 2, top = p.Y <= b.Top + 1, bottom = p.Y >= b.Bottom - 2;
                bool inside = p.X >= b.Left && p.X < b.Right && p.Y >= b.Top && p.Y < b.Bottom;
                if (!inside) continue;
                corner = top && left ? 0 : top && right ? 1 : bottom && left ? 2 : bottom && right ? 3 : -1;
                if (corner >= 0 && Windows.FullscreenOn(s)) corner = -1;   // games and videos keep their corners
                break;
            }
            if (corner != armed) { armed = corner; since = DateTime.UtcNow; fired = false; return; }
            if (corner < 0 || fired || (DateTime.UtcNow - since).TotalMilliseconds < 120) return;
            if ((GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0) return;   // dragging something
            fired = true;
            Run(corner == 0 ? Settings.CornerTopLeft : corner == 1 ? Settings.CornerTopRight : corner == 2 ? Settings.CornerBottomLeft : Settings.CornerBottomRight);
        }

        public static void Run(int action)
        {
            switch (action)
            {
                case 1: MissionControl.Toggle(IntPtr.Zero); break;
                case 2: MissionControl.Toggle(Tiling.Front()); break;
                case 3: Native.SendKeys(0x5B, 0x44); break;                                   // Win+D
                case 4: ClockPanel.Toggle(null); break;
                case 5: ControlCenterWindow.Toggle(); break;
                case 6: App.ToggleLaunchpad(); break;
                case 7: Native.PostMessage(Hotkeys.Host, 0x0112, (IntPtr)0xF140, IntPtr.Zero); break;   // SC_SCREENSAVE
                case 8: Native.PostMessage(Hotkeys.Host, 0x0112, (IntPtr)0xF170, (IntPtr)2); break;     // SC_MONITORPOWER off
                case 9: LockScreen.Lock(); break;
            }
        }
    }
}
