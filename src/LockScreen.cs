using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using global::Windows.Security.Credentials.UI;

namespace OpenDock
{
    // A macOS-style lock screen: the wallpaper, a big clock and the date, you at the bottom; unlocking goes through
    // Windows Hello (PIN, face or fingerprint, checked by Windows itself -- OpenDock never sees any of it).
    //
    // Windows keeps its own lock screen on a separate secure desktop that apps can't draw on, so this one is a
    // privacy screen over your desktop: it covers every display, stays on top and swallows the Windows key, Alt+Tab
    // and Alt+F4, but Ctrl+Alt+Del still works (Windows doesn't let any app block it). Where Windows Hello isn't set
    // up, or if you'd rather, "Use Windows Lock" hands over to Windows' own lock screen.
    static class LockScreen
    {
        static readonly List<LockWindow> windows = new List<LockWindow>();
        static IntPtr hook;
        static HookProc hookProc;   // kept alive: Windows calls it
        public static bool Active { get { return windows.Count > 0; } }

        public static void Lock()
        {
            if (Active) return;
            if (!Settings.CustomLockScreen) { Native.LockWorkStation(); return; }
            // only with Windows Hello to unlock it; otherwise Windows' own lock screen
            Hello.Available(ok =>
            {
                if (!ok) { Native.LockWorkStation(); return; }
                try { Show(); }
                catch (Exception ex) { App.Log(ex); Close(); Native.LockWorkStation(); }
            });
        }

        static void Show()
        {
            var screens = Sys.Screens();
            if (screens.Count == 0) { Native.LockWorkStation(); return; }
            var main = screens.FirstOrDefault(s => s.Primary) ?? screens[0];
            foreach (var s in screens) windows.Add(new LockWindow(s, s == main));
            foreach (var w in windows) w.Show();
            var first = windows.First(w => w.Main);
            first.Activate();
            Hook(true);
            // if GlassDock stops while this is up (a crash, the watchdog), its next start hands over to Windows' lock
            try { File.WriteAllText(Marker, DateTime.Now.ToString("o")); } catch { }
            if (!watchingDisplays) { watchingDisplays = true; Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (s, e) => { if (Active) windows[0].Dispatcher.BeginInvoke(new Action(CoverNewDisplays)); }; }
        }

        static string Marker { get { return System.IO.Path.Combine(Settings.DataDir, "locked"); } }
        static bool watchingDisplays;

        // A display plugged in while locked gets covered too.
        static void CoverNewDisplays()
        {
            if (!Active) return;
            foreach (var s in Sys.Screens().Where(s => !windows.Any(w => w.Device == s.Device)))
            {
                var w = new LockWindow(s, false);
                windows.Add(w);
                w.Show();
            }
        }

        // At startup: the lock screen was up when GlassDock last stopped, so Windows' own lock takes over.
        public static void RecoverAfterCrash()
        {
            try { if (File.Exists(Marker)) { File.Delete(Marker); Native.LockWorkStation(); } } catch (Exception ex) { App.Log(ex); }
        }

        // Unlocked (or handed to Windows): everything goes, the desktop comes back.
        internal static void Close()
        {
            Hook(false);
            foreach (var w in windows.ToList()) w.Leave();
            windows.Clear();
            try { File.Delete(Marker); } catch { }
        }

        // Windows drops a keyboard hook that ever answers too slowly, without saying so: put it back now and then.
        internal static void Rearm() { if (Active) { Hook(false); Hook(true); } }

        // Something else took the screen while locked (Task Manager from Ctrl+Alt+Del, an app forcing itself
        // forward): this overlay can't keep it out, so Windows' own secure lock takes over.
        internal static void Breached() { if (Active) UseWindowsLock(); }

        internal static void UseWindowsLock()
        {
            Native.LockWorkStation();   // Windows' secure lock takes over; once you're back, so is your desktop
            Close();
        }

        // ---------- the keys a lock screen keeps for itself ----------
        delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);

        static void Hook(bool on)
        {
            if (on && hook == IntPtr.Zero)
            {
                hookProc = (code, w, l) =>
                {
                    if (code >= 0 && Swallow(Marshal.ReadInt32(l), Marshal.ReadInt32(l, 8))) return (IntPtr)1;
                    return CallNextHookEx(hook, code, w, l);
                };
                hook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, hookProc, GetModuleHandle(null), 0);
            }
            else if (!on && hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        }

