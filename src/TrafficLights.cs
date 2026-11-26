using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace OpenDock
{
    // Mac traffic lights on every app window, in place of Windows' minimize / maximize / close buttons.
    //
    // Nothing is changed inside Windows or inside the apps: no system files, no theme patching, no code loaded into
    // other programs (so games and their anti-cheat never see GlassDock). For each window, GlassDock finds where its
    // own three caption buttons are (asking Windows' accessibility interface what's at the window's top-right corner,
    // which works for classic apps and for the ones that draw their own buttons, in any language), then lays a small
    // patch of the title bar's own colour exactly over them with the three lights on it. The patch sits in the window's
    // place in the stack (right above it, under anything covering it), moves with it, and the lights send the window
    // the same commands its own buttons would.
    static class TrafficLights
    {
        static readonly Dictionary<IntPtr, LightsWindow> shown = new Dictionary<IntPtr, LightsWindow>();
        static readonly HashSet<IntPtr> probing = new HashSet<IntPtr>();
        static readonly Dictionary<IntPtr, DateTime> unsupported = new Dictionary<IntPtr, DateTime>();   // when each was last tried
        static Native.WinEventProc onMove, onFore, onMoveSize, onReorder;   // kept alive: Windows calls them
        static bool restackQueued;
        static DispatcherTimer sweep;
        static readonly uint myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        [DllImport("user32.dll")] static extern bool IsHungAppWindow(IntPtr h);

        public static void Start()
        {
            onMove = App.Guard((hk, ev, h, obj, child, t, time) => { if (obj == 0 && child == 0) { LightsWindow w; if (shown.TryGetValue(h, out w)) w.Follow(); } });
            // a window that came up empty is tried again when it's brought forward (its corner may have been covered)
            onFore = App.Guard((hk, ev, h, obj, child, t, time) => { unsupported.Remove(h); foreach (var w in shown.Values.ToList()) w.Follow(); });
            Native.SetWinEventHook(0x800B, 0x800B, IntPtr.Zero, onMove, 0, 0, 2);   // EVENT_OBJECT_LOCATIONCHANGE
            Native.SetWinEventHook(0x0003, 0x0003, IntPtr.Zero, onFore, 0, 0, 2);   // EVENT_SYSTEM_FOREGROUND
            Native.SetWinEventHook(0x0016, 0x0017, IntPtr.Zero, onFore, 0, 0, 2);   // minimize start / end
            // a window being dragged or resized: its lights follow it every frame until it's let go
            onMoveSize = App.Guard((hk, ev, h, obj, child, t, time) =>
            {
                if (obj != 0 || child != 0) return;
                LightsWindow w; if (shown.TryGetValue(h, out w)) w.Tracking(ev == 0x000A);
            });
            Native.SetWinEventHook(0x000A, 0x000B, IntPtr.Zero, onMoveSize, 0, 0, 2);   // EVENT_SYSTEM_MOVESIZESTART / END
            // windows changed places in the stack (one brought forward, another sent back): every patch goes straight
            // back right above its own window, so the buttons under it never show through (at most once per pass of the UI loop)
            onReorder = App.Guard((hk, ev, h, obj, child, t, time) =>
            {
                if (restackQueued || shown.Count == 0) return;
                restackQueued = true;
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => { restackQueued = false; foreach (var w in shown.Values.ToList()) w.Follow(); }), System.Windows.Threading.DispatcherPriority.Send);
            });
            Native.SetWinEventHook(0x8004, 0x8004, IntPtr.Zero, onReorder, 0, 0, 2);   // EVENT_OBJECT_REORDER
            sweep = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            sweep.Tick += delegate { Sweep(); };
            App.WindowsChanged += delegate { Sweep(); };
            Settings.Changed += Sweep;
            sweep.Start();
            Sweep();
        }

        public static void Shutdown() { foreach (var w in shown.Values.ToList()) w.Close(); shown.Clear(); }

        // Which windows get lights: visible app windows with their own caption buttons -- not GlassDock's (they have
        // lights already), not full-screen ones (games, videos), not ones running as administrator (Windows wouldn't
        // let the lights press their buttons).
        // An ordinary app window GlassDock may restyle (the rules the lights use), for the window corners too.
        internal static bool Candidate(IntPtr h) { return Wanted(h); }
        internal static uint DpiOf(IntPtr h) { return GetDpiForWindow(h); }

        static bool Wanted(IntPtr h)
        {
            if (!Native.IsWindow(h) || !Native.IsWindowVisible(h) || Native.IsIconic(h) || Native.IsCloaked(h) || IsHungAppWindow(h)) return false;
            int style = Native.GetWindowLong(h, Native.GWL_STYLE), ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
            // a window with buttons: WS_SYSMENU, or a minimize / maximize box (Electron apps like Claude drop WS_SYSMENU)
            if ((style & 0x00080000) == 0 && (style & 0x00030000) == 0) return false;
            if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return false;
            uint pid; Native.GetWindowThreadProcessId(h, out pid);
            if (pid == myPid || Elevated(pid)) return false;
            var screen = Sys.Screens().FirstOrDefault(s => s.Monitor == Native.MonitorFromWindow(h, 2));
            Native.RECT r;
            if (screen != null && Native.GetWindowRect(h, out r) && !Native.IsZoomed(h) && r.Left <= screen.Bounds.Left && r.Top <= screen.Bounds.Top && r.Right >= screen.Bounds.Right && r.Bottom >= screen.Bounds.Bottom) return false;
            return true;
        }

        static void Sweep()
        {
            try
            {
                var want = Settings.TrafficLights ? App.Wins.Select(w => w.Hwnd).Where(Wanted).ToList() : new List<IntPtr>();
                // a window that's only resting (minimized, hidden, busy, full screen) keeps its lights, out of sight, so
                // they're back the moment it is; they go when the window does, or when the lights are turned off
                foreach (var h in shown.Keys.Where(k => !want.Contains(k)).ToList())
                {
                    if (Settings.TrafficLights && Native.IsWindow(h)) { shown[h].Suspend(true); continue; }
                    shown[h].Close(); shown.Remove(h);
                }
                foreach (var h in unsupported.Keys.Where(k => !Native.IsWindow(k)).ToList()) unsupported.Remove(h);
                foreach (var h in want)
                {
                    LightsWindow w;
                    if (shown.TryGetValue(h, out w)) { w.Suspend(false); w.Check(); continue; }
                    // a window that came up empty is looked at again every few seconds (its corner may have been covered)
                    DateTime tried;
                    if (!probing.Contains(h) && (!unsupported.TryGetValue(h, out tried) || (DateTime.UtcNow - tried).TotalSeconds > 5)) Probe(h, null);
                }
            }
            catch (Exception ex) { App.Log(ex); }
        }

        // Finds the window's three caption buttons, off the UI thread: the button at the top-right corner (close),
        // then the two buttons just left of it. Their places are kept relative to the window's top-right corner.
        internal static void Probe(IntPtr h, LightsWindow existing)
        {
            if (!probing.Add(h)) return;   // one lookup per window at a time
            Native.RECT wr; Native.GetWindowRect(h, out wr);
            Native.RECT fr; if (Native.DwmGetWindowAttribute(h, 9, out fr, 16) != 0) fr = wr;
            bool zoomed = Native.IsZoomed(h);
            var ui = Dispatcher.CurrentDispatcher;
            Task.Run(() =>
            {
                // first ask the window itself (works for nearly every app, even covered by another window), then
                // what Windows says about its title bar, then what the accessibility interface sees at the corner
                Rect? found = null;
                try { found = HitScan(h, fr); } catch { }
                if (found == null) try { found = FromTitleBar(h, fr); } catch { }
                if (found == null) try { found = Buttons(h, fr); } catch { }
                ui.BeginInvoke(new Action(() =>
                {
                    probing.Remove(h);
                    // the patch this lookup was for has since closed or been replaced: leave the newer state alone
                    LightsWindow current;
                    if (existing != null && (existing.Gone || !shown.TryGetValue(h, out current) || current != existing)) return;
                    if (found == null)
                    {
                        if (existing != null)
                        {
                            // the window was busy or mid-animation: look again (from Check) a couple of times first
                            if (++existing.Misses < 3) { existing.NeedsProbe = true; return; }
                            existing.Close(); shown.Remove(h);
                        }
                        unsupported[h] = DateTime.UtcNow; return;
                    }
                    unsupported.Remove(h);
                    var area = found.Value;   // relative to the frame's top-right corner (x <= 0)
                    if (existing != null) { existing.Set(area, zoomed); return; }
                    if (!Settings.TrafficLights || !Wanted(h) || shown.ContainsKey(h)) return;
                    var lw = new LightsWindow(h, area, zoomed);
                    shown[h] = lw;
                    new WindowInteropHelper(lw).EnsureHandle();   // shown by Follow once there's a picture under the lights
                    lw.Follow();
                }));
            });
        }

        static Rect? Buttons(IntPtr h, Native.RECT fr)
        {
            uint targetPid; Native.GetWindowThreadProcessId(h, out targetPid);
            Func<int, int, Rect?> button = (x, y) =>
            {
                var e = AutomationElement.FromPoint(new Point(x, y));
                if (e == null) return null;
                var info = e.Current;
                if (info.ProcessId == (int)myPid || info.ProcessId != (int)targetPid) return null;   // only the window's own buttons (another app's may be over the corner)
                if (info.ControlType != ControlType.Button && info.ControlType != ControlType.MenuItem) return null;
                var b = info.BoundingRectangle;
                if (b.IsEmpty || b.Width < 16 || b.Width > 90 || b.Height < 16 || b.Height > 70) return null;
                return b;
            };
            int cy = fr.Top + 14;
            // the close button: at the very top-right (a few pixels in, since some apps leave a hairline border)
            Rect? close = null;
            foreach (int dx in new[] { 6, 14, 24 }) { close = button(fr.Right - dx, cy); if (close != null) break; }
            if (close == null || close.Value.Right < fr.Right - 30 || close.Value.Top > fr.Top + 24) return null;
            var c = close.Value; int mid = (int)(c.Top + c.Height / 2);
            var max = button((int)c.Left - 6, mid);
            if (max == null || Math.Abs(max.Value.Height - c.Height) > 4 || max.Value.Right < c.Left - 6) return null;
            var min = button((int)max.Value.Left - 6, mid);
            if (min == null || Math.Abs(min.Value.Height - c.Height) > 4 || min.Value.Right < max.Value.Left - 6) return null;
            var all = new Rect(min.Value.Left, c.Top, c.Right - min.Value.Left, c.Height);
            // real caption buttons are ~46 px each; a tight cluster of small web buttons (the NVIDIA app) can't hold the lights
            uint dpi = GetDpiForWindow(h); double k = (dpi == 0 ? 96 : dpi) / 96.0;
            if (all.Width < LightsWindow.RowWidth * k) return null;
            return new Rect(all.Left - fr.Right, all.Top - fr.Top, all.Width, all.Height);
        }

        // Asks the window what each point along the top of its frame is, the way Windows does for the mouse. Every
        // app that supports Windows 11's Snap Layouts -- classic ones, Chrome and Edge, Electron apps that draw their
        // own title bar, Windows Terminal, Store apps -- answers "minimize / maximize / close button" over its
        // buttons, wherever it draws them and even when another window covers the corner. The three answers must
        // sit side by side, the close button at the frame's top-right, minimize at the left of the run.
        const int HTMIN = 8, HTMAX = 9, HTCLOSE = 20;
        static Rect? HitScan(IntPtr h, Native.RECT fr)
        {
            uint dpi = GetDpiForWindow(h); double k = (dpi == 0 ? 96 : dpi) / 96.0;
            // rows a little way down the title bar (the top few pixels are often the resize border), the most likely first
            foreach (int dy in new[] { 14, 20, 10, 28, 6 })
            {
                int cy = fr.Top + (int)Math.Round(dy * k);
                foreach (int dx in new[] { 6, 12, 20 })
                {
                    int r = HitAt(h, fr.Right - dx, cy);
                    if (r == -1) return null;   // not answering: leave it
                    if (r != HTCLOSE) continue;
                    var found = Row(h, fr, fr.Right - dx, cy, k);
                    if (found != null) return found;
                    break;
                }
            }
            return null;
        }

        // From a point on the close button: the run of close, maximize and minimize along its row, then its height.
        static Rect? Row(IntPtr h, Native.RECT fr, int cx, int cy, double k)
        {
            int step = Math.Max(1, (int)Math.Round(2 * k)), reach = (int)(70 * k);
            // (some apps leave a pixel or two of title bar between their buttons: small gaps are stepped over)
            int closeLeft = cx, maxLeft = -1, minLeft = -1, state = HTCLOSE, gap = 0, maxGap = (int)Math.Ceiling(4 * k);
            for (int x = cx - step; x > fr.Right - (int)(420 * k); x -= step)
            {
                int r = HitAt(h, x, cy);
                if (r == -1) return null;
                if (state == HTCLOSE && r == HTCLOSE) closeLeft = x;
                else if ((state == HTCLOSE || state == HTMAX) && r == HTMAX) { state = HTMAX; maxLeft = x; }
                else if ((state == HTMAX || state == HTMIN) && r == HTMIN) { state = HTMIN; minLeft = x; }
                else if ((gap += step) > maxGap) break;
                else continue;
                gap = 0;
            }
            if (minLeft < 0) return null;
            int top = cy, bottom = cy;
            while (top > fr.Top && cy - top < reach && HitAt(h, cx, top - 1) == HTCLOSE) top--;
            while (bottom - cy < reach && HitAt(h, cx, bottom + 1) == HTCLOSE) bottom++;
            int width = fr.Right - minLeft, height = bottom - top + 1;
            // a believable row of caption buttons, wide enough for the lights
            if (width < LightsWindow.RowWidth * k || width > 420 * k || height < 16 * k || height > reach || top > fr.Top + 24 * k) return null;
            if (closeLeft - maxLeft < 16 * k || maxLeft - minLeft < 16 * k) return null;
            return new Rect(minLeft - fr.Right, top - fr.Top, width, height);
        }

        // Some apps answer from a small window of their own laid over the title bar (Windows Terminal's drag bar),
        // so when the window itself doesn't name a button there, its child windows at that point are asked too.
        static int HitAt(IntPtr h, int x, int y)
        {
            int r = AskHit(h, x, y);
            if (r == -1 || r == HTMIN || r == HTMAX || r == HTCLOSE) return r;
            int asked = 0;
            for (var c = GetWindow(h, 5 /* GW_CHILD */); c != IntPtr.Zero && asked < 24; c = GetWindow(c, 2 /* GW_HWNDNEXT */))
            {
                Native.RECT cr;
                if (!Native.IsWindowVisible(c) || !Native.GetWindowRect(c, out cr) || x < cr.Left || x >= cr.Right || y < cr.Top || y >= cr.Bottom) continue;
                asked++;
                int cr2 = AskHit(c, x, y);
                if (cr2 == HTMIN || cr2 == HTMAX || cr2 == HTCLOSE) return cr2;
            }
            return r;
        }

        static int AskHit(IntPtr h, int x, int y)
        {
            IntPtr res;
            if (Native.SendMessageTimeout(h, 0x0084 /* WM_NCHITTEST */, IntPtr.Zero, (IntPtr)((y << 16) | (x & 0xFFFF)), 2 /* ABORTIFHUNG */, 150, out res) == IntPtr.Zero) return -1;
            return unchecked((int)res.ToInt64());
        }
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);

        // Windows draws the buttons itself (classic apps, and new ones like Windows 11's Notepad): it says where they are.
        [StructLayout(LayoutKind.Sequential)]
        struct TITLEBARINFOEX
        {
            public int cbSize; public Native.RECT rcTitleBar;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public int[] rgstate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public Native.RECT[] rgrect;
        }
        static Rect? FromTitleBar(IntPtr h, Native.RECT fr)
        {
            int size = Marshal.SizeOf(typeof(TITLEBARINFOEX));
            IntPtr mem = Marshal.AllocHGlobal(size);
            try
            {
                var info = new TITLEBARINFOEX { cbSize = size, rgstate = new int[6], rgrect = new Native.RECT[6] };
                Marshal.StructureToPtr(info, mem, false);
                IntPtr res;
                if (Native.SendMessageTimeout(h, 0x033F /* WM_GETTITLEBARINFOEX */, IntPtr.Zero, mem, 2 /* ABORTIFHUNG */, 300, out res) == IntPtr.Zero) return null;
                info = (TITLEBARINFOEX)Marshal.PtrToStructure(mem, typeof(TITLEBARINFOEX));
                const int invisible = 0x8000;
                Native.RECT min = info.rgrect[2], close = info.rgrect[5];
                if ((info.rgstate[2] & invisible) != 0 || (info.rgstate[5] & invisible) != 0) return null;
                int w = close.Right - min.Left, ht = close.Bottom - close.Top;
                uint dpi = GetDpiForWindow(h);
                if (w < LightsWindow.RowWidth * (dpi == 0 ? 96 : dpi) / 96.0 || w > 400 || ht < 16 || ht > 70 || close.Right < fr.Right - 30 || close.Right > fr.Right + 4 || close.Top > fr.Top + 24) return null;
                // Windows answers for every captioned window, even ones that paint their own title bar over that
                // spot (the NVIDIA app and other web-page apps) -- there the lights would land on the app's own
                // controls. So ask the window itself what's at those points; only real caption buttons say so.
                if (Hit(h, close) != 20 /* HTCLOSE */ || Hit(h, min) != 8 /* HTMINBUTTON */) return null;
                return new Rect(min.Left - fr.Right, close.Top - fr.Top, w, ht);
            }
            finally { Marshal.FreeHGlobal(mem); }
        }

        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);

        static int Hit(IntPtr h, Native.RECT r)
        {
            int x = (r.Left + r.Right) / 2, y = (r.Top + r.Bottom) / 2;
            IntPtr res;
            if (Native.SendMessageTimeout(h, 0x0084 /* WM_NCHITTEST */, IntPtr.Zero, (IntPtr)((y << 16) | (x & 0xFFFF)), 2, 300, out res) == IntPtr.Zero) return -1;
            return unchecked((int)res.ToInt64());   // ((int)IntPtr throws on a 64-bit value an odd app might return)
        }

        static bool Elevated(uint pid)
        {
            IntPtr p = Native.OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
            if (p == IntPtr.Zero) return true;   // can't even look: treat as off limits
            try
            {
                IntPtr tok;
                if (!OpenProcessToken(p, 0x0008 /* TOKEN_QUERY */, out tok)) return true;
                try { int elevated, len; return GetTokenInformation(tok, 20 /* TokenElevation */, out elevated, 4, out len) && elevated != 0 && !meElevated; }
                finally { Native.CloseHandle(tok); }
            }
            finally { Native.CloseHandle(p); }
        }
        static readonly bool meElevated = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        [DllImport("advapi32.dll")] static extern bool OpenProcessToken(IntPtr p, uint access, out IntPtr token);
        [DllImport("advapi32.dll")] static extern bool GetTokenInformation(IntPtr token, int cls, out int info, int len, out int ret);
    }

    // macOS's Move & Resize panel, opened by holding the green button: big layout pictures you can tap with a
    // finger -- the halves under "Move & Resize", fill / centre / side by side / quarters under "Fill & Arrange",
    // and a way back to the window's previous size. Used by GlassDock's own windows and the lights on other apps.
    class TileChooser : Window
    {
        static TileChooser open;
        readonly IntPtr target;

        public static void Show(IntPtr target, Point screenDip)
        {
            if (target == IntPtr.Zero || !Native.IsWindow(target)) return;
            if (open != null) open.Close();
            open = new TileChooser(target);
            open.Left = screenDip.X - 12; open.Top = screenDip.Y + 6;
            open.Show();
            open.Activate();
            open.KeepOnScreen();
        }

        TileChooser(IntPtr target)
        {
            this.target = target;
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false; Topmost = true; SizeToContent = SizeToContent.WidthAndHeight; FontFamily = Theme.Font; Title = "Move & Resize";
            var stack = new StackPanel { Margin = new Thickness(12, 10, 12, 10) };
            stack.Children.Add(Heading("Move & Resize"));
            stack.Children.Add(Row(new[] { Tiling.Spot.Left, Tiling.Spot.Right, Tiling.Spot.Top, Tiling.Spot.Bottom }));
            stack.Children.Add(Heading("Fill & Arrange"));
            var arrange = Row(new[] { Tiling.Spot.Fill, Tiling.Spot.Center });
            arrange.Children.Add(Button(Pair(true), "Left & Right", () => Arrange(() => Tiling.Arrange(Tiling.Spot.Left, Tiling.Spot.Right))));
            arrange.Children.Add(Button(Quads(), "Quarters", () => Arrange(Tiling.Quarters)));
            stack.Children.Add(arrange);
            var back = new TextBlock { Text = "Return to Previous Size", FontSize = 13, Margin = new Thickness(6, 8, 6, 2), Cursor = Cursors.Hand, Padding = new Thickness(4, 6, 4, 6) };
            back.SetResourceReference(TextBlock.ForegroundProperty, "MenuFg");
            back.MouseLeftButtonUp += delegate { Tiling.Move(target, Tiling.Spot.Restore); Close(); };
            stack.Children.Add(back);
            var card = new Border { Child = stack, CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), Margin = new Thickness(18),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Direction = 270, Opacity = 0.3 } };
            card.SetResourceReference(Border.BackgroundProperty, "MenuBg");
            card.SetResourceReference(Border.BorderBrushProperty, "MenuBorder");
            card.Opacity = 0;   // until it has its glass: then it springs in
            Content = card;
            // Liquid Glass under a light tint, taken while the card is still invisible, then the spring
            ContentRendered += delegate
            {
                try
                {
                    double dpi = App.Dpi;
                    var p0 = card.PointToScreen(new Point(0, 0));
                    var glass = Liquid.Card((int)p0.X, (int)p0.Y, (int)(card.ActualWidth * dpi), (int)(card.ActualHeight * dpi), 16 * dpi, dpi);
                    if (glass != null) { card.Background = Glass.Slice(glass, new Rect(0, 0, 1, 1), (Brush)Application.Current.Resources["MenuGlass"]); Liquid.Ink(card, Liquid.Brightness(glass)); }
                }
                catch (Exception ex) { App.Log(ex); }
                Motion.Appear(card, new Point(0.5, 0));
            };
            Deactivated += delegate { if (IsVisible) Close(); };
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
            Closed += delegate { if (open == this) open = null; };
            SourceInitialized += delegate { Native.MakeToolWindow(new WindowInteropHelper(this).Handle, false); };
        }

        void KeepOnScreen()
        {
            UpdateLayout();
            var b = DisplayLayout.Dip((Sys.Screens().FirstOrDefault(s => s.Monitor == Native.MonitorFromWindow(new WindowInteropHelper(this).Handle, 2)) ?? DisplayLayout.Selected).Work);
            Left = Math.Max(b.Left, Math.Min(Left, b.Right - ActualWidth));
            Top = Math.Max(b.Top, Math.Min(Top, b.Bottom - ActualHeight));
        }

        // the side-by-side layouts work on the front window: bring this one forward first
        void Arrange(Action a) { Native.Activate(target); Close(); Dispatcher.BeginInvoke(a, System.Windows.Threading.DispatcherPriority.Background); }

        static TextBlock Heading(string text)
        {
            var t = new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 4, 6, 6) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "MenuFgDim");
            return t;
        }

        StackPanel Row(Tiling.Spot[] spots)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            foreach (var s in spots) { var spot = s; row.Children.Add(Button(MacWindow.Diagram(spot), Tiling.Name(spot), () => { Tiling.Move(target, spot); Close(); })); }
            return row;
        }

        // a finger-sized button (56 x 44 DIP) with the layout drawn large
        static Border Button(FrameworkElement picture, string tip, Action click)
        {
            var b = new Border { Width = 56, Height = 44, Margin = new Thickness(3), CornerRadius = new CornerRadius(10), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = tip,
                Child = new Viewbox { Width = 34, Height = 24, Child = picture } };
            b.MouseEnter += delegate { b.SetResourceReference(Border.BackgroundProperty, "Hover"); };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; };
            b.MouseLeftButtonUp += (s, e) => { e.Handled = true; click(); };
            return b;
        }

        // two windows side by side / four in quarters, drawn like Diagram's tiny screens
        static FrameworkElement Pair(bool sideBySide) { return Multi(new[] { new Rect(2.2, 2.2, 6.7, 9.6), new Rect(11.1, 2.2, 6.7, 9.6) }); }
        static FrameworkElement Quads() { return Multi(new[] { new Rect(2.2, 2.2, 6.7, 3.7), new Rect(11.1, 2.2, 6.7, 3.7), new Rect(2.2, 8.1, 6.7, 3.7), new Rect(11.1, 8.1, 6.7, 3.7) }); }
        static FrameworkElement Multi(Rect[] parts)
        {
            var c = new Grid { Width = 20, Height = 14 };
            var frame = new Border { CornerRadius = new CornerRadius(2.5), BorderThickness = new Thickness(1.1) };
            frame.SetResourceReference(Border.BorderBrushProperty, "MenuFg");
            c.Children.Add(frame);
            foreach (var r in parts)
            {
                var fill = new Border { CornerRadius = new CornerRadius(1), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(r.Left, r.Top, 0, 0), Width = r.Width, Height = r.Height };
                fill.SetResourceReference(Border.BackgroundProperty, "MenuFg");
                c.Children.Add(fill);
            }
            return c;
        }
    }

    class LightsWindow : Window
    {
        readonly IntPtr target;
        Rect area; bool zoomed;
        IntPtr hwnd;
        readonly Border patch = new Border();
        readonly Border catcher = new Border();   // the patch's full spot, under it: holds the pointer where the patch leaves the window's edge showing
        readonly Ellipse[] dots = new Ellipse[3];
        readonly TextBlock[] marks = new TextBlock[3];
        static readonly Color[] On = { Color.FromRgb(0xFF, 0x5F, 0x57), Color.FromRgb(0xFE, 0xBC, 0x2E), Color.FromRgb(0x28, 0xC8, 0x40) };
        const double D = 17, Gap = 9;   // bigger than a Mac's: they sit where Windows' wide buttons were
        public const double RowWidth = 3 * D + 2 * Gap + 14;   // the lights plus their right margin, in DIPs
        bool hover, active = true;
        DateTime sampled = DateTime.MinValue;
        // touch: how much the lights grow under a finger, and the timers for holding / resting on green
        const double Grow = 1.75;
        readonly ScaleTransform growT = new ScaleTransform(1, 1);
        bool grown, held;
        FrameworkElement green;
        readonly System.Windows.Threading.DispatcherTimer hold = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
        readonly System.Windows.Threading.DispatcherTimer rest = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        readonly System.Windows.Threading.DispatcherTimer watch = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        readonly System.Windows.Threading.DispatcherTimer resample = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        readonly System.Windows.Threading.DispatcherTimer shrink = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
        IntPtr monitor;
        public bool Gone { get; private set; }
        public int Misses;          // lookups in a row that found no buttons (the window busy or mid-animation)
        public bool NeedsProbe;     // the buttons are to be found again (maximized, restored, another display)
        bool suspended;             // the window is resting: lights out of sight until it's back

        // Out of sight by moving off the screen, never by hiding: WPF shows and hides a window through its render
        // thread, and doing that over and over (every minimize, restore, maximize and lookup, for every window) is
        // where a struggling graphics driver made it fail in a way that took all of GlassDock down. A window that
        // is shown once and only ever moved never goes through that.
        bool shownOnce, concealed;
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
        void Conceal()
        {
            if (!shownOnce || concealed || hwnd == IntPtr.Zero) return;
            concealed = true;
            Native.SetWindowPos(hwnd, IntPtr.Zero, -32000, -32000, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | 0x4 /* NOZORDER */);
        }

        public void Suspend(bool on)
        {
            if (suspended == on) return;
            suspended = on;
            if (on) Conceal(); else Follow();
        }

        // Hidden until the buttons have been found again (their place has changed).
        void Reprobe()
        {
            NeedsProbe = true;
            Conceal();
            TrafficLights.Probe(target, this);
        }

        // Big lights for a fingertip (with their symbols), springing back after the finger lifts.
        void SetGrown(bool on)
        {
            if (on) shrink.Stop();
            if (grown == on) return;
            grown = on;
            Paint();
            if (on) Follow();   // the room to grow into first
            var a = new DoubleAnimation(on ? Grow : 1, TimeSpan.FromMilliseconds(600)) { EasingFunction = new Spring() };
            if (!on) a.Completed += delegate { if (!grown) Follow(); };   // then give the room back
            growT.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            growT.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        // Move & Resize, under the green light.
        void OpenTiles()
        {
            if (green == null || !green.IsVisible) return;
            var p = green.PointToScreen(new Point(0, green.ActualHeight));
            double k = VisualTreeHelper.GetDpi(this).DpiScaleX;
            shrink.Stop();
            TileChooser.Show(target, new Point(p.X / k, p.Y / k));
            if (grown) shrink.Start();
        }

        // While the window is being dragged or resized, the lights are moved with it every frame (the events
        // Windows sends about it arrive late and in bursts).
        bool tracking;
        public void Tracking(bool on)
        {
            if (on == tracking) return;
            tracking = on;
            if (on) CompositionTarget.Rendering += OnFrame; else { CompositionTarget.Rendering -= OnFrame; Follow(); }
        }
        void OnFrame(object s, EventArgs e) { if (Gone || !Native.IsWindow(target)) Tracking(false); else Follow(); }
        protected override void OnClosed(EventArgs e) { Gone = true; resample.Stop(); watch.Stop(); base.OnClosed(e); }

        public LightsWindow(IntPtr target, Rect area, bool zoomed)
        {
            this.target = target; this.area = area; this.zoomed = zoomed;
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false; ShowActivated = false; Focusable = false; Title = "GlassDock Traffic Lights";
            // each light where the Windows button with the same job was: yellow (minimize), green (maximize), red (close) on the right
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 14, 0) };
            string[] glyphs = { "✕", "−", "+" };
            foreach (int i in new[] { 1, 2, 0 })
            {
                int which = i;
                var g = new Grid { Width = D, Height = D, Margin = new Thickness(0, 0, i != 0 ? Gap : 0, 0), Cursor = Cursors.Arrow, Background = Brushes.Transparent };
                dots[i] = new Ellipse { Width = D, Height = D, StrokeThickness = 0.6 };
                marks[i] = new TextBlock { Text = glyphs[i], FontSize = i == 0 ? 10 : 13, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, i == 0 ? 0 : -2, 0, 0), Opacity = 0, IsHitTestVisible = false };
                // the reflections: light caught in the bottom of the bead, and a bright window reflection across its top
                var glow = new Ellipse { Width = D * 0.8, Height = D * 0.5, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 1), IsHitTestVisible = false,
                    Fill = new RadialGradientBrush(Color.FromArgb(110, 255, 255, 255), Color.FromArgb(0, 255, 255, 255)) { Center = new Point(0.5, 0.85), GradientOrigin = new Point(0.5, 0.95), RadiusX = 0.6, RadiusY = 0.7 } };
                var shine = new Ellipse { Width = D * 0.66, Height = D * 0.42, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, D * 0.07, 0, 0), IsHitTestVisible = false,
                    Fill = new LinearGradientBrush(Color.FromArgb(225, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90) };
                g.Children.Add(dots[i]); g.Children.Add(glow); g.Children.Add(shine); g.Children.Add(marks[i]);
                g.PreviewMouseLeftButtonDown += (s, e) =>
                {
                    if (e.StylusDevice != null) SetGrown(true);   // a finger or pen: big lights, as on a Mac's touchscreen
                    if (which == 2) { held = false; hold.Start(); }
                };
                g.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true; hold.Stop();
                    if (which == 2 && held) { held = false; return; }   // the hold opened Move & Resize instead
                    Press(which);
                };
                if (which == 2)
                {
                    green = g;
                    g.MouseEnter += delegate { if (Mouse.LeftButton != MouseButtonState.Pressed) rest.Start(); };
                    g.MouseLeave += delegate { rest.Stop(); hold.Stop(); };
                }
                row.Children.Add(g);
            }
            row.MouseEnter += delegate { hover = true; Paint(); };
            row.MouseLeave += delegate { hover = false; Paint(); };
            // touch: grow on the first touch, shrink a moment after the last finger lifts
            row.PreviewTouchDown += delegate { SetGrown(true); };
            row.TouchUp += delegate { shrink.Stop(); shrink.Start(); };
            row.TouchLeave += delegate { shrink.Stop(); shrink.Start(); };
            row.RenderTransform = growT; row.RenderTransformOrigin = new Point(1, 0);
            hold.Tick += delegate { hold.Stop(); held = true; OpenTiles(); };
            rest.Tick += delegate { rest.Stop(); if (green != null && green.IsMouseOver && Mouse.LeftButton != MouseButtonState.Pressed) OpenTiles(); };
            shrink.Tick += delegate { shrink.Stop(); SetGrown(false); };
            resample.Tick += delegate { resample.Stop(); sampled = DateTime.MinValue; Follow(); };
            // keeps looking at the title bar under the lights (its colour changes: active / inactive, a tab, a theme)
            // the front window's title bar twice a second (it changes most: hover, tabs, focus); the rest every 3 s
            watch.Tick += delegate { if (!Gone && shownOnce && !concealed && !tracking && !suspended && !NeedsProbe && (active || (DateTime.UtcNow - sampled).TotalSeconds > 3)) Follow(); };
            watch.Start();
            RenderOptions.SetBitmapScalingMode(patch, BitmapScalingMode.NearestNeighbor);
            patch.Child = row;
            patch.HorizontalAlignment = HorizontalAlignment.Right; patch.VerticalAlignment = VerticalAlignment.Top;
            // (the window can be bigger than the patch while the lights are grown: the rest of it is transparent,
            // and clicks there go straight through to the app)
            var holder = new Grid();
            catcher.HorizontalAlignment = HorizontalAlignment.Right; catcher.VerticalAlignment = VerticalAlignment.Top;
            catcher.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));   // invisible, but takes the pointer
            holder.Children.Add(catcher);
            holder.Children.Add(patch);
            Content = holder;
            SourceInitialized += delegate
            {
                hwnd = new WindowInteropHelper(this).Handle;
                Native.MakeToolWindow(hwnd, true);
                Glass.Exclude(hwnd);   // out of screen capture (so the title bar under it can be sampled) -- unless someone else is capturing
            };
            Paint();
        }

        // The buttons were found (again): a fresh picture of the title bar before the lights show.
        public void Set(Rect a, bool z)
        {
            area = a; zoomed = z; Misses = 0; NeedsProbe = false;
            patch.Background = null; lastHash = 0; sampled = DateTime.MinValue;
            Follow();
        }

        // The window was maximized or restored: its buttons move, so they're found again.
        public void Check()
        {
            if (!Native.IsWindow(target) || suspended) return;
            if (NeedsProbe || Native.IsZoomed(target) != zoomed) { Reprobe(); return; }
            if ((DateTime.UtcNow - sampled).TotalMilliseconds > 1500) Follow();
        }

        // Onto the buttons' spot, right above the window in the stack.
        public void Follow()
        {
            if (Gone || hwnd == IntPtr.Zero) return;
            if (suspended || NeedsProbe || !Native.IsWindow(target) || !Native.IsWindowVisible(target) || Native.IsIconic(target) || Native.IsCloaked(target)) { Conceal(); return; }
            // maximized or restored (even mid-animation): the buttons have moved, so they're found again first
            if (Native.IsZoomed(target) != zoomed) { Reprobe(); return; }
            // moved to another display: its buttons may be another size there (another DPI), so they're found again
            var mon = Native.MonitorFromWindow(target, 2);
            if (monitor != IntPtr.Zero && mon != monitor) { monitor = mon; Reprobe(); return; }
            monitor = mon;
            Native.RECT fr; if (Native.DwmGetWindowAttribute(target, 9, out fr, 16) != 0) Native.GetWindowRect(target, out fr);
            // cover the buttons edge to edge, with a pixel or two to spare: up to the frame's top and right when they
            // stop short of them, a little further left (the minimize button's hover shading starts there), and one row
            // lower (some apps draw a hairline under the buttons)
            double ax = area.Left, ay = area.Top, aw = area.Width, ah = area.Height, px1 = k0();
            if (ay > 0 && ay <= 10 * px1) { ah += ay; ay = 0; }
            if (ax + aw < 0 && ax + aw >= -6 * px1) aw = -ax;
            ax -= Math.Round(4 * px1); aw += Math.Round(4 * px1); ah += Math.Round(px1);
            int x = (int)Math.Round(fr.Right + ax), y = (int)Math.Round(fr.Top + ay), w = (int)Math.Round(aw), h = (int)Math.Round(ah);
            double k = hwnd != IntPtr.Zero ? VisualTreeHelper.GetDpi(this).DpiScaleX : App.Dpi;
            if (k <= 0) k = App.Dpi;
            // Where the patch meets the window's top and right edges it stops a pixel short, and where it meets the
            // corner it curves a little wider than Windows does: so the window's own outline and its own rounded
            // corner always show, exactly, instead of a copy of them. Those few pixels are still the patch's (an
            // almost clear layer under it), so the pointer never reaches the real buttons there and lights one up.
            bool atTop = ay <= 0, atRight = ax + aw >= 0, zoomedNow = Native.IsZoomed(target);
            // flush with the window's edges -- the picture under the lights carries the window's own outline and
            // corner (those pixels are never erased, see Process), so the patch's edge is the window's edge
            int it = 0, ir = 0;
            edgeTop = atTop; edgeRight = atRight;
            bool corner = !zoomedNow && Environment.OSVersion.Version.Build >= 22000 && atTop && atRight;
            catcher.Width = w / k; catcher.Height = h / k;
            patch.Width = (w - ir) / k; patch.Height = (h - it) / k;
            patch.Margin = new Thickness(0, it / k, ir / k, 0);
            // exactly Windows 11's corner: 8 px at 100%, scaled with the display -- the same curve, so the two edges are one
            uint tdpi = GetDpiForWindow(target);
            // the corner this window really has: GlassDock's cut-out if it's on it, else Windows' own 8 px (scaled)
            double actual = WindowCorners.RadiusPx(target);
            double cornerPx = actual > 0 ? actual : 8 * (tdpi == 0 ? 96 : tdpi) / 96.0;
            patch.CornerRadius = new CornerRadius(0, corner ? cornerPx / k : 0, 0, 0);
            // the window moved or changed size: no fresh picture of its title bar until it has been still a moment
            if (x != lastX || y != lastY || w != lastW || h != lastH) { bool real = lastW != 0; lastX = x; lastY = y; lastW = w; lastH = h; if (real) moved = DateTime.UtcNow; }
            // grown lights get room to the left and below (transparent, so the app underneath still takes clicks)
            int ww = w, wh = h;
            if (grown) { ww = Math.Max(w, (int)Math.Ceiling((RowWidth * Grow + 12) * k)); wh = Math.Max(h, (int)Math.Ceiling(((h / k - D) / 2 + D * Grow + 10) * k)); }
            bool fore = Native.GetAncestor(Native.GetForegroundWindow(), 2) == target;
            if (fore != active) { active = fore; sampled = DateTime.MinValue; resample.Stop(); resample.Start(); }   // and again once the app has repainted
            if ((DateTime.UtcNow - sampled).TotalMilliseconds > 250) Sample(x, y + it, w - ir, h - it);
            if (patch.Background == null) { Conceal(); return; }   // nothing to show over the buttons yet
            // Right above the window in the stack: just under whatever window is above it -- skipping the
            // always-on-top ones (the Dock, the menu bar), which would make the lights always-on-top too and leave
            // them floating over the next app -- or at the top of the ordinary windows when it's the front one.
            const int TOPMOST = 0x8;
            bool targetTop = (Native.GetWindowLong(target, Native.GWL_EXSTYLE) & TOPMOST) != 0;
            var above = Native.GetWindow(target, 3 /* GW_HWNDPREV */);
            while (above != IntPtr.Zero && (above == hwnd || (!targetTop && (Native.GetWindowLong(above, Native.GWL_EXSTYLE) & TOPMOST) != 0)))
                above = Native.GetWindow(above, 3);
            if (!targetTop && (Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) & TOPMOST) != 0)
                Native.SetWindowPos(hwnd, (IntPtr)(-2) /* HWND_NOTOPMOST */, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            var after = above != IntPtr.Zero ? above : targetTop ? (IntPtr)(-1) /* HWND_TOPMOST */ : IntPtr.Zero /* HWND_TOP */;
            Native.SetWindowPos(hwnd, after, x + w - ww, y, ww, wh, Native.SWP_NOACTIVATE);
            concealed = false;
            if (!shownOnce) { shownOnce = true; Show(); }
            Paint();
        }

        // What the title bar looks like under the buttons, without them: this patch is out of screen capture, so a
        // capture of its spot shows the app's own buttons. Each row's background is the colour most of that row has
        // (the symbols are thin lines, a small part of it); pixels that stand out from it -- the symbols and their
        // soft edges -- are painted over with it, and everything else is kept as it is. So the patch carries the
        // title bar's real shading (Mica, gradients, a border line along the top) instead of one flat colour.
        void Sample(int x, int y, int w, int h)
        {
            sampled = DateTime.UtcNow;
            if (w < 4 || h < 4) return;
            // A picture taken at the wrong moment would stay on the title bar: so none while the window is being
            // dragged, resized or animated (it's tried again shortly). When something else is over that spot (a menu,
            // a tooltip, another window) or part of it is off the screen, the window is asked to draw itself instead.
            if (tracking || (DateTime.UtcNow - moved).TotalMilliseconds < 450) { sampled = DateTime.UtcNow.AddMilliseconds(-700); resample.Stop(); resample.Start(); return; }
            if (Glass.Lifted || !Settings.LiveGlass || Covered(x, y, w, h) || !OnScreen(x, y, w, h)) { PrintSample(x, y, w, h); return; }   // (shown in captures, the screen would show the patch itself)
            try
            {
                var img = Icons.CaptureScreen(x, y, w, h);
                int W = img.PixelWidth, H = img.PixelHeight;
                var px = new byte[W * 4 * H]; img.CopyPixels(px, W * 4, 0);
                Process(px, W, H, false, false);
            }
            catch { }
        }

        // The window draws itself into a picture (works when it's covered), off the UI thread -- an app that's slow
        // to draw doesn't hold GlassDock up -- at most every few seconds per window.
        bool printing; DateTime printed = DateTime.MinValue;
        void PrintSample(int x, int y, int w, int h)
        {
            if (printing || (patch.Background != null && (DateTime.UtcNow - printed).TotalSeconds < 3)) { sampled = DateTime.UtcNow.AddMilliseconds(-700); return; }
            printing = true;
            var t = target;
            System.Threading.Tasks.Task.Run(() =>
            {
                byte[] px = null;
                try { px = Print(t, x, y, w, h); } catch { }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    printing = false; printed = DateTime.UtcNow;
                    if (!Gone && px != null) { Process(px, w, h, true, true); Follow(); }
                }));
            });
        }

        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);

        // The part (x, y, w, h) of the screen that window covers, as the window draws it; null if it couldn't.
        static byte[] Print(IntPtr t, int x, int y, int w, int h)
        {
            Native.RECT wr;
            if (!Native.GetWindowRect(t, out wr)) return null;
            int fw = wr.Right - wr.Left, fh = wr.Bottom - wr.Top, cx = x - wr.Left, cy = y - wr.Top;
            if (fw < 2 || fh < 2 || cx < 0 || cy < 0 || cx + w > fw || cy + h > fh) return null;
            IntPtr screen = Native.GetDC(IntPtr.Zero), dc = Native.CreateCompatibleDC(screen), bmp = Native.CreateCompatibleBitmap(screen, fw, fh);
            var old = Native.SelectObject(dc, bmp);
            try
            {
                if (!PrintWindow(t, dc, 2 /* PW_RENDERFULLCONTENT */)) return null;
                Native.SelectObject(dc, old); old = IntPtr.Zero;
                var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, new Int32Rect(cx, cy, w, h), BitmapSizeOptions.FromEmptyOptions());
                var img = new FormatConvertedBitmap(src, PixelFormats.Bgr32, null, 0);
                var px = new byte[w * 4 * h]; img.CopyPixels(px, w * 4, 0);
                // some apps draw nothing into the picture (all black): no use
                for (int i = 0; i < px.Length; i += 4) if (px[i] > 2 || px[i + 1] > 2 || px[i + 2] > 2) return px;
                return null;
            }
            finally { if (old != IntPtr.Zero) Native.SelectObject(dc, old); Native.DeleteObject(bmp); Native.DeleteDC(dc); Native.ReleaseDC(IntPtr.Zero, screen); }
        }

        // From a picture of the spot under the buttons: the title bar without them (see Sample). `strict` turns down
        // pictures that don't look like a title bar even when there's nothing under the lights yet.
        long lastHash;
        bool edgeTop, edgeRight;   // the patch meets the window's top / right edge (its outline is in the picture)
        bool lastWasBlack;   // the title bar really is black (seen on screen): black pictures of it are fine
        void Process(byte[] px, int W, int H, bool strict, bool printed)
        {
            int stride = W * 4;
            bool first = patch.Background == null;
            // A picture that's mostly pure black is a failed one -- the window couldn't draw itself (many apps can't,
            // when asked to), or the screen came back black after a heavy task -- unless the title bar really is
            // black, which a picture straight off the screen shows first. Keep the last good one instead.
            bool black = Glass.BlackShare(px) > 0.5;
            if (black && !lastWasBlack && (printed || !first)) return;
            if (!printed) lastWasBlack = black;
            var bg = new int[H * 3];
            var odd = new bool[W * H];
            var count = new Dictionary<int, int>();
            long lum = 0; int clear = 0;
            for (int r = 0; r < H; r++)
            {
                count.Clear();
                int best = 0, most = 0;
                for (int c = 0; c < W; c++)
                {
                    int i = r * stride + c * 4, key = (px[i + 2] >> 3) << 10 | (px[i + 1] >> 3) << 5 | (px[i] >> 3), n;
                    count.TryGetValue(key, out n); count[key] = ++n;
                    if (n > most) { most = n; best = key; }
                }
                if (most * 10 >= W * 4) clear++;   // at least 40% of the row is one colour: title bar, not a picture
                long sb = 0, sg = 0, sr = 0; int m = 0;
                for (int c = 0; c < W; c++)
                {
                    int i = r * stride + c * 4;
                    if (((px[i + 2] >> 3) << 10 | (px[i + 1] >> 3) << 5 | (px[i] >> 3)) != best) continue;
                    sb += px[i]; sg += px[i + 1]; sr += px[i + 2]; m++;
                }
                if (m == 0) return;
                int B0 = (int)(sb / m), G0 = (int)(sg / m), R0 = (int)(sr / m);
                bg[r * 3] = B0; bg[r * 3 + 1] = G0; bg[r * 3 + 2] = R0;
                lum += R0 + G0 + B0;
                for (int c = 0; c < W; c++)
                {
                    int i = r * stride + c * 4;
                    odd[r * W + c] = Math.Abs(px[i] - B0) + Math.Abs(px[i + 1] - G0) + Math.Abs(px[i + 2] - R0) > 14;
                }
            }
            // something that isn't a plain title bar was caught (a video, a picture, a half-drawn window): keep the last one
            if ((strict || !first) && clear * 10 < H * 8) return;
            // a pixel's width around each symbol too, so no faint outline of it is left behind
            long hash = 17;
            for (int r = 0; r < H; r++)
                for (int c = 0; c < W; c++)
                {
                    bool near = false;
                    for (int dr = -1; dr <= 1 && !near; dr++)
                        for (int dc = -1; dc <= 1 && !near; dc++)
                        {
                            int rr = r + dr, cc = c + dc;
                            near = rr >= 0 && rr < H && cc >= 0 && cc < W && odd[rr * W + cc];
                        }
                    int i = r * stride + c * 4;
                    // the window's outline along the top and right edge and its rounded corner stay exactly as captured
                    bool edge = (edgeTop && r < 2) || (edgeRight && c >= W - 3);
                    if (near && !edge) { px[i] = (byte)bg[r * 3]; px[i + 1] = (byte)bg[r * 3 + 1]; px[i + 2] = (byte)bg[r * 3 + 2]; }
                    px[i + 3] = 255;
                    hash = hash * 31 + (px[i] | px[i + 1] << 8 | px[i + 2] << 16);
                }
            hash = hash * 31 + W * 7919 + H;
            if (!first && hash == lastHash) return;   // the same as what's there: nothing to redraw
            lastHash = hash;
            var bar = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgra32, null, px, stride); bar.Freeze();
            var brush = new ImageBrush(bar) { Stretch = Stretch.Fill }; brush.Freeze();
            patch.Background = brush;
            bool lightBar = lum / H > 3 * 128;
            for (int i = 0; i < 3; i++) dots[i].Stroke = new SolidColorBrush(lightBar ? Color.FromArgb(40, 0, 0, 0) : Color.FromArgb(50, 255, 255, 255));
            grayOff = lightBar ? Color.FromRgb(0xDD, 0xDD, 0xDF) : Color.FromRgb(0x5A, 0x5A, 0x5E);
        }

        int lastX, lastY, lastW, lastH;
        DateTime moved = DateTime.MinValue;

        // Is any other window (or menu, or tooltip) over this part of the screen, above the target in the stack?
        bool Covered(int x, int y, int w, int h)
        {
            int n = 0;
            for (var o = Native.GetWindow(target, 3 /* GW_HWNDPREV */); o != IntPtr.Zero && n < 400; o = Native.GetWindow(o, 3), n++)
            {
                if (o == hwnd || !Native.IsWindowVisible(o) || Native.IsCloaked(o)) continue;
                Native.RECT r;
                if (!Native.GetWindowRect(o, out r) || r.Right <= x || r.Left >= x + w || r.Bottom <= y || r.Top >= y + h) continue;
                // click-through overlays and windows kept out of screen pictures (GlassDock's own) don't get in the picture's way
                uint aff; if ((Native.GetWindowLong(o, Native.GWL_EXSTYLE) & 0x20 /* WS_EX_TRANSPARENT */) != 0 || (GetWindowDisplayAffinity(o, out aff) && aff != 0)) continue;
                return true;
            }
            return false;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetWindowDisplayAffinity(IntPtr h, out uint affinity);

        static bool OnScreen(int x, int y, int w, int h)
        {
            return Sys.Screens().Any(s => x >= s.Bounds.Left && y >= s.Bounds.Top && x + w <= s.Bounds.Right && y + h <= s.Bounds.Bottom);
        }

        // the screen's scale for this window, before the patch has a handle of its own
        double k0() { double k = hwnd != IntPtr.Zero ? VisualTreeHelper.GetDpi(this).DpiScaleX : App.Dpi; return k > 0 ? k : 1; }
        Color grayOff = Color.FromRgb(0xDD, 0xDD, 0xDF);

        static Color Mix(Color a, Color b, double t)
        {
            return Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        }

        void Paint()
        {
            for (int i = 0; i < 3; i++)
            {
                Color c = active || hover ? On[i] : grayOff;   // a bead: lighter at the top, a touch deeper at the bottom
                dots[i].Fill = new LinearGradientBrush(new GradientStopCollection {
                    new GradientStop(Mix(c, Colors.White, 0.22), 0), new GradientStop(c, 0.5), new GradientStop(Mix(c, Colors.Black, 0.18), 1) }, 90);
                marks[i].Opacity = hover || grown ? 1 : 0;
            }
            marks[2].Text = Native.IsWindow(target) && Native.IsZoomed(target) ? "–" : "+";
        }

        // The same commands the window's own buttons send.
        void Press(int which)
        {
            const uint WM_SYSCOMMAND = 0x0112;
            uint cmd = which == 0 ? 0xF060u /* SC_CLOSE */ : which == 1 ? 0xF020u /* SC_MINIMIZE */ : Native.IsZoomed(target) ? 0xF120u /* SC_RESTORE */ : 0xF030u /* SC_MAXIMIZE */;
            Native.PostMessage(target, WM_SYSCOMMAND, (IntPtr)cmd, IntPtr.Zero);
            if (which != 0) Native.SetForegroundWindow(target);
        }
    }
}
