using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Devices.Radios;

namespace OpenDock
{
    // WinRT async results, delivered on the UI thread (the thread that first asks).
    static class WinRT
    {
        static Dispatcher ui;

        public static void Then<T>(global::Windows.Foundation.IAsyncOperation<T> op, Action<T> done)
        {
            if (ui == null) ui = Dispatcher.CurrentDispatcher;
            op.Completed = (o, status) =>
            {
                if (status != global::Windows.Foundation.AsyncStatus.Completed) return;
                T result;
                try { result = o.GetResults(); } catch { return; }
                Post(() => done(result));
            };
        }

        public static void Post(Action a)
        {
            if (ui != null) ui.BeginInvoke(new Action(() => { try { a(); } catch (Exception ex) { App.Log(ex); } }));
        }
    }

    // Wi-Fi and Bluetooth on/off, through the same radio switches as Windows' quick settings.
    static class Radios
    {
        static IReadOnlyList<Radio> all = new Radio[0];
        static readonly List<Radio> airplaneOff = new List<Radio>();   // what airplane mode turned off, to turn back on

        public static void Load(Action done)
        {
            try { Begin(done); } catch (Exception ex) { App.Log(ex); }
        }

        static void Begin(Action done)
        {
            WinRT.Then(Radio.RequestAccessAsync(), access => WinRT.Then(Radio.GetRadiosAsync(), radios => { all = radios; done(); }));
        }

        public static bool Has(RadioKind kind) { return all.Any(r => r.Kind == kind); }
        public static bool On(RadioKind kind) { return all.Any(r => r.Kind == kind && r.State == RadioState.On); }
        public static bool Airplane { get { return all.Count > 0 && all.All(r => r.State != RadioState.On); } }

        public static void Set(RadioKind kind, bool on, Action done)
        {
            foreach (var r in all.Where(r => r.Kind == kind)) Switch(r, on, done);
        }

        public static void SetAirplane(bool on, Action done)
        {
            if (on)
            {
                airplaneOff.Clear();
                foreach (var r in all.Where(r => r.State == RadioState.On)) { airplaneOff.Add(r); Switch(r, false, done); }
            }
            else foreach (var r in airplaneOff.Count > 0 ? airplaneOff.ToList() : all.ToList()) Switch(r, true, done);
        }

        static void Switch(Radio r, bool on, Action done)
        {
            try { WinRT.Then(r.SetStateAsync(on ? RadioState.On : RadioState.Off), status => done()); }
            catch (Exception ex) { App.Log(ex); }
        }
    }

    // Screen brightness: DDC/CI for external monitors, WMI for a laptop's own panel.
    static class Brightness
    {
        static IntPtr monitor; static bool laptop;
        static int pending = -1; static int busy;

