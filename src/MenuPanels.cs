using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.Devices.Radios;

namespace OpenDock
{
    // The menu bar's Wi-Fi menu: the switch, the network you're on, the others nearby (click one to join; a new
    // secured one asks for its password right in the list) and a way to the full settings.
    class WifiPanel : GlassPanel
    {
        static WifiPanel instance;
        public static void Toggle(FrameworkElement from)
        {
            if (instance == null) instance = new WifiPanel();
            instance.ToggleFrom(from);
        }

        static Sys.WifiState last;   // the previous scan, shown at once while a new one runs
        readonly Switch2 power = new Switch2();
        string asking, joining;      // the network whose password row is open / that we're joining
        bool scanning;

        WifiPanel() : base("OpenDock Wi-Fi", 300 + 2 * Pad)
        {
            power.Changed += on =>
            {
                if (!Radios.Has(RadioKind.WiFi)) return;
                Radios.Set(RadioKind.WiFi, on, () => { Relayout(Build); Later(on ? 2500 : 300, Rescan); });
            };
        }

        protected override void Prepare() { asking = null; Build(); }

        protected override void Refresh()
        {
            Radios.Load(() => { power.Set(Radios.On(RadioKind.WiFi), false); power.IsEnabled = Radios.Has(RadioKind.WiFi); });
            Rescan();
        }

