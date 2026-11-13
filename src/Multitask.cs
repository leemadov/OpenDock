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
using System.Windows.Threading;

namespace OpenDock
{
    // macOS 15 window tiling by dragging: take a window to a screen edge or corner (or hold Alt -- macOS' Option --
    // anywhere) and a glass outline shows where it will go; let go and it fills that tile, with margins. Dragging a
    // tiled window away gives it back the size it had.
    static class DragTile
    {
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        static Native.WinEventProc onStart, onEnd;   // kept alive: Windows calls them
        static DispatcherTimer poll;
        static IntPtr moving;
        static Native.RECT startRect;
        static bool wasZoomed, wasTiled;
        static List<Sys.Display> screens;
        static Tiling.Spot? zone;
        static Sys.Display zoneScreen;
        static TilePreview preview;

        public static void Start()
        {
            onStart = App.Guard((hk, ev, h, obj, child, t, time) => { if (obj == 0 && child == 0) Begin(h); });
            onEnd = App.Guard((hk, ev, h, obj, child, t, time) => { if (obj == 0 && child == 0) End(h); });
            Native.SetWinEventHook(0x000A, 0x000A, IntPtr.Zero, onStart, 0, 0, 0);   // EVENT_SYSTEM_MOVESIZESTART (our windows too)
            Native.SetWinEventHook(0x000B, 0x000B, IntPtr.Zero, onEnd, 0, 0, 0);     // EVENT_SYSTEM_MOVESIZEEND
            poll = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            poll.Tick += delegate { Track(); };
        }

        static void Begin(IntPtr h)
        {
            moving = IntPtr.Zero;
            if (!Settings.DragTile && !Settings.DragTileAlt) return;
            h = Native.GetAncestor(h, 2);
            if (h == IntPtr.Zero || !Tiling.Movable(h)) return;
            if ((Native.GetWindowLong(h, Native.GWL_STYLE) & 0x40000) == 0) return;   // not resizable (WS_THICKFRAME): leave it be
            if (preview != null && h == preview.Handle) return;
            // A resize starts on a border, a move on the title bar: ask the window what's under the pointer.
            Native.POINT p; Native.GetCursorPos(out p);
            IntPtr hit;
            var at = (IntPtr)unchecked((int)(((uint)(p.Y & 0xFFFF) << 16) | (uint)(p.X & 0xFFFF)));
            if (Native.SendMessageTimeout(h, 0x84 /* WM_NCHITTEST */, IntPtr.Zero, at, 2 /* ABORTIFHUNG */, 60, out hit) != IntPtr.Zero)
            {
                int ht = hit.ToInt32();
                if (ht >= 10 && ht <= 17) return;   // HTLEFT..HTBOTTOMRIGHT: a resize
            }
            if (!Native.GetWindowRect(h, out startRect)) return;
            wasZoomed = Native.IsZoomed(h);
            Native.RECT tile, frame;
            wasTiled = Tiling.TiledFrame(h, out tile) && Native.DwmGetWindowAttribute(h, 9, out frame, 16) == 0
                && Math.Abs(frame.Left - tile.Left) <= 4 && Math.Abs(frame.Top - tile.Top) <= 4 && Math.Abs(frame.Right - tile.Right) <= 4 && Math.Abs(frame.Bottom - tile.Bottom) <= 4;
            screens = Sys.Screens();
            moving = h; zone = null; zoneScreen = null;
            poll.Start();
        }

        static void Track()
        {
            if (moving == IntPtr.Zero || !Native.IsWindow(moving)) { poll.Stop(); Hide(); return; }
            Native.POINT p;
            if (!Native.GetCursorPos(out p)) return;
            var screen = screens.FirstOrDefault(s => p.X >= s.Bounds.Left && p.X < s.Bounds.Right && p.Y >= s.Bounds.Top && p.Y < s.Bounds.Bottom);
            Tiling.Spot? z = null;
            if (screen != null)
            {
                bool alt = Settings.DragTileAlt && (GetAsyncKeyState(0x12) & 0x8000) != 0;
                z = alt ? Anywhere(screen, p) : Settings.DragTile ? Edge(screen, p) : null;
            }
            if (z == zone && screen == zoneScreen) return;
            zone = z; zoneScreen = screen;
            if (z == null) { Hide(); return; }
            if (preview == null) preview = new TilePreview();
            preview.Go(TileRect(screen, z.Value), new Point(p.X, p.Y), moving);
        }