        // The Windows key, Alt+Tab, Alt+Esc, Alt+F4 and Ctrl+Esc: the ways out of a full-screen window.
        // (Typing still works: the Windows Hello prompt needs it for a PIN.)
        static bool Swallow(int vk, int flags)
        {
            bool alt = (flags & 0x20) != 0, ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;
            if (vk == 0x5B || vk == 0x5C) return true;                         // Windows keys
            if (alt && (vk == 0x09 || vk == 0x1B || vk == 0x73)) return true;  // Alt+Tab, Alt+Esc, Alt+F4
            if (ctrl && vk == 0x1B) return true;                               // Ctrl+Esc
            return false;
        }
    }

    // Windows Hello from a desktop app: the system's own prompt, attached to our window.
    static class Hello
    {
        [ComImport, Guid("39E050C3-4E74-441A-8DC0-B81104DF949C"), InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
        interface IUserConsentVerifierInterop
        {
            [return: MarshalAs(UnmanagedType.IInspectable)]
            object RequestVerificationForWindowAsync(IntPtr appWindow, [MarshalAs(UnmanagedType.HString)] string message, [In] ref Guid riid);
        }
        static readonly Guid AsyncResultIid = new Guid("FD596FFD-2318-558F-9DBE-D21DF43764A5");   // IAsyncOperation<UserConsentVerificationResult>

        public static void Available(Action<bool> done)
        {
            var ui = Dispatcher.CurrentDispatcher;
            try
            {
                var op = UserConsentVerifier.CheckAvailabilityAsync();
                op.Completed = (o, status) =>
                {
                    bool ok = false;
                    try { ok = status == global::Windows.Foundation.AsyncStatus.Completed && o.GetResults() == UserConsentVerifierAvailability.Available; } catch { }
                    ui.BeginInvoke(new Action(() => done(ok)));
                };
            }
            catch (Exception ex) { App.Log(ex); done(false); }
        }

        // Asks Windows to check it's you; `done(true)` only when Windows says verified.
        public static void Verify(IntPtr window, string message, Action<bool> done)
        {
            var ui = Dispatcher.CurrentDispatcher;
            try
            {
                var factory = (IUserConsentVerifierInterop)WindowsRuntimeMarshal.GetActivationFactory(typeof(UserConsentVerifier));
                var iid = AsyncResultIid;
                var op = (global::Windows.Foundation.IAsyncOperation<UserConsentVerificationResult>)factory.RequestVerificationForWindowAsync(window, message, ref iid);
                op.Completed = (o, status) =>
                {
                    bool ok = false;
                    try { ok = status == global::Windows.Foundation.AsyncStatus.Completed && o.GetResults() == UserConsentVerificationResult.Verified; } catch { }
                    ui.BeginInvoke(new Action(() => done(ok)));
                };
            }
            catch (Exception ex) { App.Log(ex); done(false); }
        }
    }

    class LockWindow : Window
    {
        public readonly bool Main;
        public readonly string Device;
        int guardTicks;
        bool wasActive;
        DateTime grace;
        readonly TextBlock clock = new TextBlock(), date = new TextBlock(), hint = new TextBlock();
        readonly StackPanel who = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom };
        readonly Grid content = new Grid();
        readonly DispatcherTimer tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        readonly DispatcherTimer guard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        Border unlockPill;
        bool verifying, leaving;
        IntPtr hwnd;
        static readonly Dictionary<string, ImageSource> pictures = new Dictionary<string, ImageSource>();

        // For a preview (the ad, Settings): a lock screen that isn't shown, its name and time given.
        public static FrameworkElement Preview(double w, double h, string name, DateTime at, ImageSource wallpaper)
        {
            var lw = new LockWindow(null, true, name, at, wallpaper) { Width = w, Height = h };
            var c = (FrameworkElement)lw.Content; lw.Content = null;
            c.Width = w; c.Height = h;
            return c;
        }

        public LockWindow(Sys.Display screen, bool main) : this(screen, main, null, DateTime.MinValue, null) { }

        LockWindow(Sys.Display screen, bool main, string name, DateTime at, ImageSource wallpaper)
        {
            Main = main;
            Device = screen != null ? screen.Device : null;
            Title = "GlassDock Lock Screen";
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
            Background = Brushes.Black; FontFamily = Theme.Font; Foreground = Brushes.White;
            if (screen != null)
            {
                var b = DisplayLayout.Dip(screen.Bounds);
                Left = b.Left; Top = b.Top; Width = b.Width; Height = b.Height;
            }
            // the wallpaper, a touch dimmer at the top and bottom so the white text reads on any picture
            var wall = new Image { Source = wallpaper ?? Wallpaper(), Stretch = Stretch.UniformToFill };
            content.Children.Add(wall);
            content.Children.Add(new Border { Background = new LinearGradientBrush(new GradientStopCollection {
                new GradientStop(Color.FromArgb(70, 0, 0, 0), 0), new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.42),
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.66), new GradientStop(Color.FromArgb(90, 0, 0, 0), 1) }, new Point(0, 0), new Point(0, 1)) });

            // the date, then the big clock (macOS 26: large and heavy, at the top)
            var top = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
            var display = new FontFamily("Segoe UI Variable Display, Segoe UI");
            date.FontFamily = display; date.FontWeight = FontWeights.SemiBold; date.HorizontalAlignment = HorizontalAlignment.Center;
            date.Foreground = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255));
            clock.FontFamily = display; clock.FontWeight = FontWeights.Bold; clock.HorizontalAlignment = HorizontalAlignment.Center;
            clock.Foreground = new SolidColorBrush(Color.FromArgb(232, 255, 255, 255));
            var soft = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 0, Opacity = 0.28, Color = Colors.Black };
            date.Effect = soft; clock.Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 0, Opacity = 0.25, Color = Colors.Black };
            top.Children.Add(date); top.Children.Add(clock);
            content.Children.Add(top);
            SizeChanged += delegate { Layout(top); };
            content.SizeChanged += delegate { Layout(top); };

            if (main)
            {
                // you, at the bottom: your picture (or initials), your name, and how to unlock
                string who_ = name ?? Account();
                who.Children.Add(Avatar(who_, 72, name == null));
                var nm = new TextBlock { Text = who_, FontSize = 17, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 10), Effect = soft };
                who.Children.Add(nm);
                unlockPill = Pill("\uE8D7", "Unlock with Windows Hello");
                unlockPill.MouseLeftButtonUp += (s, e) => { e.Handled = true; Unlock(); };
                who.Children.Add(unlockPill);
                hint.Text = "Use Windows Lock"; hint.FontSize = 12.5; hint.HorizontalAlignment = HorizontalAlignment.Center; hint.Margin = new Thickness(0, 12, 0, 0);
                hint.Foreground = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)); hint.Cursor = Cursors.Hand; hint.Effect = soft;
                hint.MouseLeftButtonUp += (s, e) => { e.Handled = true; LockScreen.UseWindowsLock(); };
                who.Children.Add(hint);
                content.Children.Add(who);
            }
            TextElement.SetForeground(content, Brushes.White);   // (also when shown as a preview, outside this window)
            Content = content;
            Show(at == DateTime.MinValue ? DateTime.Now : at);
            if (screen == null) return;   // a preview

            tick.Tick += delegate { Show(DateTime.Now); };
            // stay on top of everything (the Dock and the menu bar are always-on-top too), but leave the Hello prompt be
            guard.Tick += delegate
            {
                if (hwnd == IntPtr.Zero || verifying || leaving) return;
                if (Main)
                {
                    // another program in front (not the secure desktop, where there's no foreground window at all)
                    var fg = Native.GetForegroundWindow();
                    uint pid; Native.GetWindowThreadProcessId(fg, out pid);
                    bool settled = wasActive && DateTime.UtcNow > grace;   // not while it's still coming up, or the Hello prompt is going away
                    if (settled && fg != IntPtr.Zero && pid != (uint)System.Diagnostics.Process.GetCurrentProcess().Id) { LockScreen.Breached(); return; }
                    if (++guardTicks % 10 == 0) LockScreen.Rearm();
                }
                Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                if (Main && !IsActive) Activate();
            };
            SourceInitialized += delegate { hwnd = new WindowInteropHelper(this).Handle; Native.MakeToolWindow(hwnd, false); tick.Start(); guard.Start(); };
            Activated += delegate { if (!wasActive) { wasActive = true; grace = DateTime.UtcNow.AddSeconds(1); } };
            Closing += (s, e) => { if (!leaving) e.Cancel = true; };   // Alt+F4 and friends don't close it
            // any key or click asks for Windows Hello, as a Mac shows Touch ID
            PreviewKeyDown += (s, e) => { if (Main && !verifying) Unlock(); };
            MouseLeftButtonUp += delegate { if (Main && !verifying) Unlock(); };
            Opacity = 0;
            Loaded += delegate { BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260))); };
        }

        void Layout(FrameworkElement top)
        {
            double h = ActualHeight > 0 ? ActualHeight : Height;
            if (double.IsNaN(h) || h <= 0) return;
            date.FontSize = Math.Max(16, h * 0.026);
            clock.FontSize = Math.Max(60, h * 0.17);
            clock.Margin = new Thickness(0, -h * 0.012, 0, 0);
            top.Margin = new Thickness(0, h * 0.075, 0, 0);
            who.Margin = new Thickness(0, 0, 0, h * 0.07);
            double k = Math.Max(1, h / 1000.0);   // the account, name and button grow with the screen
            who.LayoutTransform = new ScaleTransform(k, k);
        }

        void Show(DateTime now)
        {
            date.Text = now.ToString("dddd, MMMM d");
            clock.Text = now.ToString("h:mm");
        }

        void Unlock()
        {
            if (verifying || leaving || hwnd == IntPtr.Zero) return;
            verifying = true;
            if (unlockPill != null) unlockPill.Opacity = 0.55;
            Hello.Verify(hwnd, "Unlock GlassDock", ok =>
            {
                verifying = false;
                grace = DateTime.UtcNow.AddSeconds(2);
                if (unlockPill != null) unlockPill.Opacity = 1;
                if (ok) LockScreen.Close();
                else if (Main) Activate();
            });
        }

        // Unlocking: the screen lifts away, as on a Mac.
        public void Leave()
        {
            leaving = true; tick.Stop(); guard.Stop();
            var move = new TranslateTransform(); content.RenderTransform = move;
            var time = TimeSpan.FromMilliseconds(320);
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-ActualHeight * 0.06, time) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
            var fade = new DoubleAnimation(0, time);
            fade.Completed += delegate { Close(); };
            BeginAnimation(OpacityProperty, fade);
        }

        // ---------- pieces ----------

        static Border Pill(string glyph, string text)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            row.Children.Add(new TextBlock { Text = glyph, FontFamily = Theme.Glyphs, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0) });
            row.Children.Add(new TextBlock { Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            // Liquid Glass: a bright, see-through pill with a lit rim
            var b = new Border { Child = row, Padding = new Thickness(20, 10, 22, 11), CornerRadius = new CornerRadius(22), Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Center,
                Background = new LinearGradientBrush(Color.FromArgb(78, 255, 255, 255), Color.FromArgb(40, 255, 255, 255), 90),
                BorderThickness = new Thickness(1), BorderBrush = new LinearGradientBrush(Color.FromArgb(170, 255, 255, 255), Color.FromArgb(40, 255, 255, 255), 90),
                Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0.22 } };
            b.MouseEnter += delegate { b.Background = new LinearGradientBrush(Color.FromArgb(105, 255, 255, 255), Color.FromArgb(60, 255, 255, 255), 90); };
            b.MouseLeave += delegate { b.Background = new LinearGradientBrush(Color.FromArgb(78, 255, 255, 255), Color.FromArgb(40, 255, 255, 255), 90); };
            return b;
        }

        static FrameworkElement Avatar(string name, double size, bool picture)
        {
            var circle = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), HorizontalAlignment = HorizontalAlignment.Center,
                BorderThickness = new Thickness(1.5), BorderBrush = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)),
                Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Direction = 270, Opacity = 0.3 } };
            var pic = picture ? AccountPicture() : null;   // a preview never shows your own picture
            if (pic != null) circle.Background = new ImageBrush(pic) { Stretch = Stretch.UniformToFill };
            else
            {
                circle.Background = new LinearGradientBrush(Color.FromRgb(0xA8, 0xA8, 0xB0), Color.FromRgb(0x6E, 0x6E, 0x76), 90);
                string initials = string.Concat((name ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpper(w[0]).ToString()));
                circle.Child = new TextBlock { Text = initials, FontSize = size * 0.38, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            }
            return circle;
        }

        static string Account()
        {
            string who = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            return who.Substring(who.IndexOf('\\') + 1);
        }

        // Your Windows account picture, if you've set one (Windows keeps copies under Public\AccountPictures\<SID>).
        static ImageSource AccountPicture()
        {
            try
            {
                string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
                ImageSource cached;
                if (pictures.TryGetValue(sid, out cached)) return cached;
                string dir = System.IO.Path.Combine(Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public", "AccountPictures", sid);
                string best = Directory.Exists(dir) ? Directory.GetFiles(dir, "*Image448*").Concat(Directory.GetFiles(dir, "*Image240*")).FirstOrDefault() : null;
                ImageSource img = null;
                if (best != null)
                {
                    var bi = new BitmapImage(); bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.UriSource = new Uri(best); bi.DecodePixelWidth = 160; bi.EndInit(); bi.Freeze();
                    img = bi;
                }
                pictures[sid] = img;
                return img;
            }
            catch { return null; }
        }

        static ImageSource Wallpaper()
        {
            try
            {
                var sb = new StringBuilder(520);
                Native.SystemParametersInfo(0x73 /* SPI_GETDESKWALLPAPER */, 520, sb, 0);
                if (File.Exists(sb.ToString()))
                {
                    var bi = new BitmapImage(); bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.UriSource = new Uri(sb.ToString()); bi.DecodePixelWidth = 2560; bi.EndInit(); bi.Freeze();
                    return bi;
                }
            }
            catch { }
            return OpenDock.Wallpaper.Blurred;
        }
    }
}
