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
    // A macOS-style lock screen: the wallpaper, a big clock and the date, you at the bottom. It unlocks with a
    // GlassDock passcode, tapped on Liquid Glass keys that bend the wallpaper behind them (as on an iPhone) or typed,
    // or with Windows Hello (PIN, face or fingerprint, checked by Windows itself -- GlassDock never sees any of it).
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
        public static bool HelloReady;   // Windows Hello is set up (checked each time the screen locks)

        public static void Lock()
        {
            if (Active) return;
            if (!Settings.CustomLockScreen) { Native.LockWorkStation(); return; }
            // only with a way to unlock it -- a GlassDock passcode or Windows Hello; otherwise Windows' own lock screen
            Hello.Available(ok =>
            {
                HelloReady = ok;
                if (!ok && !Passcode.IsSet) { Native.LockWorkStation(); return; }
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
            backdrop.Children.Add(wall);
            content.Children.Add(backdrop);
            backdrop.Children.Add(new Border { Background = new LinearGradientBrush(new GradientStopCollection {
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
            topPanel = top;
            SizeChanged += delegate { Layout(top); };
            content.SizeChanged += delegate { Layout(top); };

            if (main)
            {
                // you, at the bottom: your picture (or initials), your name, and how to unlock
                string who_ = name ?? Account();
                who.Children.Add(Avatar(who_, 72, name == null));
                var nm = new TextBlock { Text = who_, FontSize = 17, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 10), Effect = soft };
                who.Children.Add(nm);
                bool code = screen != null && Passcode.IsSet;
                unlockPill = code ? Pill("\uE75F", "Enter Passcode") : Pill("\uE8D7", "Unlock with Windows Hello");
                unlockPill.MouseLeftButtonUp += (s, e) => { e.Handled = true; if (code) ShowKeypad(); else Unlock(); };
                who.Children.Add(unlockPill);
                hint.Text = "Use Windows Lock"; hint.FontSize = 12.5; hint.HorizontalAlignment = HorizontalAlignment.Center; hint.Margin = new Thickness(0, 12, 0, 0);
                hint.Foreground = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)); hint.Cursor = Cursors.Hand; hint.Effect = soft;
                hint.MouseLeftButtonUp += (s, e) => { e.Handled = true; LockScreen.UseWindowsLock(); };
                who.Children.Add(hint);
                content.Children.Add(who);
                if (code) { keypad = BuildKeypad(); content.Children.Add(keypad); }
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
            // any key or click brings up the passcode keypad (digits typed go straight in), or asks for Windows Hello
            // when there's no passcode, as a Mac shows Touch ID
            PreviewKeyDown += (s, e) =>
            {
                if (!Main || verifying || leaving) return;
                if (keypad == null) { Unlock(); return; }
                int d = e.Key >= Key.D0 && e.Key <= Key.D9 ? e.Key - Key.D0 : e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9 ? e.Key - Key.NumPad0 : -1;
                e.Handled = true;
                if (d >= 0) { ShowKeypad(); Type((char)('0' + d), true); }
                else if (e.Key == Key.Back) Backspace();
                else if (e.Key == Key.Escape) HideKeypad();
                else ShowKeypad();
            };
            MouseLeftButtonUp += delegate { if (!Main || verifying || leaving) return; if (keypad == null) Unlock(); else ShowKeypad(); };
            idle.Tick += delegate { idle.Stop(); if (typed.Length == 0) HideKeypad(); };
            SizeChanged += delegate { lensed = false; if (keypadShown) MakeLenses(); };
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
            if (keypad != null) { keypad.LayoutTransform = new ScaleTransform(k, k); lensed = false; if (keypadShown) MakeLenses(); }
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

        // ---------- the passcode keypad ----------
        // iPhone-style: "Enter Passcode", a dot per digit, and round Liquid Glass keys. Each key is a lens over a
        // picture of the wallpaper behind it (GlassDock's own Liquid Glass lens), so the scene bends around its rim
        // exactly as it does through Apple's glass.

        readonly Grid backdrop = new Grid();   // the wallpaper and its shading: what the keys bend
        StackPanel topPanel;
        Grid keypad;
        StackPanel dotRow;
        TextBlock keypadNote, leftLink, rightLink;
        readonly List<Ellipse> dotMarks = new List<Ellipse>();
        readonly List<Tuple<FrameworkElement, Image>> keyLenses = new List<Tuple<FrameworkElement, Image>>();
        readonly Dictionary<char, Action<bool>> keyFlash = new Dictionary<char, Action<bool>>();
        readonly DispatcherTimer idle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
        string typed = "";
        int wrongTries;
        bool keypadShown, lensed, checking;
        const double KeyD = 84, KeyGapX = 26, KeyGapY = 21;
        // the keys' glass (fractions of the key's width): the band at the rim that bends, how far it pulls the scene
        // from outside the key, how fast the bend eases toward the centre, and the rim's light
        const double KeyBend = 0.175, KeyPull = 0.60, KeyFalloff = 2.5, KeySaturation = 1.0, KeyBevel = 0.45, KeyGlint = 0.1;
        const double KeyBlur = 1.5;   // DIPs of smear in the rim band, none in the middle
        const byte KeyFrost = 30;
        const int MaxTries = 5;

        Grid BuildKeypad()
        {
            var soft = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 0, Opacity = 0.3, Color = Colors.Black };
            var g = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0, IsHitTestVisible = false };
            var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            col.Children.Add(new TextBlock { Text = "Enter Passcode", FontSize = 22, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Effect = soft });
            keypadNote = new TextBlock { Text = "Your passcode unlocks GlassDock", FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0), Foreground = new SolidColorBrush(Color.FromArgb(225, 255, 255, 255)), Effect = soft };
            col.Children.Add(keypadNote);
            dotRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 24, 0, 34), RenderTransform = new TranslateTransform() };
            for (int i = 0; i < Math.Max(Passcode.MinLength, Passcode.Length); i++)
            {
                var e = new Ellipse { Width = 13, Height = 13, Stroke = Brushes.White, StrokeThickness = 1.4, Margin = new Thickness(10, 0, 10, 0), Effect = soft };
                dotMarks.Add(e); dotRow.Children.Add(e);
            }
            col.Children.Add(dotRow);
            var keys = new Grid();
            for (int c = 0; c < 3; c++) keys.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(KeyD + KeyGapX) });
            for (int r = 0; r < 4; r++) keys.RowDefinitions.Add(new RowDefinition { Height = new GridLength(KeyD + KeyGapY) });
            string[] digits = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "", "0", "" };
            string[] letters = { "", "ABC", "DEF", "GHI", "JKL", "MNO", "PQRS", "TUV", "WXYZ", "", "", "" };
            for (int i = 0; i < digits.Length; i++)
            {
                if (digits[i] == "") continue;
                var key = KeyButton(digits[i][0], letters[i]);
                Grid.SetRow(key, i / 3); Grid.SetColumn(key, i % 3);
                keys.Children.Add(key);
            }
            col.Children.Add(keys);
            // under the keys: Windows Hello (when it's set up) on the left, Cancel / Delete on the right
            var bottom = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            leftLink = Link(LockScreen.HelloReady ? "Windows Hello" : "Use Windows Lock", HorizontalAlignment.Left);
            leftLink.MouseLeftButtonUp += (s, e) => { e.Handled = true; if (LockScreen.HelloReady) Unlock(); else LockScreen.UseWindowsLock(); };
            rightLink = Link("Cancel", HorizontalAlignment.Right);
            rightLink.MouseLeftButtonUp += (s, e) => { e.Handled = true; if (typed.Length > 0) Backspace(); else HideKeypad(); };
            bottom.Children.Add(leftLink); bottom.Children.Add(rightLink);
            col.Children.Add(bottom);
            g.Children.Add(col);
            return g;
        }

        static TextBlock Link(string text, HorizontalAlignment side)
        {
            return new TextBlock { Text = text, FontSize = 17, FontWeight = FontWeights.Medium, HorizontalAlignment = side, Cursor = Cursors.Hand, Padding = new Thickness(KeyGapX / 2, 8, KeyGapX / 2, 8),
                Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 0, Opacity = 0.3, Color = Colors.Black } };
        }

        FrameworkElement KeyButton(char digit, string letters)
        {
            var press = new ScaleTransform(1, 1);
            var k = new Grid { Width = KeyD, Height = KeyD, Cursor = Cursors.Hand, Background = Brushes.Transparent, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = press };
            // the glass: the wallpaper bent through the key (filled in by MakeLenses), a faint frost, and a lit rim
            var lens = new Image { Stretch = Stretch.Fill, Clip = new EllipseGeometry(new Point(KeyD / 2, KeyD / 2), KeyD / 2, KeyD / 2) };
            k.Children.Add(lens);
            keyLenses.Add(Tuple.Create((FrameworkElement)k, lens));
            var frost = new Ellipse { Fill = new SolidColorBrush(Color.FromArgb(KeyFrost, 255, 255, 255)) };
            k.Children.Add(frost);
            // the rim band glows a little -- light gathered just inside the edge of the glass, where the border is lit
            // (top-left and bottom-right), nearly clear at the sides
            k.Children.Add(new Ellipse { IsHitTestVisible = false, Fill = new RadialGradientBrush(new GradientStopCollection {
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.65), new GradientStop(Color.FromArgb(35, 255, 255, 255), 1) }),
                OpacityMask = new LinearGradientBrush(new GradientStopCollection {
                    new GradientStop(Color.FromArgb(255, 0, 0, 0), 0), new GradientStop(Color.FromArgb(89, 0, 0, 0), 0.5), new GradientStop(Color.FromArgb(230, 0, 0, 0), 1) }, new Point(0.15, 0), new Point(0.85, 1)) });
            // the border: one crisp line, bright along the top-left and bottom-right arcs, gone at the sides
            k.Children.Add(new Ellipse { StrokeThickness = 1.4, Stroke = new LinearGradientBrush(new GradientStopCollection {
                new GradientStop(Color.FromArgb(255, 255, 255, 255), 0), new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.35),
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.65), new GradientStop(Color.FromArgb(230, 255, 255, 255), 1) }, new Point(0.15, 0), new Point(0.85, 1)) });
            var label = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, letters == "" ? 0 : 3, 0, 0),
                Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = 0.18, Color = Colors.Black } };
            label.Children.Add(new TextBlock { Text = digit.ToString(), FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), FontSize = 44, FontWeight = FontWeights.Black,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -8, 0, -6) });
            if (letters != "") label.Children.Add(new TextBlock { Text = letters, FontSize = 11, FontWeight = FontWeights.Black,
                Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 0, Opacity = 0.3, Color = Colors.Black }, HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)) });
            k.Children.Add(label);
            Action<bool> flash = down =>
            {
                frost.Fill = new SolidColorBrush(Color.FromArgb(down ? (byte)105 : KeyFrost, 255, 255, 255));
                var a = new DoubleAnimation(down ? 0.94 : 1, TimeSpan.FromMilliseconds(down ? 70 : 220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                press.BeginAnimation(ScaleTransform.ScaleXProperty, a); press.BeginAnimation(ScaleTransform.ScaleYProperty, a);
            };
            keyFlash[digit] = flash;
            bool down_ = false;
            k.MouseLeftButtonDown += (s, e) => { e.Handled = true; down_ = true; flash(true); };
            k.MouseLeftButtonUp += (s, e) => { e.Handled = true; if (!down_) return; down_ = false; flash(false); Type(digit); };
            k.MouseLeave += delegate { if (down_) { down_ = false; flash(false); } };
            return k;
        }

        void ShowKeypad()
        {
            if (keypad == null || leaving) return;
            idle.Stop(); idle.Start();
            if (keypadShown) return;
            keypadShown = true;
            keypad.IsHitTestVisible = true;
            if (!lensed) MakeLenses();
            var t = TimeSpan.FromMilliseconds(240);
            keypad.BeginAnimation(OpacityProperty, new DoubleAnimation(1, t));
            if (topPanel != null) topPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, t));
            who.IsHitTestVisible = false;
            who.BeginAnimation(OpacityProperty, new DoubleAnimation(0, t));
        }

        void HideKeypad()
        {
            if (keypad == null || !keypadShown || checking) return;
            keypadShown = false; idle.Stop();
            typed = ""; ShowDots();
            keypad.IsHitTestVisible = false;
            var t = TimeSpan.FromMilliseconds(240);
            keypad.BeginAnimation(OpacityProperty, new DoubleAnimation(0, t));
            if (topPanel != null) topPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(1, t));
            who.IsHitTestVisible = true;
            who.BeginAnimation(OpacityProperty, new DoubleAnimation(1, t));
        }

        void Type(char digit, bool fromKeys = false)
        {
            if (checking || typed.Length >= Passcode.Length) return;
            idle.Stop(); idle.Start();
            Action<bool> flash;
            if (fromKeys && keyFlash.TryGetValue(digit, out flash)) { flash(true); Later(90, () => flash(false)); }   // a typed digit lights its key
            typed += digit;
            ShowDots();
            if (typed.Length == Passcode.Length) { checking = true; Later(140, Submit); }
        }

        void Backspace()
        {
            if (checking || typed.Length == 0) { if (typed.Length == 0) HideKeypad(); return; }
            typed = typed.Substring(0, typed.Length - 1);
            ShowDots();
        }

        void ShowDots()
        {
            for (int i = 0; i < dotMarks.Count; i++) dotMarks[i].Fill = i < typed.Length ? Brushes.White : null;
            if (rightLink != null) rightLink.Text = typed.Length > 0 ? "Delete" : "Cancel";
        }

        void Submit()
        {
            string code = typed;
            bool ok = Passcode.Check(code);
            typed = "";
            if (ok) { checking = false; LockScreen.Close(); return; }
            wrongTries++;
            // wrong: the dots shake, as on an iPhone, and empty
            var shake = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
            double[] xs = { -16, 14, -11, 8, -5, 3, 0 };
            for (int i = 0; i < xs.Length; i++) shake.KeyFrames.Add(new EasingDoubleKeyFrame(xs[i], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(60 * (i + 1)))));
            shake.Completed += delegate { checking = false; ShowDots(); };
            ((TranslateTransform)dotRow.RenderTransform).BeginAnimation(TranslateTransform.XProperty, shake);
            int left = MaxTries - wrongTries;
            if (left <= 0) { keypadNote.Text = "Too many tries. Windows' lock takes over."; Later(900, LockScreen.UseWindowsLock); return; }
            keypadNote.Text = left == 1 ? "Wrong passcode. One more try before Windows' lock takes over." : "Wrong passcode. Try again.";
        }

        void Later(int ms, Action a)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += delegate { t.Stop(); if (!leaving) a(); };
            t.Start();
        }

        // Each key's glass: the wallpaper behind it, as shown (shading and all), bent by GlassDock's Liquid Glass lens
        // -- a round lens whose thick rim pulls the scene from just outside the key into view, as Apple's does.
        void MakeLenses()
        {
            try
            {
                UpdateLayout();
                var src = PresentationSource.FromVisual(this);
                if (src == null || backdrop.ActualWidth < 1) return;
                double sx = src.CompositionTarget.TransformToDevice.M11, sy = src.CompositionTarget.TransformToDevice.M22;
                int pw = (int)Math.Round(backdrop.ActualWidth * sx), ph = (int)Math.Round(backdrop.ActualHeight * sy);
                var rtb = new RenderTargetBitmap(pw, ph, 96 * sx, 96 * sy, PixelFormats.Pbgra32);
                rtb.Render(backdrop);
                var conv = new FormatConvertedBitmap(rtb, PixelFormats.Bgr32, null, 0);
                var px = new byte[pw * ph * 4]; conv.CopyPixels(px, pw * 4, 0);
                foreach (var kl in keyLenses)
                {
                    var b = kl.Item1.TransformToVisual(backdrop).TransformBounds(new Rect(0, 0, kl.Item1.ActualWidth, kl.Item1.ActualHeight));
                    var r = new Rect(b.X * sx, b.Y * sy, b.Width * sx, b.Height * sy);
                    kl.Item2.Source = Liquid.Smear(Liquid.Lens(px, pw, ph, r, r.Width / 2, r.Width * KeyBend, r.Width * KeyPull, false, 0, KeySaturation, KeyBevel, KeyGlint, KeyFalloff), r.Width / 2, r.Width * KeyBend, KeyBlur * sx);   // a thin, flat lens: the scene bends in a band at the rim and passes almost straight through the middle, as on the iPhone
                }
                lensed = true;
            }
            catch (Exception ex) { App.Log(ex); }
        }

        // Unlocking: the screen lifts away, as on a Mac.
        public void Leave()
        {
            leaving = true; tick.Stop(); guard.Stop(); idle.Stop();
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
