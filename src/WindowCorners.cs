using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace OpenDock
{
    // Macs round every window the same, bigger than Windows 11's 8 px. This clips other apps' ordinary windows to a
    // rounded shape of the chosen radius (Settings > Appearance > Windows) -- the same cut-out GlassDock's own windows use.
    //
    // A window keeps its cut-out after the program that made it has gone, and a cut-out made for one size would clip the
    // window if it were enlarged. So every window this touches is written down (rounded.txt), put back the moment
    // the setting is turned off or GlassDock quits, and any a crashed GlassDock left behind are put back at the next start.
    // Windows that are maximized, full screen, run as administrator, or aren't ordinary app windows are left alone.
    // Rounding takes a window's drop shadow with it and its edge isn't smoothed; the radius slider at 8 turns all of it off.
    static class WindowCorners
    {
        static readonly Dictionary<IntPtr, string> done = new Dictionary<IntPtr, string>();   // window -> what its cut-out was made for
        static readonly HashSet<IntPtr> dirty = new HashSet<IntPtr>();
        static Native.WinEventProc onChange;
        static DispatcherTimer settle, sweep;
        [DllImport("user32.dll")] static extern int GetWindowRgn(IntPtr h, IntPtr rgn);
        [DllImport("gdi32.dll")] static extern int GetRgnBox(IntPtr rgn, out Native.RECT box);
        static readonly Dictionary<IntPtr, DateTime> last = new Dictionary<IntPtr, DateTime>();       // when each window was last looked at
        static readonly Dictionary<IntPtr, List<DateTime>> resets = new Dictionary<IntPtr, List<DateTime>>();   // when an app took its shape back
        static readonly Dictionary<IntPtr, DateTime> leaveAlone = new Dictionary<IntPtr, DateTime>();   // apps that keep fighting it: left in peace for a while

        static string Record { get { return Path.Combine(Settings.DataDir, "rounded.txt"); } }

        public static void Start()
        {
            Recover();
            onChange = App.Guard((hk, ev, h, obj, child, t, time) =>
            {
                if (obj != 0 || child != 0 || !Active || h == IntPtr.Zero) return;
                // a window being resized is looked at right away (a cut-out made for the old size would clip the new
                // one for a moment), at most every 8 ms; the timer catches the last change
                DateTime when;
                if (!last.TryGetValue(h, out when) || (DateTime.UtcNow - when).TotalMilliseconds > 8) One(h);
                else { dirty.Add(h); if (!settle.IsEnabled) settle.Start(); }
            });
            Native.SetWinEventHook(0x800B, 0x800B, IntPtr.Zero, onChange, 0, 0, 2);   // moved or resized
            Native.SetWinEventHook(0x8002, 0x8002, IntPtr.Zero, onChange, 0, 0, 2);   // shown
            Native.SetWinEventHook(0x0016, 0x0017, IntPtr.Zero, onChange, 0, 0, 2);   // minimized / restored
            settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(12) };
            settle.Tick += delegate { settle.Stop(); var now = dirty.ToList(); dirty.Clear(); foreach (var h in now) One(h); };
            sweep = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };   // windows that never announced themselves
            sweep.Tick += delegate { Sweep(); };
            sweep.Start();
            Settings.Changed += Sweep;
            Sweep();
        }

        // The corner radius (physical px) GlassDock's cut-out gives this window right now, or 0 when it has Windows' own.
        public static double RadiusPx(IntPtr h)
        {
            string key;
            if (!done.TryGetValue(h, out key) || string.IsNullOrEmpty(key)) return 0;
            var probe = Native.CreateRoundRectRgn(0, 0, 1, 1, 0, 0);
            int has = GetWindowRgn(h, probe); Native.DeleteObject(probe);
            if (has == 0) return 0;   // the app took its shape back
            int slash = key.LastIndexOf('/');
            int d; return slash >= 0 && int.TryParse(key.Substring(slash + 1), out d) ? d / 2.0 : 0;
        }

        static bool Active { get { return Settings.RoundOtherWindows && Settings.WindowCorner > 8; } }

        static int Radius { get { return Math.Max(8, Math.Min(40, Settings.WindowCorner)); } }

        static void Sweep()
        {
            try
            {
                if (!Active) { Restore(); return; }
                foreach (var h in done.Keys.Where(k => !Native.IsWindow(k)).ToList()) done.Remove(h);
                foreach (var w in App.Wins) One(w.Hwnd);
            }
            catch (Exception ex) { App.Log(ex); }
        }

        static void One(IntPtr h)
        {
            try
            {
                if (!Active) return;
                last[h] = DateTime.UtcNow;
                DateTime until;
                if (leaveAlone.TryGetValue(h, out until)) { if (DateTime.UtcNow < until) return; leaveAlone.Remove(h); }
                if (!TrafficLights.Candidate(h) || Native.IsZoomed(h)) { Clear(h); return; }
                Native.RECT wr, fr;
                if (!Native.GetWindowRect(h, out wr)) return;
                if (Native.DwmGetWindowAttribute(h, 9, out fr, 16) != 0) fr = wr;
                uint dpi = TrafficLights.DpiOf(h);
                int d = (int)Math.Round(2 * Radius * (dpi == 0 ? 96 : dpi) / 96.0);
                string key = (wr.Right - wr.Left) + "x" + (wr.Bottom - wr.Top) + "/" + (fr.Left - wr.Left) + "," + (fr.Top - wr.Top) + "/" + d;
                string was;
                if (done.TryGetValue(h, out was) && was == key)
                {
                    // still cut the way it was made? Some apps (Chrome, Electron) put their own shape back on resize,
                    // and an app that does it over and over is left alone for a minute instead of fought with
                    if (HasOurShape(h, fr.Right - fr.Left, fr.Bottom - fr.Top)) return;
                    var times = resets.ContainsKey(h) ? resets[h] : (resets[h] = new List<DateTime>());
                    times.RemoveAll(t => (DateTime.UtcNow - t).TotalSeconds > 5);
                    times.Add(DateTime.UtcNow);
                    if (times.Count > 6) { leaveAlone[h] = DateTime.UtcNow.AddSeconds(60); times.Clear(); Clear(h); return; }
                }
                int r1 = 2; int b1 = unchecked((int)0xFFFFFFFE);
                int round = 1; Native.DwmSetWindowAttribute(h, 33, ref round, 4);          // no Windows rounding: it would show as a fringe
                Native.DwmSetWindowAttribute(h, 34, ref b1, 4);                           // no Windows outline
                var rgn = Native.CreateRoundRectRgn(fr.Left - wr.Left, fr.Top - wr.Top, fr.Right - wr.Left + 1, fr.Bottom - wr.Top + 1, d, d);
                if (Native.SetWindowRgn(h, rgn, true) == 0) { Native.DeleteObject(rgn); return; }
                bool first = !done.ContainsKey(h);
                done[h] = key;
                if (first) Save();
            }
            catch (Exception ex) { App.Log(ex); }
        }

        // Does the window have a cut-out of the size it should (its visible frame)?
        static bool HasOurShape(IntPtr h, int width, int height)
        {
            var probe = Native.CreateRoundRectRgn(0, 0, 1, 1, 0, 0);
            try
            {
                if (GetWindowRgn(h, probe) <= 1) return false;
                Native.RECT box; GetRgnBox(probe, out box);
                return Math.Abs((box.Right - box.Left) - width) <= 1 && Math.Abs((box.Bottom - box.Top) - height) <= 1;
            }
            finally { Native.DeleteObject(probe); }
        }

        // One window back to exactly as Windows draws it.
        static void Clear(IntPtr h)
        {
            last.Remove(h);
            if (!done.Remove(h) || !Native.IsWindow(h)) { Save(); return; }
            Native.SetWindowRgn(h, IntPtr.Zero, true);
            int round = 2; Native.DwmSetWindowAttribute(h, 33, ref round, 4);
            int border = unchecked((int)0xFFFFFFFF); Native.DwmSetWindowAttribute(h, 34, ref border, 4);
            Save();
        }

        public static void Restore()
        {
            foreach (var h in done.Keys.ToList()) Clear(h);
            try { if (File.Exists(Record)) File.Delete(Record); } catch { }
        }

        // The windows rounded so far, for the next start if this GlassDock never gets to put them back.
        static void Save()
        {
            try
            {
                if (done.Count == 0) { if (File.Exists(Record)) File.Delete(Record); return; }
                File.WriteAllLines(Record, done.Keys.Select(h => h.ToInt64().ToString()).ToArray());
            }
            catch { }
        }

        static void Recover()
        {
            try
            {
                if (!File.Exists(Record)) return;
                foreach (var line in File.ReadAllLines(Record))
                {
                    long v; if (!long.TryParse(line, out v)) continue;
                    var h = new IntPtr(v);
                    if (!Native.IsWindow(h)) continue;
                    done[h] = "";
                    Clear(h);
                }
                File.Delete(Record);
            }
            catch (Exception ex) { App.Log(ex); }
        }
    }
}