        // The screen's edges and corners, as on macOS: the top edge fills the screen, the sides take halves, the
        // corners quarters. The bottom edge only has its corners (the Dock lives there).
        public static Tiling.Spot? Edge(Sys.Display s, Native.POINT p)
        {
            var b = s.Bounds;
            bool l = p.X <= b.Left + 1, r = p.X >= b.Right - 2, t = p.Y <= b.Top + 1, bt = p.Y >= b.Bottom - 2;
            double cy = (b.Bottom - b.Top) * 0.14, cx = (b.Right - b.Left) * 0.1;
            if (l) return p.Y < b.Top + cy ? Tiling.Spot.TopLeft : p.Y > b.Bottom - cy ? Tiling.Spot.BottomLeft : Tiling.Spot.Left;
            if (r) return p.Y < b.Top + cy ? Tiling.Spot.TopRight : p.Y > b.Bottom - cy ? Tiling.Spot.BottomRight : Tiling.Spot.Right;
            if (t) return p.X < b.Left + cx ? Tiling.Spot.TopLeft : p.X > b.Right - cx ? Tiling.Spot.TopRight : Tiling.Spot.Fill;
            if (bt) return p.X < b.Left + cx ? Tiling.Spot.BottomLeft : p.X > b.Right - cx ? Tiling.Spot.BottomRight : (Tiling.Spot?)null;
            return null;
        }

        // Holding Alt: wherever the pointer is picks the tile -- sides for halves, near the corners for quarters,
        // the middle for the whole screen.
        public static Tiling.Spot Anywhere(Sys.Display s, Native.POINT p)
        {
            var w = s.Work;
            double fx = (p.X - w.Left) / (double)Math.Max(1, w.Right - w.Left), fy = (p.Y - w.Top) / (double)Math.Max(1, w.Bottom - w.Top);
            int col = fx < 0.3 ? 0 : fx > 0.7 ? 2 : 1, row = fy < 0.25 ? 0 : fy > 0.75 ? 2 : 1;
            var grid = new[,] { { Tiling.Spot.TopLeft, Tiling.Spot.Left, Tiling.Spot.BottomLeft }, { Tiling.Spot.Top, Tiling.Spot.Fill, Tiling.Spot.Bottom }, { Tiling.Spot.TopRight, Tiling.Spot.Right, Tiling.Spot.BottomRight } };
            return grid[col, row];
        }

        // A tile on a display, as a visible frame in physical pixels.
        static Native.RECT TileRect(Sys.Display s, Tiling.Spot spot)
        {
            var work = new Rect(s.Work.Left, s.Work.Top, s.Work.Right - s.Work.Left, s.Work.Bottom - s.Work.Top);
            var r = Tiling.Area(work, spot, Tiling.Gap * App.Dpi, work.Size);
            return new Native.RECT { Left = (int)Math.Round(r.Left), Top = (int)Math.Round(r.Top), Right = (int)Math.Round(r.Right), Bottom = (int)Math.Round(r.Bottom) };
        }

        static void End(IntPtr h)
        {
            h = Native.GetAncestor(h, 2);
            if (h != moving || moving == IntPtr.Zero) return;
            poll.Stop();
            var z = zone; var screen = zoneScreen;
            moving = IntPtr.Zero; zone = null;
            Hide();
            Native.RECT cur;
            if (!Native.GetWindowRect(h, out cur)) return;
            if (z != null && screen != null)
            {
                Tiling.Remember(h, wasZoomed ? cur : startRect);
                var target = TileRect(screen, z.Value);
                // after Windows' own snapping (if it's on) has had its turn -- and again once its animation has
                // finished, if Snap put the window back in its own (margin-less) half
                Later(90, () => Tiling.PlaceTile(h, target));
                foreach (int ms in new[] { 450, 900 })
                    Later(ms, () => { Native.RECT f; if (Native.DwmGetWindowAttribute(h, 9, out f, 16) == 0 && (Math.Abs(f.Left - target.Left) > 3 || Math.Abs(f.Top - target.Top) > 3 || Math.Abs(f.Right - target.Right) > 3 || Math.Abs(f.Bottom - target.Bottom) > 3)) Tiling.PlaceTile(h, target); });
                return;
            }
            // Dragged out of its tile: back to the size it had, with the title bar staying under the pointer.
            Native.RECT old;
            if (!wasTiled || !Tiling.Untile(h, out old)) return;
            Native.POINT p; Native.GetCursorPos(out p);
            int ow = old.Right - old.Left, oh = old.Bottom - old.Top, cw = Math.Max(1, cur.Right - cur.Left);
            int left = (int)Math.Round(p.X - (p.X - cur.Left) * ow / (double)cw);
            Tiling.PlaceAt(h, new Native.RECT { Left = left, Top = cur.Top, Right = left + ow, Bottom = cur.Top + oh });
        }