        void Rescan()
        {
            if (scanning) return;
            scanning = true;
            Task.Run(() => Sys.Wifi()).ContinueWith(t =>
            {
                scanning = false;
                if (t.Exception == null) last = t.Result;
                if (IsVisible) Relayout(Build);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void Build()
        {
            ClearTiles();
            double w = Width - 2 * Pad, y = Pad;

            var head = Add(new GlassTile(new Rect(Pad, y, w, 50), 25));
            var bar = new DockPanel { Margin = new Thickness(18, 0, 13, 0) };
            var parent = power.Parent as Panel;
            if (parent != null) parent.Children.Remove(power);
            DockPanel.SetDock(power, Dock.Right);
            bar.Children.Add(power);
            bar.Children.Add(Text("Wi-Fi", 14, FontWeights.SemiBold));
            head.Body.Children.Add(bar);
            y += 50 + G;

            var list = new StackPanel { Margin = new Thickness(6, 8, 6, 8) };
            var s = last;
            if (s == null) list.Children.Add(Note("Looking for networks…"));
            else if (!s.HasAdapter) list.Children.Add(Note("This PC has no Wi-Fi adapter."));
            else if (Radios.Has(RadioKind.WiFi) && !Radios.On(RadioKind.WiFi)) list.Children.Add(Note("Wi-Fi is off."));
            else
            {
                if (s.Connected)
                {
                    var off = Text("Disconnect", 12, FontWeights.Normal, true);
                    off.Cursor = Cursors.Hand;
                    off.MouseLeftButtonUp += (o, e) => { e.Handled = true; Task.Run(() => Sys.WifiDisconnect()).ContinueWith(_ => Later(1500, Rescan), TaskScheduler.FromCurrentSynchronizationContext()); };
                    list.Children.Add(Row(Badge("\uE701", true, Blue), s.Ssid, "Connected", off, null));
                }
                var nets = s.Nets.Where(n => !(s.Connected && n.Ssid == s.Ssid)).Take(8).ToList();
                if (nets.Count > 0) list.Children.Add(Section(s.Connected ? "Other Networks" : "Networks"));
                foreach (var net in nets)
                {
                    var n = net;
                    string status = joining == n.Ssid ? "Joining…" : n.Known ? "Known network" : null;
                    list.Children.Add(Row(Badge("\uE701", false, Blue), n.Ssid, status, Signal(n), () => Join(n)));
                    if (asking == n.Ssid) list.Children.Add(PasswordRow(n));
                }
                if (nets.Count == 0 && (!s.Connected || s.Error != null)) list.Children.Add(Note(s.Error ?? "No networks nearby."));
            }
            list.Measure(new Size(w, double.PositiveInfinity));
            double lh = Math.Ceiling(list.DesiredSize.Height);
            var networks = Add(new GlassTile(new Rect(Pad, y, w, lh), 22));
            networks.Body.Children.Add(list);
            y += lh + G;

            var foot = Add(new GlassTile(new Rect(Pad, y, w, 44), 22));
            foot.Body.Children.Add(Row(null, "Wi-Fi Settings…", null, null, () => { Dismiss(false); App.OpenSettings("Wi-Fi"); }));
            Height = y + 44 + Pad;
        }

        static FrameworkElement Signal(Sys.WifiNet n)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal };
            if (n.Secure) { var lk = Glyph("\uE72E", 11); lk.Margin = new Thickness(0, 0, 8, 0); lk.Opacity = 0.7; p.Children.Add(lk); }
            var bars = Glyph("\uE701", 13);
            bars.Opacity = n.Signal >= 70 ? 1 : n.Signal >= 40 ? 0.7 : 0.45;
            p.Children.Add(bars);
            return p;
        }

        void Join(Sys.WifiNet n)
        {
            if (joining != null) return;
            if (n.Known || !n.Secure) { Connect(n, null); return; }
            asking = asking == n.Ssid ? null : n.Ssid;
            Relayout(Build);
        }

        void Connect(Sys.WifiNet n, string password)
        {
            asking = null; joining = n.Ssid;
            Relayout(Build);
            Task.Run(() => Sys.WifiConnect(n, password)).ContinueWith(_ => Later(2500, () => { joining = null; Rescan(); }), TaskScheduler.FromCurrentSynchronizationContext());
        }

        FrameworkElement PasswordRow(Sys.WifiNet n)
        {
            var pw = new PasswordBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 0, 8, 0), FontSize = 13 };
            pw.SetResourceReference(Control.ForegroundProperty, "CcFg");
            pw.SetResourceReference(PasswordBox.CaretBrushProperty, "CcFg");
            var field = new Border { CornerRadius = new CornerRadius(8), Height = 30, Child = pw };
            field.SetResourceReference(Border.BackgroundProperty, "CcInner");
            var join = new Border { CornerRadius = new CornerRadius(8), Height = 30, Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(8, 0, 0, 0), Background = new SolidColorBrush(Blue), Cursor = Cursors.Hand,
                Child = new TextBlock { Text = "Join", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center } };
            Action go = () => Connect(n, pw.Password);
            join.MouseLeftButtonUp += (s, e) => { e.Handled = true; go(); };
            pw.KeyDown += (s, e) => { if (e.Key == Key.Enter) { e.Handled = true; go(); } };
            var d = new DockPanel { Margin = new Thickness(46, 2, 8, 8) };
            DockPanel.SetDock(join, Dock.Right);
            d.Children.Add(join); d.Children.Add(field);
            Later(80, () => pw.Focus());
            return d;
        }
    }

    // The menu bar's Sound menu: the volume, the output devices (click one to switch) and the full settings.
    class SoundPanel : GlassPanel
    {
        static SoundPanel instance;
        public static void Toggle(FrameworkElement from)
        {
            if (instance == null) instance = new SoundPanel();
            instance.ToggleFrom(from);
        }

        Slider2 volume;
        TextBlock muteGlyph;

        SoundPanel() : base("OpenDock Sound", 300 + 2 * Pad) { }

        protected override void Prepare() { Build(); }

        protected override void Refresh()
        {
            volume.Value = Sys.GetVolume(true);
            bool muted = Sys.GetMute(true);
            muteGlyph.Text = muted ? "\uE74F" : "\uE995";
            ((FrameworkElement)muteGlyph.Parent).ToolTip = muted ? "Unmute" : "Mute";
        }

        void Build()
        {
            ClearTiles();
            double w = Width - 2 * Pad, y = Pad;

            var top = Add(new GlassTile(new Rect(Pad, y, w, 76), 26));
            var g = new StackPanel { Margin = new Thickness(18, 12, 12, 10) };
            g.Children.Add(Text("Sound", 14, FontWeights.SemiBold));
            var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            var lo = Glyph("\uE993", 13); lo.Margin = new Thickness(0, 0, 10, 0);
            DockPanel.SetDock(lo, Dock.Left); row.Children.Add(lo);
            var mute = new Border { Width = 28, Height = 24, CornerRadius = new CornerRadius(12), Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(6, 0, 0, 0) };
            muteGlyph = Glyph("\uE995", 15);
            mute.Child = muteGlyph;
            mute.MouseLeftButtonUp += (s, e) => { e.Handled = true; Sys.SetMute(true, !Sys.GetMute(true)); Refresh(); };
            DockPanel.SetDock(mute, Dock.Right); row.Children.Add(mute);
            volume = new Slider2 { VerticalAlignment = VerticalAlignment.Center };
            volume.Changed += v => { Sys.SetVolume(true, (float)v); if (Sys.GetMute(true) && v > 0) { Sys.SetMute(true, false); Refresh(); } };
            row.Children.Add(volume);
            g.Children.Add(row);
            top.Body.Children.Add(g);
            y += 76 + G;

            var list = new StackPanel { Margin = new Thickness(6, 6, 6, 8) };
            list.Children.Add(Section("Output"));
            var devices = Sys.AudioDevices(true);
            foreach (var dev in devices)
            {
                var d = dev;
                bool ears = d.Name.IndexOf("headphone", StringComparison.OrdinalIgnoreCase) >= 0 || d.Name.IndexOf("headset", StringComparison.OrdinalIgnoreCase) >= 0;
                list.Children.Add(Row(Badge(ears ? "\uE7F6" : "\uE7F5", d.Default, Blue), d.Name, null, null, () => { Sys.SetDefaultAudio(d.Id); Relayout(Build); }));
            }
            if (devices.Count == 0) list.Children.Add(Note("No sound output devices."));
            list.Measure(new Size(w, double.PositiveInfinity));
            double lh = Math.Ceiling(list.DesiredSize.Height);
            var outputs = Add(new GlassTile(new Rect(Pad, y, w, lh), 22));
            outputs.Body.Children.Add(list);
            y += lh + G;

            var foot = Add(new GlassTile(new Rect(Pad, y, w, 44), 22));
            foot.Body.Children.Add(Row(null, "Sound Settings…", null, null, () => { Dismiss(false); App.OpenSettings("Sound"); }));
            Height = y + 44 + Pad;
        }
    }

    // macOS opens Notification Center from the date and time: today's date, this month, what's playing and a
    // running timer, as widgets. (Windows keeps notifications to itself; a link at the bottom shows them.)
    class ClockPanel : GlassPanel
    {
        static ClockPanel instance;
        public static void Toggle(FrameworkElement from)
        {
            if (instance == null) instance = new ClockPanel();
            instance.ToggleFrom(from);
        }

        readonly DispatcherTimer tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        TextBlock time, timerText;

        ClockPanel() : base("OpenDock Notification Center", 4 * U + 3 * G + 2 * Pad)
        {
            tick.Tick += delegate
            {
                if (time != null) time.Text = DateTime.Now.ToString("t");
                if (timerText != null) timerText.Text = Living.TimerText;
            };
            IsVisibleChanged += delegate { if (IsVisible) tick.Start(); else tick.Stop(); };
            NowPlaying.Changed += delegate { if (IsVisible) Relayout(Build); };
        }

        protected override void Prepare() { NowPlaying.Start(); Build(); }
        protected override void Refresh() { }

        void Build()
        {
            ClearTiles();
            double w = Width - 2 * Pad, y = Pad;
            var now = DateTime.Now;

            var date = Add(new GlassTile(new Rect(Pad, y, w, 86), 28));
            var dg = new Grid { Margin = new Thickness(20, 0, 20, 0) };
            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var day = Text(now.ToString("dddd"), 13, FontWeights.SemiBold);
            day.Foreground = new SolidColorBrush(Red);
            left.Children.Add(day);
            left.Children.Add(Text(now.ToString("MMMM d"), 22, FontWeights.SemiBold));
            time = Text(now.ToString("t"), 26, FontWeights.Light);
            time.HorizontalAlignment = HorizontalAlignment.Right;
            dg.Children.Add(left); dg.Children.Add(time);
            date.Body.Children.Add(dg);
            y += 86 + G;

            var cal = Add(new GlassTile(new Rect(Pad, y, w, 264), 28));
            cal.Body.Children.Add(Month(now));
            y += 264 + G;

            if (NowPlaying.Active)
            {
                var media = Add(new GlassTile(new Rect(Pad, y, w, 76), 26));
                var md = new DockPanel { Margin = new Thickness(14, 0, 10, 0) };
                var art = new Border { Width = 48, Height = 48, CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Center };
                art.SetResourceReference(Border.BackgroundProperty, "CcInner");
                if (NowPlaying.Art != null) art.Background = new ImageBrush(NowPlaying.Art) { Stretch = Stretch.UniformToFill };
                else art.Child = Glyph("\uE8D6", 20);
                DockPanel.SetDock(art, Dock.Left); md.Children.Add(art);
                var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                controls.Children.Add(Transport(NowPlaying.Playing ? PauseShape : PlayShape, 16, NowPlaying.Toggle));
                controls.Children.Add(Transport(NextShape, 12, NowPlaying.Next));
                DockPanel.SetDock(controls, Dock.Right); md.Children.Add(controls);
                var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 6, 0) };
                words.Children.Add(Text(NowPlaying.Title, 13.5, FontWeights.SemiBold));
                words.Children.Add(Text(NowPlaying.Artist ?? "", 12, FontWeights.Normal, true));
                md.Children.Add(words);
                media.Body.Children.Add(md);
                y += 76 + G;
            }

            timerText = null;
            if (Living.TimerText != "")
            {
                var timer = Add(new GlassTile(new Rect(Pad, y, w, 56), 28));
                var td = new DockPanel { Margin = new Thickness(18, 0, 10, 0) };
                var icon = Glyph("\uE916", 16);
                icon.Foreground = new SolidColorBrush(Orange); icon.Margin = new Thickness(0, 0, 10, 0);
                DockPanel.SetDock(icon, Dock.Left); td.Children.Add(icon);
                var pause = Transport(Living.TimerRunning ? PauseShape : PlayShape, 13, () => { Living.PauseTimer(); Relayout(Build); });
                DockPanel.SetDock(pause, Dock.Right); td.Children.Add(pause);
                timerText = Text(Living.TimerText, 18, FontWeights.SemiBold);
                timerText.Foreground = new SolidColorBrush(Orange);
                td.Children.Add(timerText);
                timer.Body.Children.Add(td);
                y += 56 + G;
            }

            var link = Add(new GlassTile(new Rect((Width - 210) / 2, y, 210, 30), 15) { Interactive = true, Cursor = Cursors.Hand });
            var lt = Text("Windows Notifications…", 12.5, FontWeights.Normal);
            lt.HorizontalAlignment = HorizontalAlignment.Center;
            link.Body.Children.Add(lt);
            link.MouseLeftButtonUp += delegate { Dismiss(true); Native.SendKeys(0x5B, 0x4E); };
            Height = y + 30 + Pad;
        }

        // This month, weeks starting on the day your region starts them; today in a red circle.
        static FrameworkElement Month(DateTime now)
        {
            var format = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat;
            int first = (int)format.FirstDayOfWeek;
            var root = new StackPanel { Margin = new Thickness(16, 14, 16, 10) };
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 6) };
            var month = Text(now.ToString("MMMM").ToUpper(), 12.5, FontWeights.Bold);
            month.Foreground = new SolidColorBrush(Red);
            head.Children.Add(month);
            head.Children.Add(Text("  " + now.Year, 12.5, FontWeights.SemiBold, true));
            root.Children.Add(head);
            var grid = new UniformGrid { Columns = 7 };
            for (int i = 0; i < 7; i++)
            {
                var t = Text(format.ShortestDayNames[(first + i) % 7].Substring(0, 1), 11, FontWeights.SemiBold, true);
                t.HorizontalAlignment = HorizontalAlignment.Center;
                grid.Children.Add(t);
            }
            var start = new DateTime(now.Year, now.Month, 1);
            var day = start.AddDays(-(((int)start.DayOfWeek - first + 7) % 7));
            for (int i = 0; i < 42; i++, day = day.AddDays(1))
            {
                bool today = day.Date == now.Date;
                var t = new TextBlock { Text = day.Day.ToString(), FontSize = 12.5, FontWeight = today ? FontWeights.Bold : FontWeights.Normal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                if (today) t.Foreground = Brushes.White;
                else if (day.Month != now.Month) t.Opacity = 0.35;
                var cell = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 1, 0, 1), Child = t };
                if (today) cell.Background = new SolidColorBrush(Red);
                grid.Children.Add(cell);
            }
            root.Children.Add(grid);
            return root;
        }
    }
}