        public static int Read(string device)
        {
            var d = Sys.Displays().FirstOrDefault(x => x.Device == device);
            if (d != null && d.Brightness >= 0) { monitor = d.Monitor; laptop = false; return d.Brightness; }
            try
            {
                using (var s = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness"))
                    foreach (ManagementObject o in s.Get()) using (o) { laptop = true; return Convert.ToInt32(o["CurrentBrightness"]); }
            }
            catch { }
            return -1;
        }

        // Monitors take a while per change; only the latest value of a drag is sent, one at a time.
        public static void Write(int percent)
        {
            Interlocked.Exchange(ref pending, percent);
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
            Task.Run(() =>
            {
                try
                {
                    int v;
                    while ((v = Interlocked.Exchange(ref pending, -1)) >= 0)
                    {
                        if (laptop)
                        {
                            using (var s = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods"))
                                foreach (ManagementObject o in s.Get()) using (o) o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)v });
                        }
                        else Sys.SetBrightness(monitor, v);
                    }
                }
                catch (Exception ex) { App.Log(ex); }
                finally { Interlocked.Exchange(ref busy, 0); if (Volatile.Read(ref pending) >= 0) Write(Volatile.Read(ref pending)); }
            });
        }
    }

    // macOS-style Control Center: Liquid Glass tiles floating under the menu bar, opened from its Control Center
    // icon. The panel's look, animations and dismissal come from GlassPanel.
    class ControlCenterWindow : GlassPanel
    {
        static ControlCenterWindow instance;

        public static void Toggle()
        {
            if (instance == null) instance = new ControlCenterWindow();
            instance.ToggleFrom(null);   // lined up with the screen's right edge, like macOS
        }

        GlassTile wifiTile, btTile, airTile, focusTile, darkTile, timerTile, musicTile, displayTile;
        Border wifiDot, focusDot;
        TextBlock wifiSub, focusSub, wifiGlyph, focusGlyph, muteGlyph;
        Slider2 volume, brightness;

        ControlCenterWindow() : base("OpenDock Control Center", 4 * U + 3 * G + 2 * Pad)
        {
            Height = 6 * U + 6 * G + 30 + 2 * Pad;
            Build();
            NowPlaying.Changed += delegate { if (IsVisible) ShowMusic(); };
        }

        static double X(int col) { return Pad + col * (U + G); }
        static double Y(int row) { return Pad + row * (U + G); }

        void Build()
        {
            wifiTile = Pill(0, 0, out wifiDot, out wifiGlyph, "\uE701", "Wi-Fi", out wifiSub);
            wifiDot.MouseLeftButtonUp += (s, e) => { e.Handled = true; if (Radios.Has(RadioKind.WiFi)) Radios.Set(RadioKind.WiFi, !Radios.On(RadioKind.WiFi), ShowRadios); };
            wifiTile.MouseLeftButtonUp += delegate { Dismiss(false); App.OpenSettings("Wi-Fi"); };

            btTile = Circle(0, 1, "\uE702", Blue, "Bluetooth", delegate { if (Radios.Has(RadioKind.Bluetooth)) Radios.Set(RadioKind.Bluetooth, !Radios.On(RadioKind.Bluetooth), ShowRadios); });
            airTile = Circle(1, 1, "\uE709", Orange, "Airplane mode", delegate { Radios.SetAirplane(!Radios.Airplane, ShowRadios); });

            musicTile = Add(new GlassTile(new Rect(X(2), Y(0), 2 * U + G, 2 * U + G), 32));

            focusTile = Pill(2, 0, out focusDot, out focusGlyph, "\uE708", "Focus", out focusSub);
            focusTile.MouseLeftButtonUp += delegate { Sys.Toasts = !Sys.Toasts; ShowFocus(); };
            Circle(2, 2, "\uE7C4", "Task View", delegate { Dismiss(true); Native.SendKeys(0x5B, 0x09); });
            Circle(3, 2, "\uEC15", "Cast to a display", delegate { Dismiss(true); Native.SendKeys(0x5B, 0x4B); });

            displayTile = SliderTile(3, "Display", "\uEC8A", "\uE706", out brightness);
            brightness.Changed += v => Brightness.Write((int)Math.Round(v * 100));
            SliderTile(4, "Sound", "\uE993", "\uE995", out volume);
            volume.Changed += v => { Sys.SetVolume(true, (float)v); if (Sys.GetMute(true) && v > 0) { Sys.SetMute(true, false); ShowVolume(); } };

            darkTile = Circle(0, 5, "\uE793", Colors.Black, "Dark Mode", delegate { ToggleDark(); });
            Circle(1, 5, "\uE8EF", "Calculator", delegate { Dismiss(false); Launcher.Shell("calculator:"); });
            timerTile = Circle(2, 5, "\uE916", Orange, "Timer", delegate { TimerMenu(); });
            Circle(3, 5, "\uE722", "Screenshot", delegate { Dismiss(true); Later(300, () => Launcher.Shell("ms-screenclip:")); });

            var edit = Add(new GlassTile(new Rect((Width - 124) / 2, Y(6), 124, 30), 15) { Interactive = true, Cursor = Cursors.Hand });
            edit.Body.Children.Add(new TextBlock { Text = "Edit Controls", FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            edit.MouseLeftButtonUp += delegate { Dismiss(false); App.OpenSettings("Control Center"); };
        }

        protected override void Refresh()
        {
            NowPlaying.Start();
            ShowMusic(); ShowFocus(); ShowDark(); ShowTimer(); ShowVolume();
            Radios.Load(ShowRadios);
            string device = DisplayLayout.Selected.Device;
            brightness.IsEnabled = false; brightness.Opacity = 0.45;
            Task.Run(() => Brightness.Read(device)).ContinueWith(t =>
            {
                bool ok = t.Result >= 0;
                brightness.IsEnabled = ok; brightness.Opacity = ok ? 1 : 0.45;
                if (ok) brightness.Value = t.Result / 100.0;
                displayTile.ToolTip = ok ? null : "This display doesn't let Windows change its brightness";
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // ---------- state ----------

        void ShowRadios()
        {
            bool wifi = Radios.On(RadioKind.WiFi), bt = Radios.On(RadioKind.Bluetooth), air = Radios.Airplane;
            Dot(wifiDot, wifiGlyph, wifi, Blue);
            btTile.Active = bt; btTile.Paint(light);
            airTile.Active = air; airTile.Paint(light);
            if (!Radios.Has(RadioKind.WiFi)) { wifiSub.Text = "No Wi-Fi adapter"; return; }
            if (!wifi) { wifiSub.Text = "Off"; return; }
            Task.Run(() => Sys.Wifi()).ContinueWith(t => { wifiSub.Text = t.Result.Connected && !string.IsNullOrEmpty(t.Result.Ssid) ? t.Result.Ssid : "Not Connected"; },
                TaskScheduler.FromCurrentSynchronizationContext());
        }

        void ShowFocus()
        {
            bool dnd = !Sys.Toasts;
            Dot(focusDot, focusGlyph, dnd, Purple);
            focusSub.Text = dnd ? "Do Not Disturb" : "";
            focusSub.Visibility = dnd ? Visibility.Visible : Visibility.Collapsed;
        }

        void ShowDark()
        {
            darkTile.Active = Theme.Dark;
            darkTile.Paint(light);
        }

        void ShowTimer()
        {
            bool on = Living.TimerText != "";
            timerTile.Active = on;
            timerTile.ToolTip = on ? "Timer: " + Living.TimerText + (Living.TimerRunning ? "" : " (paused)") : "Timer";
            timerTile.Paint(light);
        }

        void ShowVolume()
        {
            volume.Value = Sys.GetVolume(true);
            bool muted = Sys.GetMute(true);
            muteGlyph.Text = muted ? "\uE74F" : "\uE995";
            ((FrameworkElement)muteGlyph.Parent).ToolTip = muted ? "Unmute" : "Mute";
        }

        void ShowMusic()
        {
            var body = musicTile.Body;
            body.Children.Clear();
            // 140 tall inside: 14 + 44 art + 6 + title + artist + 26 controls + 8 fits without spilling over the edge
            var stack = new StackPanel { Margin = new Thickness(16, 14, 12, 8) };
            var art = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(10), HorizontalAlignment = HorizontalAlignment.Left };
            art.SetResourceReference(Border.BackgroundProperty, "CcInner");
            if (NowPlaying.Art != null) art.Background = new ImageBrush(NowPlaying.Art) { Stretch = Stretch.UniformToFill };
            else art.Child = Glyph("\uE8D6", 20);
            stack.Children.Add(art);
            bool active = NowPlaying.Active;
            var title = Text(active ? NowPlaying.Title : "Not Playing", 14, FontWeights.SemiBold);
            title.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(title);
            stack.Children.Add(Text(active ? NowPlaying.Artist ?? "" : "", 12, FontWeights.Normal, true));
            var controls = new Grid { Margin = new Thickness(0, 2, 0, 0), Height = 26, Opacity = active ? 1 : 0.45 };
            for (int i = 0; i < 3; i++) controls.ColumnDefinitions.Add(new ColumnDefinition());
            string[] shapes = { PreviousShape, NowPlaying.Playing ? PauseShape : PlayShape, NextShape };
            Action[] acts = { NowPlaying.Previous, NowPlaying.Toggle, NowPlaying.Next };
            for (int i = 0; i < 3; i++)
            {
                var b = Transport(shapes[i], i == 1 ? 17 : 13, acts[i]);
                Grid.SetColumn(b, i);
                controls.Children.Add(b);
            }
            stack.Children.Add(controls);
            body.Children.Add(stack);
        }

        // ---------- actions ----------

        void TimerMenu()
        {
            var cm = new ContextMenu { PlacementTarget = timerTile, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            if (Living.TimerText != "") cm.Items.Add(Item(Living.TimerRunning ? "Pause Timer (" + Living.TimerText + ")" : "Resume Timer (" + Living.TimerText + ")", () => { Living.PauseTimer(); ShowTimer(); }));
            foreach (int m in new[] { 1, 3, 5, 10, 15, 30, 60 }) { int min = m; cm.Items.Add(Item("Start " + m + (m == 1 ? " Minute" : " Minutes"), () => { Living.StartTimer(min); ShowTimer(); })); }
            cm.IsOpen = true;
        }

        void OutputMenu(FrameworkElement anchor)
        {
            var cm = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            foreach (var dev in Sys.AudioDevices(true)) { var d = dev; var mi = Item(d.Name, () => { Sys.SetDefaultAudio(d.Id); ShowVolume(); }); mi.IsChecked = d.Default; cm.Items.Add(mi); }
            if (cm.Items.Count == 0) cm.Items.Add(new MenuItem { Header = "No output devices", IsEnabled = false });
            cm.Items.Add(new Separator());
            cm.Items.Add(Item("Sound Settings…", () => { Dismiss(false); App.OpenSettings("Sound"); }));
            cm.IsOpen = true;
        }

        static MenuItem Item(string text, Action act)
        {
            var mi = new MenuItem { Header = text };
            mi.Click += delegate { act(); };
            return mi;
        }

        // Dark Mode switches Windows' own apps and system colours too, as macOS does, and OpenDock follows.
        void ToggleDark()
        {
            bool dark = !Theme.Dark;
            const string key = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
            Registry.SetValue(key, "AppsUseLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
            Registry.SetValue(key, "SystemUsesLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
            Settings.Appearance = 0; Settings.Save(); Theme.Refresh();
            ShowDark();
            Task.Run(() => { IntPtr r; SendMessageTimeout((IntPtr)0xFFFF, 0x1A /* WM_SETTINGCHANGE */, IntPtr.Zero, "ImmersiveColorSet", 2 /* ABORTIFHUNG */, 500, out r); });
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr result);

        // ---------- tiles ----------

        GlassTile Circle(int col, int row, string glyph, string tip, Action click)
        {
            return Circle(col, row, glyph, Blue, tip, click);
        }

        GlassTile Circle(int col, int row, string glyph, Color accent, string tip, Action click)
        {
            var t = Add(new GlassTile(new Rect(X(col), Y(row), U, U), U / 2) { ToolTip = tip, Cursor = Cursors.Hand, Accent = accent, Interactive = true });
            t.Icon = Glyph(glyph, 22);
            t.Body.Children.Add(t.Icon);
            t.MouseLeftButtonUp += delegate { click(); };
            return t;
        }

        // A wide tile, two columns from `col`: a round switch on the left, a name and a status beside it.
        GlassTile Pill(int row, int col, out Border dot, out TextBlock glyph, string icon, string name, out TextBlock sub)
        {
            var t = Add(new GlassTile(new Rect(X(col), Y(row), 2 * U + G, U), U / 2) { Cursor = Cursors.Hand, Interactive = true });
            // a 40px switch and tight gaps leave ~78px for the words, enough for a typical network name
            var dock = new DockPanel { Margin = new Thickness(10, 0, 6, 0) };
            dot = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), VerticalAlignment = VerticalAlignment.Center };
            dot.SetResourceReference(Border.BackgroundProperty, "CcInner");
            glyph = Glyph(icon, 17);
            dot.Child = glyph;
            DockPanel.SetDock(dot, Dock.Left);
            dock.Children.Add(dot);
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            words.Children.Add(Text(name, 14, FontWeights.SemiBold));
            sub = Text("", 11.5, FontWeights.Normal, true);
            words.Children.Add(sub);
            dock.Children.Add(words);
            t.Body.Children.Add(dock);
            return t;
        }

        GlassTile SliderTile(int row, string name, string low, string high, out Slider2 slider)
        {
            var t = Add(new GlassTile(new Rect(X(0), Y(row), 4 * U + 3 * G, U), U / 2));
            var g = new Grid { Margin = new Thickness(20, 9, 14, 9) };
            g.RowDefinitions.Add(new RowDefinition()); g.RowDefinitions.Add(new RowDefinition());
            g.Children.Add(Text(name, 14, FontWeights.SemiBold));
            var row2 = new DockPanel();
            Grid.SetRow(row2, 1);
            var lo = Glyph(low, 13); lo.Margin = new Thickness(0, 0, 10, 0);
            DockPanel.SetDock(lo, Dock.Left); row2.Children.Add(lo);
            if (name == "Sound")
            {
                // side by side, each in its own 24px circle (the row is ~23px), so the loud-speaker icon can't run into the output button
                var outBtn = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0) };
                var mute = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = Glyph(high, 14) };
                muteGlyph = (TextBlock)mute.Child;
                mute.MouseLeftButtonUp += (s, e) => { e.Handled = true; Sys.SetMute(true, !Sys.GetMute(true)); ShowVolume(); };
                outBtn.Children.Add(mute);
                var route = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Cursor = Cursors.Hand, Margin = new Thickness(6, 0, 0, 0), Child = Glyph("\uE7F5", 12), ToolTip = "Sound output" };
                route.SetResourceReference(Border.BackgroundProperty, "CcInner");
                route.MouseLeftButtonUp += (s, e) => { e.Handled = true; OutputMenu(route); };
                outBtn.Children.Add(route);
                DockPanel.SetDock(outBtn, Dock.Right); row2.Children.Add(outBtn);
            }
            else
            {
                var hi = Glyph(high, 15); hi.Margin = new Thickness(10, 0, 0, 0);
                DockPanel.SetDock(hi, Dock.Right); row2.Children.Add(hi);
            }
            slider = new Slider2 { VerticalAlignment = VerticalAlignment.Center };
            row2.Children.Add(slider);
            g.Children.Add(row2);
            t.Body.Children.Add(g);
            return t;
        }
    }

    // A thick macOS-style slider: a pale track that fills up. Drag or click anywhere on it.
    class Slider2 : Grid
    {
        readonly Border fill = new Border { CornerRadius = new CornerRadius(7), HorizontalAlignment = HorizontalAlignment.Left };
        double value;
        public event Action<double> Changed;

        public Slider2()
        {
            Height = 14; Cursor = Cursors.Hand; Background = Brushes.Transparent;
            var track = new Border { CornerRadius = new CornerRadius(7) };
            track.SetResourceReference(Border.BackgroundProperty, "CcTrack");
            fill.SetResourceReference(Border.BackgroundProperty, "CcFill");
            Children.Add(track);
            Children.Add(fill);
            SizeChanged += delegate { Draw(); };
            MouseLeftButtonDown += (s, e) => { if (!IsEnabled) return; CaptureMouse(); Drag(e); e.Handled = true; };
            MouseMove += (s, e) => { if (IsMouseCaptured) Drag(e); };
            MouseLeftButtonUp += (s, e) => { if (IsMouseCaptured) ReleaseMouseCapture(); e.Handled = true; };
        }

        public double Value { get { return value; } set { this.value = Math.Max(0, Math.Min(1, value)); Draw(); } }

        void Drag(MouseEventArgs e)
        {
            Value = e.GetPosition(this).X / Math.Max(1, ActualWidth);
            if (Changed != null) Changed(value);
        }

        void Draw() { fill.Width = Math.Max(Height, ActualWidth * value); }
    }
}