        static void Hide() { if (preview != null) preview.Away(); }

        static void Later(int ms, Action a)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += delegate { t.Stop(); try { a(); } catch (Exception ex) { App.Log(ex); } };
            t.Start();
        }
    }

    // The outline of where a dragged window will tile: a pane of glass in the accent colour, sitting just under the
    // window being dragged, that grows out from the pointer and glides between tiles.
    class TilePreview : Window
    {
        readonly Border glass;
        public IntPtr Handle;
        Rect from, to, now;
        bool shown, leaving;
        readonly System.Diagnostics.Stopwatch clock = new System.Diagnostics.Stopwatch();
        const double Ms = 170, Shadow = 24;

        public TilePreview()
        {
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false; ShowActivated = false; Focusable = false; IsHitTestVisible = false; Title = "Tile";
            var accent = Application.Current.TryFindResource("Accent") as SolidColorBrush;
            var c = accent != null ? accent.Color : Color.FromRgb(0x0A, 0x84, 0xFF);
            glass = new Border
            {
                Margin = new Thickness(Shadow), CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1.5),
                BorderBrush = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
                Background = new LinearGradientBrush(Color.FromArgb(105, (byte)(c.R / 2 + 128), (byte)(c.G / 2 + 128), (byte)(c.B / 2 + 128)), Color.FromArgb(70, c.R, c.G, c.B), 90),
                Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 3, Direction = 270, Opacity = 0.3 }
            };
            Content = glass;
            SourceInitialized += delegate
            {
                Handle = new WindowInteropHelper(this).Handle;
                Native.MakeToolWindow(Handle, true);
                Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, Native.GetWindowLong(Handle, Native.GWL_EXSTYLE) | Native.WS_EX_TRANSPARENT);
            };
        }

        // `target` = the tile (physical px), `pointer` where the drag is (physical px), `under` the dragged window.
        public void Go(Native.RECT target, Point pointer, IntPtr under)
        {
            double d = App.Dpi;
            to = new Rect(target.Left / d, target.Top / d, (target.Right - target.Left) / d, (target.Bottom - target.Top) / d);
            from = shown && !leaving ? now : new Rect(pointer.X / d - 40, pointer.Y / d - 30, 80, 60);
            leaving = false;
            if (!shown) { shown = true; Opacity = 0; Apply(from); Show(); CompositionTarget.Rendering += Frame; }
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(110)));
            clock.Restart();
            if (Handle != IntPtr.Zero && under != IntPtr.Zero) Native.SetWindowPos(Handle, under, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        public void Away()
        {
            if (!shown || leaving) return;
            leaving = true;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
            fade.Completed += delegate { if (!leaving) return; shown = false; leaving = false; CompositionTarget.Rendering -= Frame; Hide(); };
            BeginAnimation(OpacityProperty, fade);
        }

        void Frame(object s, EventArgs e)
        {
            if (leaving) return;
            double t = Math.Min(1, clock.Elapsed.TotalMilliseconds / Ms), k = 1 - Math.Pow(1 - t, 3);
            Apply(new Rect(from.X + (to.X - from.X) * k, from.Y + (to.Y - from.Y) * k, from.Width + (to.Width - from.Width) * k, from.Height + (to.Height - from.Height) * k));
        }

        void Apply(Rect r)
        {
            now = r;
            Left = r.X - Shadow; Top = r.Y - Shadow; Width = r.Width + 2 * Shadow; Height = r.Height + 2 * Shadow;
        }
    }

    // Alt+` (Cmd+` on a Mac): the next window of the app you're in; Alt+Shift+` the other way round.
    static class WindowCycle
    {
        public static void Next(bool back)
        {
            var front = Tiling.Front();
            string key = StageManager.AppKey(front);
            if (key == null) return;
            var z = new Dictionary<IntPtr, int>(); int n = 0;
            Native.EnumWindows((h, l) => { z[h] = n++; return true; }, IntPtr.Zero);
            var mine = StageManager.AppWindows(IntPtr.Zero).Where(h => StageManager.AppKey(h) == key).OrderBy(h => z.ContainsKey(h) ? z[h] : int.MaxValue).ToList();
            if (mine.Count < 2) return;
            if (!back) { Native.Activate(mine[mine.Count - 1]); return; }   // the backmost comes forward: A B C -> C A B
            Native.Activate(mine[1]);                                        // and back: A B C -> B C A
            Native.SetWindowPos(mine[0], mine[mine.Count - 1], 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | 0x4000 /* ASYNC: a hung app can't freeze GlassDock */);
        }
    }

    // Stage Manager: the app you're using stays in the middle of the screen; the apps you used recently wait in a
    // strip on the left as live pictures of their windows. Click one (or switch to it any other way: the Dock,
    // Alt+Tab, a notification) and it takes the stage while the one you were using joins the strip.
    //
    // Windows off stage are parked just past the left edge of the screens, not minimized, so their pictures stay
    // live. Where each one was is written to stage.txt as it happens, so even if OpenDock were to crash, the next
    // start puts every window back.
    static class StageManager
    {
        class Away { public IntPtr Hwnd; public Native.RECT Rect; public string Key; public long Order; public DateTime When = DateTime.UtcNow; }
        static readonly List<Away> away = new List<Away>();
        static readonly List<string> recent = new List<string>();   // apps in the strip, most recent first
        static string stage;
        static IntPtr monitor;
        static bool on, busy;
        static long order;
        static StageStrip strip;
        static readonly uint myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);

        static string File { get { return Path.Combine(Settings.DataDir, "stage.txt"); } }
        public static bool Active { get { return on; } }
        public const int MaxApps = 6;

        public static void Start()
        {
            Recover();
            App.ForegroundChanged += OnForeground;
            App.WindowsChanged += delegate { if (on) Prune(); };
            Settings.Changed += Sync;
            Sync();
        }

        public static void Toggle() { Settings.StageManager = !Settings.StageManager; Settings.Apply(); }

        static void Sync()
        {
            try
            {
                if (Settings.StageManager && !on) Enable();
                else if (!Settings.StageManager && on) Disable();
            }
            catch (Exception ex) { App.Log(ex); }
        }

        // The app a window belongs to (as the Dock groups them), or null for anything that isn't an app window.
        public static string AppKey(IntPtr h)
        {
            if (h == IntPtr.Zero) return null;
            var w = App.Wins.FirstOrDefault(x => x.Hwnd == h);
            if (w != null) return w.Key;
            if (App.Finders.Any(f => new WindowInteropHelper(f).Handle == h)) return "opendock:finder";
            uint pid; Native.GetWindowThreadProcessId(h, out pid);
            if (pid == myPid || !Native.IsWindowVisible(h) || Native.GetWindow(h, 4) != IntPtr.Zero || Native.GetWindowTextLength(h) == 0) return null;
            if ((Native.GetWindowLong(h, Native.GWL_EXSTYLE) & (Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE)) != 0 || !Tiling.Movable(h)) return null;
            return Windows.ExeOf(h);
        }

        // App windows (on one display, or all of them), front to back.
        public static List<IntPtr> AppWindows(IntPtr onMonitor)
        {
            var all = App.Wins.Select(w => w.Hwnd).Concat(App.Finders.Where(f => f.IsVisible).Select(f => new WindowInteropHelper(f).Handle)).Distinct().ToList();
            var z = new Dictionary<IntPtr, int>(); int n = 0;
            Native.EnumWindows((h, l) => { z[h] = n++; return true; }, IntPtr.Zero);
            return all.Where(h => Native.IsWindow(h) && Native.IsWindowVisible(h) && !Native.IsIconic(h) && !Native.IsCloaked(h) && (onMonitor == IntPtr.Zero || Native.MonitorFromWindow(h, 2) == onMonitor))
                .OrderBy(h => z.ContainsKey(h) ? z[h] : int.MaxValue).ToList();
        }

        static Sys.Display Screen { get { return Sys.Screens().FirstOrDefault(s => s.Monitor == monitor) ?? DisplayLayout.Selected; } }

        static void Enable()
        {
            var front = Tiling.Front();
            monitor = front != IntPtr.Zero ? Native.MonitorFromWindow(front, 2) : DisplayLayout.Selected.Monitor;
            stage = AppKey(front);
            on = true;
            busy = true;
            try
            {
                foreach (var h in AppWindows(monitor).Where(h => AppKey(h) != stage && !Parked(h)).Reverse()) Send(h);   // back to front: the frontmost ends up most recent
                foreach (var h in AppWindows(monitor).Where(h => AppKey(h) == stage)) Clear(h);
            }
            finally { busy = false; }
            Save();
            if (strip == null) strip = new StageStrip();
            strip.Place(Screen);
            Refresh();
            Later(900, Prune);
        }

        static void Disable()
        {
            on = false;
            foreach (var a in away.ToList()) Back(a);
            away.Clear(); recent.Clear(); stage = null;
            if (strip != null) strip.Clear();
            try { if (System.IO.File.Exists(File)) System.IO.File.Delete(File); } catch (Exception ex) { App.Log(ex); }
        }

        // OpenDock is quitting: every window goes back (Stage Manager comes back on at the next start).
        public static void Shutdown()
        {
            if (!on) return;
            on = false;
            foreach (var a in away.ToList()) Back(a);
            away.Clear();
            try { if (System.IO.File.Exists(File)) System.IO.File.Delete(File); } catch { }
        }

        static int ParkX(Native.RECT r) { return GetSystemMetrics(76 /* SM_XVIRTUALSCREEN */) - (r.Right - r.Left) - 400; }
        static bool Parked(IntPtr h) { Native.RECT r; return Native.GetWindowRect(h, out r) && r.Right < GetSystemMetrics(76) - 100; }

        // A window leaves the stage for the strip.
        static void Send(IntPtr h)
        {
            Native.RECT r;
            if (!Native.GetWindowRect(h, out r)) return;
            string key = AppKey(h);
            if (key == null) return;
            // always-on-top windows (a picture-in-picture video) and full-screen ones (games) stay where they are
            if ((Native.GetWindowLong(h, Native.GWL_EXSTYLE) & 0x8 /* WS_EX_TOPMOST */) != 0) return;
            var s = Screen;
            if ((Native.GetWindowLong(h, Native.GWL_STYLE) & 0x00C00000) == 0 && r.Left <= s.Bounds.Left && r.Top <= s.Bounds.Top && r.Right >= s.Bounds.Right && r.Bottom >= s.Bounds.Bottom) return;
            if (!away.Any(a => a.Hwnd == h)) away.Add(new Away { Hwnd = h, Rect = r, Key = key, Order = ++order });
            Native.SetWindowPos(h, IntPtr.Zero, ParkX(r), r.Top, 0, 0, Native.SWP_NOSIZE | 0x4 /* NOZORDER */ | Native.SWP_NOACTIVATE | 0x4000 /* ASYNC */);
            recent.Remove(key); recent.Insert(0, key);
        }

        // A window comes back to where it was.
        static void Back(Away a)
        {
            away.Remove(a);
            if (!Native.IsWindow(a.Hwnd)) return;
            var r = Clear(a.Rect);
            Native.SetWindowPos(a.Hwnd, IntPtr.Zero, r.Left, r.Top, 0, 0, Native.SWP_NOSIZE | 0x4 | Native.SWP_NOACTIVATE | 0x4000);
        }

        // Keep the stage clear of the strip: a window that would sit under it moves right, if it fits.
        static Native.RECT Clear(Native.RECT r)
        {
            if (!on || strip == null) return r;
            var s = Screen;
            int edge = s.Work.Left + (int)Math.Round((StageStrip.W + 8) * App.Dpi);
            int w = r.Right - r.Left;
            if (r.Left < edge && r.Right > s.Work.Left && edge + w <= s.Work.Right) { r.Left = edge; r.Right = edge + w; }
            return r;
        }

        static void Clear(IntPtr h)
        {
            if (Native.IsZoomed(h)) return;
            Native.RECT r;
            if (!Native.GetWindowRect(h, out r)) return;
            var c = Clear(r);
            if (c.Left != r.Left) Native.SetWindowPos(h, IntPtr.Zero, c.Left, c.Top, 0, 0, Native.SWP_NOSIZE | 0x4 | Native.SWP_NOACTIVATE | 0x4000);
        }

        // An app takes the stage: everything else on this display goes to the strip, its windows come back.
        static void Bring(string key, IntPtr focus)
        {
            busy = true;
            try
            {
                foreach (var h in AppWindows(monitor).Where(h => AppKey(h) != key && !Parked(h)).Reverse()) Send(h);
                var mine = away.Where(a => a.Key == key).OrderByDescending(a => a.Order).ToList();   // backmost first
                foreach (var a in mine) Back(a);
                foreach (var a in mine) Native.SetWindowPos(a.Hwnd, IntPtr.Zero, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | 0x4000 /* ASYNC */);   // HWND_TOP, in order
                recent.Remove(key);
                stage = key;
                if (focus == IntPtr.Zero && mine.Count > 0) focus = mine[mine.Count - 1].Hwnd;
                if (focus != IntPtr.Zero && Native.GetForegroundWindow() != focus) Native.Activate(focus);
            }
            finally { busy = false; }
            Save();
            Refresh();
            Later(900, Prune);
        }

        // Picked in the strip.
        public static void Pick(string key)
        {
            if (!on) return;
            Bring(key, IntPtr.Zero);
        }

        static void OnForeground(IntPtr h)
        {
            if (!on || busy) return;
            try
            {
                h = Native.GetAncestor(h, 2);
                string key = AppKey(h);
                if (key == null) return;
                var a = away.FirstOrDefault(x => x.Hwnd == h);
                if (a == null && Parked(h))
                {
                    // a window that was parked but slipped out of the list (it was hidden for a while): bring it back whole
                    Native.RECT r; Native.GetWindowRect(h, out r);
                    var s = Screen; int w = r.Right - r.Left, ht = r.Bottom - r.Top;
                    a = new Away { Hwnd = h, Key = key, Order = ++order, Rect = new Native.RECT { Left = (s.Work.Left + s.Work.Right - w) / 2, Top = (s.Work.Top + s.Work.Bottom - ht) / 2 } };
                    a.Rect.Right = a.Rect.Left + w; a.Rect.Bottom = a.Rect.Top + ht;
                    away.Add(a);
                }
                if (a == null && Native.MonitorFromWindow(h, 2) != monitor) return;   // Stage Manager looks after one display
                if (key == stage) { if (a != null) { Back(a); Save(); Refresh(); } return; }
                Bring(key, h);
            }
            catch (Exception ex) { App.Log(ex); }
        }

        // Closed windows leave; apps with nothing left leave the strip. A window that isn't parked a moment after it
        // was sent (Windows won't let OpenDock move apps running as administrator) stays on stage instead.
        static void Prune()
        {
            if (!on) return;
            int before = away.Count + recent.Count;
            away.RemoveAll(a => !Native.IsWindow(a.Hwnd) || ((DateTime.UtcNow - a.When).TotalMilliseconds > 700 && !Parked(a.Hwnd) && Native.IsWindowVisible(a.Hwnd)));
            recent.RemoveAll(k => !away.Any(a => a.Key == k));
            if (away.Count + recent.Count != before) { Save(); Refresh(); }
        }

        static void Later(int ms, Action a)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += delegate { t.Stop(); try { a(); } catch (Exception ex) { App.Log(ex); } };
            t.Start();
        }

        static void Refresh()
        {
            if (strip == null || !on) return;
            var dock = App.Docks.FirstOrDefault();
            var groups = new List<StageStrip.Group>();
            foreach (var key in recent.Take(MaxApps))
            {
                var wins = away.Where(a => a.Key == key && Native.IsWindow(a.Hwnd)).OrderByDescending(a => a.Order).Select(a => a.Hwnd).ToList();
                if (wins.Count == 0) continue;
                string name = null; ImageSource icon = null;
                if (dock != null) dock.Describe(wins[0], out name, out icon);
                if (key == "opendock:finder") { name = "Finder"; icon = Art.Finder; }
                groups.Add(new StageStrip.Group { Key = key, Name = name ?? Native.WindowText(wins[0]), Icon = icon, Windows = wins });
            }
            strip.Show(groups);
        }

        static void Save()
        {
            try
            {
                if (away.Count == 0) { if (System.IO.File.Exists(File)) System.IO.File.Delete(File); return; }
                Directory.CreateDirectory(Settings.DataDir);
                Workspace.AtomicWrite(File, string.Join(Environment.NewLine, away.Select(a => a.Hwnd.ToInt64() + "\t" + a.Rect.Left + "\t" + a.Rect.Top)) + Environment.NewLine);
            }
            catch (Exception ex) { App.Log(ex); }
        }

        // After a crash: put back every window that was off stage.
        static void Recover()
        {
            try
            {
                if (!System.IO.File.Exists(File)) return;
                foreach (var line in System.IO.File.ReadAllLines(File))
                {
                    var p = line.Split('\t');
                    long h; int x, y;
                    if (p.Length < 3 || !long.TryParse(p[0], out h) || !int.TryParse(p[1], out x) || !int.TryParse(p[2], out y)) continue;
                    var hw = new IntPtr(h);
                    if (Native.IsWindow(hw) && Parked(hw)) Native.SetWindowPos(hw, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | 0x4 | Native.SWP_NOACTIVATE | 0x4000);
                }
                System.IO.File.Delete(File);
            }
            catch (Exception ex) { App.Log(ex); }
        }
    }

    // The strip on the left: for each recent app, its windows stacked as live pictures with the app's icon beside
    // them. The window itself is see-through (DWM draws the pictures on top of it), sits with the other windows so
    // the app on stage can cover it, and comes forward when the pointer touches the screen's left edge.
    class StageStrip : Window
    {
        [StructLayout(LayoutKind.Sequential)]
        struct THUMB_PROPS { public int dwFlags; public Native.RECT rcDestination, rcSource; public byte opacity; public bool fVisible, fSourceClientAreaOnly; }
        [StructLayout(LayoutKind.Sequential)] struct MARGINS { public int l, r, t, b; }
        [DllImport("dwmapi.dll")] static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
        [DllImport("dwmapi.dll")] static extern int DwmUnregisterThumbnail(IntPtr thumb);
        [DllImport("dwmapi.dll")] static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref THUMB_PROPS p);
        [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref MARGINS m);

        public class Group { public string Key, Name; public ImageSource Icon; public List<IntPtr> Windows; }

        public const double W = 196, SlotH = 120, SlotGap = 18, Top0 = 26;
        readonly Canvas canvas = new Canvas { Background = Brushes.Transparent };
        readonly List<IntPtr> thumbs = new List<IntPtr>();
        readonly DispatcherTimer edge = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        IntPtr hwnd;
        bool raised;
        List<Group> last = new List<Group>();

        public StageStrip()
        {
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; ShowActivated = false;
            Background = Brushes.Transparent; Title = "Stage Manager"; FontFamily = Theme.Font;
            Content = canvas;
            SourceInitialized += delegate
            {
                hwnd = new WindowInteropHelper(this).Handle;
                Native.MakeToolWindow(hwnd, true);
                var src = HwndSource.FromHwnd(hwnd);
                if (src != null && src.CompositionTarget != null) src.CompositionTarget.BackgroundColor = Colors.Transparent;
                var m = new MARGINS { l = -1, r = -1, t = -1, b = -1 };
                DwmExtendFrameIntoClientArea(hwnd, ref m);                       // see-through
                int v = 1; Native.DwmSetWindowAttribute(hwnd, 33, ref v, 4);     // square corners
                v = unchecked((int)0xFFFFFFFE); Native.DwmSetWindowAttribute(hwnd, 34, ref v, 4);   // no border
                v = 1; Native.DwmSetWindowAttribute(hwnd, 3, ref v, 4);          // no open/close animation
            };
            edge.Tick += delegate { Edge(); };
            Closed += delegate { Unregister(); };
        }

        public void Place(Sys.Display s)
        {
            double d = App.Dpi;
            Left = s.Work.Left / d; Top = s.Work.Top / d; Width = W; Height = (s.Work.Bottom - s.Work.Top) / d;
        }

        public void Clear() { Unregister(); canvas.Children.Clear(); last = new List<Group>(); edge.Stop(); Hide(); }

        void Unregister() { foreach (var t in thumbs) DwmUnregisterThumbnail(t); thumbs.Clear(); }

        public void Show(List<Group> groups)
        {
            last = groups;
            if (groups.Count == 0) { Unregister(); canvas.Children.Clear(); Hide(); return; }
            if (!IsVisible)
            {
                base.Show(); edge.Start();
                var front = Tiling.Front();   // with the other windows, under the app you're using
                if (front != IntPtr.Zero && hwnd != IntPtr.Zero) Native.SetWindowPos(hwnd, front, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
            Unregister();
            canvas.Children.Clear();
            double d = App.Dpi, y = Top0;
            int fit = Math.Max(1, (int)((ActualHeight > 0 ? ActualHeight : Height) - Top0) / (int)(SlotH + SlotGap));
            foreach (var g in groups.Take(fit))
            {
                var slot = new Rect(10, y, W - 20, SlotH);
                var group = g;
                // the hover glow and the click target (the pictures sit on top of it)
                var hot = new Border { Width = slot.Width, Height = slot.Height, CornerRadius = new CornerRadius(14), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = g.Name };
                hot.MouseEnter += delegate { hot.Background = new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)); };
                hot.MouseLeave += delegate { hot.Background = Brushes.Transparent; };
                hot.MouseLeftButtonUp += delegate { StageManager.Pick(group.Key); };
                Canvas.SetLeft(hot, slot.X); Canvas.SetTop(hot, slot.Y);
                canvas.Children.Add(hot);
                if (g.Icon != null)
                {
                    var icon = new Image { Source = g.Icon, Width = 34, Height = 34, IsHitTestVisible = false, Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.35 } };
                    Canvas.SetLeft(icon, slot.X + 2); Canvas.SetTop(icon, slot.Bottom - 38);
                    canvas.Children.Add(icon);
                }
                // up to three windows, stacked: the most recent in front, the others peeking out above and to the right
                var area = new Rect(slot.X + 38, slot.Y + 14, slot.Width - 44, slot.Height - 20);
                var wins = g.Windows.Take(3).ToList();
                for (int i = wins.Count - 1; i >= 0; i--)
                {
                    Native.RECT wr, fr;
                    if (!Native.GetWindowRect(wins[i], out wr)) continue;
                    if (Native.DwmGetWindowAttribute(wins[i], 9, out fr, 16) != 0) fr = wr;
                    double fw = Math.Max(1, fr.Right - fr.Left), fh = Math.Max(1, fr.Bottom - fr.Top);
                    double scale = 1 - 0.1 * i;
                    double k = Math.Min(area.Width * scale / fw, area.Height * scale / fh) * d;
                    double tw = fw * k / d, th = fh * k / d;
                    double x = area.X + (area.Width - tw) / 2 + 7 * i, ty = area.Bottom - th - 9 * i;
                    IntPtr t;
                    if (DwmRegisterThumbnail(hwnd, wins[i], out t) != 0) continue;
                    thumbs.Add(t);
                    var p = new THUMB_PROPS
                    {
                        dwFlags = 0x1 | 0x2 | 0x4 | 0x8,
                        rcDestination = new Native.RECT { Left = (int)Math.Round(x * d), Top = (int)Math.Round(ty * d), Right = (int)Math.Round((x + tw) * d), Bottom = (int)Math.Round((ty + th) * d) },
                        rcSource = new Native.RECT { Left = fr.Left - wr.Left, Top = fr.Top - wr.Top, Right = fr.Right - wr.Left, Bottom = fr.Bottom - wr.Top },
                        opacity = (byte)(i == 0 ? 255 : 215), fVisible = true
                    };
                    DwmUpdateThumbnailProperties(t, ref p);
                }
                y += SlotH + SlotGap;
            }
        }

        // The pointer at the screen's left edge brings the strip forward (when a window covers it); moving away
        // from it puts it back under the app you're using.
        void Edge()
        {
            if (hwnd == IntPtr.Zero || !IsVisible) return;
            Native.POINT p;
            if (!Native.GetCursorPos(out p)) return;
            double d = App.Dpi;
            double left = Left * d, top = Top * d, right = (Left + W) * d, bottom = (Top + ActualHeight) * d;
            bool inY = p.Y >= top && p.Y < bottom;
            if (!raised && inY && p.X <= left + 1 && p.X >= left - 2)
            {
                raised = true;
                Native.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);   // HWND_TOP
            }
            else if (raised && (!inY || p.X > right + 40))
            {
                raised = false;
                var front = Tiling.Front();
                if (front != IntPtr.Zero && front != hwnd) Native.SetWindowPos(hwnd, front, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
        }
    }
}
