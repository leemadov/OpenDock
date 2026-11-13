using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace OpenDock
{
    // The Genie and the close animation for every app's windows, not just OpenDock's. Windows won't let one app
    // hide another's window, so the effect is played from a picture: OpenDock takes one when your pointer heads
    // for a window's title-bar buttons (and shortly after you switch to a window), and plays it the moment Windows
    // reports the window minimizing or closing. Restoring from the Dock brings the window back off screen, plays
    // the Genie backwards from a fresh picture, then puts the window in its place.
    static class AppEffects
    {
        [StructLayout(LayoutKind.Sequential)]
        struct PLACEMENT { public int length, flags, showCmd; public Native.POINT min, max; public Native.RECT normal; }
        [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr h, ref PLACEMENT p);
        [DllImport("user32.dll")] static extern bool SetWindowPlacement(IntPtr h, ref PLACEMENT p);
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
        [DllImport("user32.dll")] static extern bool IsHungAppWindow(IntPtr h);

        class Shot { public BitmapSource Image; public Native.RECT Window, Frame; public DateTime At; }

        static readonly Dictionary<IntPtr, Shot> shots = new Dictionary<IntPtr, Shot>();
        static readonly HashSet<IntPtr> ours = new HashSet<IntPtr>();   // windows OpenDock is minimizing/restoring itself
        static readonly List<Native.WinEventProc> keep = new List<Native.WinEventProc>();
        static readonly uint myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        static DispatcherTimer hover;
        static IntPtr capturing;
        static int minimizing;     // several windows minimizing at once (Show Desktop) get no effect
        static DateTime burst;

        static bool On { get { return Settings.AppGenie && (Settings.MinimizeAnimation || Settings.CloseAnimation); } }
        static bool Minimizing { get { return Settings.AppGenie && Settings.MinimizeAnimation; } }
        static bool Closing { get { return Settings.AppGenie && Settings.CloseAnimation; } }

        public static void Start()
        {
            Native.WinEventProc onMin = App.Guard((hk, ev, h, obj, child, t, time) => { if (obj == 0 && child == 0) OnMinimize(h); });
            Native.WinEventProc onGone = App.Guard((hk, ev, h, obj, child, t, time) => { if (obj == 0 && child == 0) OnGone(h); });
            keep.Add(onMin); keep.Add(onGone);
            Native.SetWinEventHook(0x0016, 0x0016, IntPtr.Zero, onMin, 0, 0, 2);   // EVENT_SYSTEM_MINIMIZESTART
            Native.SetWinEventHook(0x8001, 0x8001, IntPtr.Zero, onGone, 0, 0, 2);   // EVENT_OBJECT_DESTROY
            Native.SetWinEventHook(0x8003, 0x8003, IntPtr.Zero, onGone, 0, 0, 2);   // EVENT_OBJECT_HIDE
            hover = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            hover.Tick += delegate { Watch(); };
            hover.Start();
            App.ForegroundChanged += h =>
            {
                // A picture a moment after you switch to a window covers Win+Down and other keyboard minimizes.
                var w = Native.GetAncestor(h, 2);
                var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                t.Tick += delegate { t.Stop(); if (Native.GetAncestor(Native.GetForegroundWindow(), 2) == w) Capture(w, false); };
                t.Start();
            };
        }

        static bool AppWindow(IntPtr h)
        {
            if (h == IntPtr.Zero || !Native.IsWindow(h) || Native.IsIconic(h)) return false;
            uint pid; Native.GetWindowThreadProcessId(h, out pid);
            return pid != myPid && App.Wins.Any(w => w.Hwnd == h);
        }

        // The pointer near the title-bar buttons of the window you're in: refresh its picture.
        static void Watch()
        {
            if (!On) return;
            var h = Native.GetAncestor(Native.GetForegroundWindow(), 2);
            if (!AppWindow(h)) return;
            Native.POINT p; Native.RECT f;
            if (!Native.GetCursorPos(out p) || Native.DwmGetWindowAttribute(h, 9, out f, 16) != 0) return;
            double d = App.Dpi;
            bool nearButtons = p.Y >= f.Top - 4 && p.Y <= f.Top + 56 * d && p.X <= f.Right + 4 && p.X >= f.Right - 220 * d;
            if (nearButtons) Capture(h, true);
        }

        static void Capture(IntPtr h, bool hurry)
        {
            if (!On || !AppWindow(h) || capturing != IntPtr.Zero || Windows.FullscreenOn(Sys.Screens().FirstOrDefault(s => s.Monitor == Native.MonitorFromWindow(h, 2)) ?? DisplayLayout.Selected)) return;
            Shot old;
            // (a full-window capture: while the pointer rests near the buttons, every 2 s is plenty)
            if (shots.TryGetValue(h, out old) && (DateTime.UtcNow - old.At).TotalMilliseconds < (hurry ? 2000 : 3000)) return;
            capturing = h;
            Task.Run(() => Take(h)).ContinueWith(t =>
            {
                capturing = IntPtr.Zero;
                if (t.Result == null) return;
                shots[h] = t.Result;
                Trim();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // Minimized windows keep their pictures (for the way back out); the others' go first.
        static void Trim()
        {
            foreach (var gone in shots.Keys.Where(k => !Native.IsWindow(k)).ToList()) shots.Remove(gone);
            while (shots.Count > 10)
                shots.Remove(shots.OrderBy(x => Native.IsIconic(x.Key) ? 1 : 0).ThenBy(x => x.Value.At).First().Key);
        }

        // The window's picture as DWM draws it (works for browsers and other GPU-drawn apps), cut to the visible frame.
        static Shot Take(IntPtr h)
        {
            try
            {
                if (IsHungAppWindow(h)) return null;
                Native.RECT wr, fr;
                if (!Native.GetWindowRect(h, out wr) || Native.DwmGetWindowAttribute(h, 9, out fr, 16) != 0) return null;
                int w = wr.Right - wr.Left, ht = wr.Bottom - wr.Top;
                if (w < 40 || ht < 40 || w > 8000 || ht > 8000) return null;
                IntPtr screen = Native.GetDC(IntPtr.Zero), dc = Native.CreateCompatibleDC(screen), bmp = Native.CreateCompatibleBitmap(screen, w, ht);
                var old = Native.SelectObject(dc, bmp);
                try
                {
                    if (!PrintWindow(h, dc, 2 /* PW_RENDERFULLCONTENT */)) return null;
                    Native.SelectObject(dc, old);
                    var crop = new Int32Rect(Math.Max(0, fr.Left - wr.Left), Math.Max(0, fr.Top - wr.Top), Math.Min(w, fr.Right - fr.Left), Math.Min(ht, fr.Bottom - fr.Top));
                    if (crop.X + crop.Width > w) crop.Width = w - crop.X;
                    if (crop.Y + crop.Height > ht) crop.Height = ht - crop.Y;
                    var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, crop, BitmapSizeOptions.FromEmptyOptions());
                    BitmapSource img = new FormatConvertedBitmap(src, PixelFormats.Bgr32, null, 0);
                    // Big windows are kept at most 1400 px wide: plenty for half a second of motion, ~4x less memory.
                    if (img.PixelWidth > 1400)
                    {
                        double k = 1400.0 / img.PixelWidth;
                        var small = new TransformedBitmap(img, new ScaleTransform(k, k));
                        int sw = small.PixelWidth, sh = small.PixelHeight;
                        var px = new byte[sw * sh * 4];
                        small.CopyPixels(px, sw * 4, 0);
                        img = BitmapSource.Create(sw, sh, 96, 96, PixelFormats.Bgr32, null, px, sw * 4);
                    }
                    img.Freeze();
                    if (Blank(img)) return null;
                    return new Shot { Image = img, Window = wr, Frame = fr, At = DateTime.UtcNow };
                }
                finally { Native.SelectObject(dc, old); Native.DeleteObject(bmp); Native.DeleteDC(dc); Native.ReleaseDC(IntPtr.Zero, screen); }
            }
            catch (Exception ex) { App.Log(ex); return null; }
        }

        // A failed print comes back black.
        static bool Blank(BitmapSource img)
        {
            int w = img.PixelWidth, h = img.PixelHeight, lit = 0;
            var px = new byte[4];
            for (int i = 1; i <= 12; i++)
            {
                img.CopyPixels(new Int32Rect(w * i / 13, h * ((i * 7) % 13 + 1) / 14, 1, 1), px, 4, 0);
                if (px[0] + px[1] + px[2] > 24) lit++;
            }
            return lit == 0;
        }

        static Rect Dip(Native.RECT r) { return DisplayLayout.Dip(r); }

        static Rect IconFor(IntPtr h, Rect frame)
        {
            var center = new Point(frame.Left + frame.Width / 2, frame.Top + frame.Height / 2);
            var dock = App.Docks.FirstOrDefault(d => d.MonitorBounds.Contains(center)) ?? App.Dock;
            var r = dock == null ? null : dock.IconRect(h, false);
            if (r.HasValue) return r.Value;
            var screen = dock != null ? dock.MonitorBounds : DisplayLayout.Bounds;
            return new Rect(screen.Left + screen.Width / 2 - 24, screen.Bottom - 56, 48, 48);
        }

        // Where a minimized window will come back to, in screen pixels (its placement is in work-area coordinates).
        static Native.RECT Visible(PLACEMENT p, IntPtr h)
        {
            // Workspace coordinates are measured from the main display's work area (past the menu bar).
            var screens = Sys.Screens();
            var primary = screens.FirstOrDefault(s => s.Primary) ?? DisplayLayout.Selected;
            int dx = primary.Work.Left - primary.Bounds.Left, dy = primary.Work.Top - primary.Bounds.Top;
            var r = p.normal;
            var rect = new Native.RECT { Left = r.Left + dx, Top = r.Top + dy, Right = r.Right + dx, Bottom = r.Bottom + dy };
            if ((p.flags & 2 /* WPF_RESTORETOMAXIMIZED */) == 0) return rect;
            int cx = (rect.Left + rect.Right) / 2, cy = (rect.Top + rect.Bottom) / 2;
            var screen = screens.FirstOrDefault(s => cx >= s.Bounds.Left && cx < s.Bounds.Right && cy >= s.Bounds.Top && cy < s.Bounds.Bottom) ?? primary;
            return screen.Work;
        }

        static bool Near(Native.RECT a, Native.RECT b, int slack)
        {
            return Math.Abs(a.Left - b.Left) <= slack && Math.Abs(a.Top - b.Top) <= slack && Math.Abs(a.Right - b.Right) <= slack && Math.Abs(a.Bottom - b.Bottom) <= slack;
        }

        // ---------- minimizing ----------

        static void OnMinimize(IntPtr h)
        {
            if (!Minimizing || ours.Contains(h)) return;
            if ((DateTime.UtcNow - burst).TotalMilliseconds > 250) { burst = DateTime.UtcNow; minimizing = 0; }
            if (++minimizing > 1) return;    // Show Desktop and friends: no Genie storm
            Shot s;
            if (!shots.TryGetValue(h, out s) || (DateTime.UtcNow - s.At).TotalSeconds > 20 || WindowFx.Busy) return;
            // Only if the picture is of the window where it was (it may have moved or resized since).
            var p = new PLACEMENT { length = Marshal.SizeOf(typeof(PLACEMENT)) };
            if (!GetWindowPlacement(h, ref p)) return;
            bool maximized = (p.flags & 2) != 0;
            var was = Visible(p, h);
            if (!maximized && !Near(was, s.Window, 12)) return;
            var frame = Dip(s.Frame);
            WindowFx.Minimize(s.Image, frame, IconFor(h, frame), false, Settings.MinimizeEffect == 0, null, null);
        }

        // From the Dock: the picture is taken right now, then the window goes.
        public static void Minimize(IntPtr h)
        {
            if (!Minimizing || !AppWindow(h) || WindowFx.Busy) { Native.ShowWindow(h, Native.SW_MINIMIZE); return; }
            var s = Take(h);
            ours.Add(h);
            Later(1500, () => ours.Remove(h));
            if (s == null) { Native.ShowWindow(h, Native.SW_MINIMIZE); return; }
            shots[h] = s;
            Trim();
            var frame = Dip(s.Frame);
            // The window goes once the effect's first frame covers it.
            WindowFx.Minimize(s.Image, frame, IconFor(h, frame), false, Settings.MinimizeEffect == 0, () => Native.ShowWindow(h, Native.SW_MINIMIZE), null);
        }

        // ---------- restoring ----------

        // Out of the Dock: the picture kept from when it minimized pours back out (Genie backwards), and the real
        // window takes its place at the end. (Windows won't keep a restored window off screen to picture it fresh.)
        public static void Restore(IntPtr h)
        {
            Shot s;
            uint pid; Native.GetWindowThreadProcessId(h, out pid);
            if (!Minimizing || !Native.IsIconic(h) || pid == myPid || WindowFx.Busy || !shots.TryGetValue(h, out s)) { Native.Activate(h); return; }
            var p = new PLACEMENT { length = Marshal.SizeOf(typeof(PLACEMENT)) };
            if (!GetWindowPlacement(h, ref p)) { Native.Activate(h); return; }
            var target = Visible(p, h);
            // Where its visible frame will be once it's back (the picture's invisible borders taken off).
            int l = s.Frame.Left - s.Window.Left, tp = s.Frame.Top - s.Window.Top, r = s.Window.Right - s.Frame.Right, b = s.Window.Bottom - s.Frame.Bottom;
            var to = (p.flags & 2) != 0 ? target : new Native.RECT { Left = target.Left + l, Top = target.Top + tp, Right = target.Right - r, Bottom = target.Bottom - b };
            var frame = Dip(to);
            ours.Add(h);
            WindowFx.Minimize(s.Image, frame, IconFor(h, frame), true, Settings.MinimizeEffect == 0, null, () => { Native.Activate(h); Later(500, () => ours.Remove(h)); }, () => !Native.IsIconic(h) && Native.IsWindowVisible(h));
        }

        // ---------- closing ----------

        static void OnGone(IntPtr h)
        {
            Shot s;
            if (!Closing || ours.Contains(h) || !shots.TryGetValue(h, out s)) return;
            shots.Remove(h);
            // Only a close you just reached for (the picture is fresh), never a window that's being minimized.
            if ((DateTime.UtcNow - s.At).TotalSeconds > 4 || Native.IsIconic(h) || WindowFx.Busy) return;
            if (Native.IsWindow(h) && Native.IsWindowVisible(h)) return;   // a child hid, not the window
            // ...and the pointer is on its close button corner (a browser tab dragged back in also "closes" a window).
            Native.POINT c; double d = App.Dpi;
            if (!Native.GetCursorPos(out c) || c.X < s.Frame.Right - 160 * d || c.X > s.Frame.Right + 4 || c.Y < s.Frame.Top - 4 || c.Y > s.Frame.Top + 56 * d) return;
            WindowFx.Close(s.Image, Dip(s.Frame));
        }

        static void Later(int ms, Action a)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += delegate { t.Stop(); a(); };
            t.Start();
        }
    }
}
