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
        static Native.WinEventProc onMove, onFore;   // kept alive: Windows calls them
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
                foreach (var h in shown.Keys.Where(k => !want.Contains(k)).ToList()) { shown[h].Close(); shown.Remove(h); }
                foreach (var h in unsupported.Keys.Where(k => !Native.IsWindow(k)).ToList()) unsupported.Remove(h);
                foreach (var h in want)
                {
                    LightsWindow w;
                    if (shown.TryGetValue(h, out w)) { w.Check(); continue; }
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
                Rect? found = null;
                try { found = Buttons(h, fr); } catch { }
                if (found == null) try { found = FromTitleBar(h, fr); } catch { }
                ui.BeginInvoke(new Action(() =>
                {
                    probing.Remove(h);
                    // the patch this lookup was for has since closed or been replaced: leave the newer state alone
                    LightsWindow current;
                    if (existing != null && (existing.Gone || !shown.TryGetValue(h, out current) || current != existing)) return;
                    if (found == null) { if (existing != null) { existing.Close(); shown.Remove(h); } unsupported[h] = DateTime.UtcNow; return; }
                    unsupported.Remove(h);
                    var area = found.Value;   // relative to the frame's top-right corner (x <= 0)
                    if (existing != null) { existing.Set(area, zoomed); return; }
                    if (!Settings.TrafficLights || !Wanted(h) || shown.ContainsKey(h)) return;
                    var lw = new LightsWindow(h, area, zoomed);
                    shown[h] = lw;
                    lw.Show();
                    lw.Follow();
                }));
            });
        }

        static Rect? Buttons(IntPtr h, Native.RECT fr)
        {
            Func<int, int, Rect?> button = (x, y) =>
            {
                var e = AutomationElement.FromPoint(new Point(x, y));
                if (e == null) return null;
                var info = e.Current;
                if (info.ProcessId == (int)myPid) return null;
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
            return (int)res;
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

    class LightsWindow : Window
    {
        readonly IntPtr target;
        Rect area; bool zoomed;
        IntPtr hwnd;
        readonly Border patch = new Border();
        readonly Ellipse[] dots = new Ellipse[3];
        readonly TextBlock[] marks = new TextBlock[3];
        static readonly Color[] On = { Color.FromRgb(0xFF, 0x5F, 0x57), Color.FromRgb(0xFE, 0xBC, 0x2E), Color.FromRgb(0x28, 0xC8, 0x40) };
        const double D = 17, Gap = 9;   // bigger than a Mac's: they sit where Windows' wide buttons were
        public const double RowWidth = 3 * D + 2 * Gap + 14;   // the lights plus their right margin, in DIPs
        bool hover, active = true;
        DateTime sampled = DateTime.MinValue;
        public bool Gone { get; private set; }
        protected override void OnClosed(EventArgs e) { Gone = true; base.OnClosed(e); }

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
                g.MouseLeftButtonUp += (s, e) => { e.Handled = true; Press(which); };
                row.Children.Add(g);
            }
            row.MouseEnter += delegate { hover = true; Paint(); };
            row.MouseLeave += delegate { hover = false; Paint(); };
            patch.Child = row;
            Content = patch;
            SourceInitialized += delegate
            {
                hwnd = new WindowInteropHelper(this).Handle;
                Native.MakeToolWindow(hwnd, true);
                Native.SetWindowDisplayAffinity(hwnd, 0x11);   // out of screen capture: the title bar under it can be sampled
            };
            Paint();
        }

        public void Set(Rect a, bool z) { area = a; zoomed = z; sampled = DateTime.MinValue; Follow(); }

        // The window was maximized or restored: its buttons move, so they're found again.
        public void Check()
        {
            if (!Native.IsWindow(target)) return;
            if (Native.IsZoomed(target) != zoomed) { Hide(); TrafficLights.Probe(target, this); return; }
            if ((DateTime.UtcNow - sampled).TotalMilliseconds > 1500) Follow();
        }

        // Onto the buttons' spot, right above the window in the stack.
        public void Follow()
        {
            if (Gone || hwnd == IntPtr.Zero || !Native.IsWindow(target)) return;
            if (!Native.IsWindowVisible(target) || Native.IsIconic(target) || Native.IsCloaked(target)) { if (IsVisible) Hide(); return; }
            Native.RECT fr; if (Native.DwmGetWindowAttribute(target, 9, out fr, 16) != 0) Native.GetWindowRect(target, out fr);
            int x = (int)Math.Round(fr.Right + area.Left), y = (int)Math.Round(fr.Top + area.Top), w = (int)Math.Round(area.Width), h = (int)Math.Round(area.Height);
            bool fore = Native.GetAncestor(Native.GetForegroundWindow(), 2) == target;
            if (fore != active) { active = fore; sampled = DateTime.MinValue; }
            if ((DateTime.UtcNow - sampled).TotalMilliseconds > 1000) Sample(x, y, w, h);
            if (!IsVisible) Show();
            var above = Native.GetWindow(target, 3 /* GW_HWNDPREV */);
            if (above == hwnd) above = Native.GetWindow(hwnd, 3);
            Native.SetWindowPos(hwnd, above == IntPtr.Zero ? IntPtr.Zero : above, x, y, w, h, Native.SWP_NOACTIVATE | (above == IntPtr.Zero ? 0x4u /* NOZORDER */ : 0));
            Paint();
        }

        // The title bar's colour just left of the buttons (this patch is out of capture, so it isn't in the sample).
        void Sample(int x, int y, int w, int h)
        {
            sampled = DateTime.UtcNow;
            try
            {
                var img = Icons.CaptureScreen(x - 8, y + h / 4, 4, Math.Max(1, h / 2));
                var px = new byte[img.PixelWidth * img.PixelHeight * 4]; img.CopyPixels(px, img.PixelWidth * 4, 0);
                long r = 0, g = 0, b = 0; int n = px.Length / 4;
                for (int i = 0; i < px.Length; i += 4) { b += px[i]; g += px[i + 1]; r += px[i + 2]; }
                patch.Background = new SolidColorBrush(Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n)));
                bool lightBar = (r + g + b) / n > 3 * 128;
                for (int i = 0; i < 3; i++) dots[i].Stroke = new SolidColorBrush(lightBar ? Color.FromArgb(40, 0, 0, 0) : Color.FromArgb(50, 255, 255, 255));
                grayOff = lightBar ? Color.FromRgb(0xDD, 0xDD, 0xDF) : Color.FromRgb(0x5A, 0x5A, 0x5E);
            }
            catch { }
        }
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
                marks[i].Opacity = hover ? 1 : 0;
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
