using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ellipse = System.Windows.Shapes.Ellipse;
using System.Windows.Shell;
using System.Windows.Threading;

namespace OpenDock
{
    // The System Settings panes that stand in for Windows Settings pages. Each reads and changes the real
    // setting through Sys; anything slow loads in the background behind a "Loading…" line.
    partial class SettingsWindow
    {
        // Runs work off the UI thread, then fills a slot in the pane (if it's still the pane on screen).
        void Load<T>(StackPanel body, Func<T> work, Action<T, StackPanel> fill)
        {
            var slot = new StackPanel();
            var wait = Text("Loading…", 13, "FgDim"); wait.Margin = new Thickness(4, 14, 0, 0);
            slot.Children.Add(wait);
            body.Children.Add(slot);
            Task.Run(work).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (paneHost.Content != body) return;
                slot.Children.Clear();
                if (t.IsFaulted) { slot.Children.Add(Text("Couldn't read this setting.", 13, "FgDim")); return; }
                fill(t.Result, slot);
            })));
        }

        void Refresh(int delayMs)
        {
            string pane = current;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
            t.Tick += delegate { t.Stop(); if (current == pane && IsLoaded) Show(pane, false); };
            t.Start();
        }

        static void OffUi(Action a) { Task.Run(a); }

        // A switch that changes a system setting (not an OpenDock one).
        static void Toggle(Panel p, string label, bool value, Action<bool> set)
        {
            var cb = new CheckBox { Content = Text(label, 13, "Fg"), IsChecked = value, Style = (Style)Application.Current.Resources["Switch"], Cursor = Cursors.Hand };
            cb.Click += delegate { set(cb.IsChecked == true); };
            Row(p, cb);
        }

        // A slider that applies its value after you stop moving it for a moment (some settings are slow to write).
        static void Level(Panel p, string label, double min, double max, double value, string left, string right, Action<double> set)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(Text(label, 13, "Fg"));
            var l = Text(left ?? "", 11, "FgDim"); l.Margin = new Thickness(0, 0, 8, 0); Grid.SetColumn(l, 1);
            var r = Text(right ?? "", 11, "FgDim"); r.Margin = new Thickness(8, 0, 0, 0); Grid.SetColumn(r, 3);
            var s = new Slider { Minimum = min, Maximum = max, Value = value, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(s, 2);
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            t.Tick += delegate { t.Stop(); double v = s.Value; set(v); };
            s.ValueChanged += delegate { t.Stop(); t.Start(); };
            foreach (var x in new[] { l, r }) if (x.Text.Length == 1 && x.Text[0] >= '') { x.FontFamily = Theme.Glyphs; x.FontSize = 13; }   // icon labels (speaker...)
            g.Children.Add(l); g.Children.Add(s); g.Children.Add(r);
            Row(p, g);
        }

        // A slider over named steps ("Never", "5 minutes"...), showing the current one on the right.
        static void Steps(Panel p, string label, string[] names, int index, Action<int> set)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            g.Children.Add(Text(label, 13, "Fg"));
            var s = new Slider { Minimum = 0, Maximum = names.Length - 1, Value = Math.Max(0, index), IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center };
            var v = Text(names[Math.Max(0, index)], 13, "FgDim"); v.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(s, 1); Grid.SetColumn(v, 2);
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            t.Tick += delegate { t.Stop(); int i = (int)s.Value; OffUi(() => set(i)); };
            s.ValueChanged += delegate { v.Text = names[(int)s.Value]; t.Stop(); t.Start(); };
            g.Children.Add(s); g.Children.Add(v);
            Row(p, g);
        }

        // A row you can pick (default device, power mode...): a check mark when it's the current one.
        static void Choice(Panel p, string label, string detail, bool chosen, Action pick)
        {
            var d = new DockPanel { Background = Brushes.Transparent, Cursor = chosen ? Cursors.Arrow : Cursors.Hand };
            var mark = Text(chosen ? "" : "", 13, "Accent"); mark.FontFamily = Theme.Glyphs; mark.Width = 22;
            DockPanel.SetDock(mark, Dock.Left);
            d.Children.Add(mark);
            if (detail != null) { var x = Text(detail, 13, "FgDim"); DockPanel.SetDock(x, Dock.Right); d.Children.Add(x); }
            var name = Text(label, 13, "Fg"); name.TextTrimming = TextTrimming.CharacterEllipsis;
            d.Children.Add(name);
            if (!chosen && pick != null) d.MouseLeftButtonUp += delegate { pick(); };
            Row(p, d);
        }

        // A row that opens another System Settings pane.
        void NavLink(Panel p, string label, string glyph, string color, string pane)
        {
            var d = new DockPanel { Background = Brushes.Transparent, Cursor = Cursors.Hand };
            if (glyph != null)
            {
                var tile = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(6), Background = new LinearGradientBrush(Lighter(color), Hex(color), 90), Margin = new Thickness(0, 0, 10, 0) };
                tile.Child = new TextBlock { Text = glyph, FontFamily = Theme.Glyphs, FontSize = 12, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(tile, Dock.Left);
                d.Children.Add(tile);
            }
            var go = Text("", 11, "FgDim"); go.FontFamily = Theme.Glyphs;
            DockPanel.SetDock(go, Dock.Right);
            d.Children.Add(go); d.Children.Add(Text(label, 13, "Fg"));
            d.MouseLeftButtonUp += delegate { Show(pane, true); };
            Row(p, d);
        }

        static void Note(Panel body, string text)
        {
            var t = Text(text, 11.5, "FgDim");
            t.TextWrapping = TextWrapping.Wrap; t.Margin = new Thickness(4, 10, 4, 0);
            body.Children.Add(t);
        }

        // ----- Wi-Fi -----

        void WiFi(StackPanel body)
        {
            Load(body, Sys.Wifi, (w, slot) =>
            {
                var top = Group(slot);
                if (!w.HasAdapter) { InfoRow(top, "Wi-Fi", "No Wi-Fi adapter"); Note(slot, "This PC connects over Ethernet. See Network for its connection."); return; }
                if (w.Connected)
                {
                    var d = new DockPanel();
                    var off = Pill("Disconnect", () => { OffUi(Sys.WifiDisconnect); Refresh(2500); });
                    DockPanel.SetDock(off, Dock.Right);
                    var name = new StackPanel();
                    name.Children.Add(Text(w.Ssid, 13, "Fg", FontWeights.SemiBold));
                    name.Children.Add(Text("Connected · signal " + w.Signal, 11.5, "FgDim"));
                    d.Children.Add(off); d.Children.Add(name);
                    Row(top, d);
                }
                else InfoRow(top, "Wi-Fi", "Not connected");

                var others = w.Nets.Where(n => !(w.Connected && n.Ssid == w.Ssid)).ToList();
                if (others.Count > 0)
                {
                    slot.Children.Add(Heading("Other Networks"));
                    var g = Group(slot);
                    foreach (var n in others) NetworkRow(g, n);
                }
                else Note(slot, w.Error ?? "No other networks nearby.");
            });
        }

        void NetworkRow(StackPanel g, Sys.WifiNet n)
        {
            var box = new StackPanel();
            var d = new DockPanel { Background = Brushes.Transparent, Cursor = Cursors.Hand };
            var info = Text((n.Secure ? "  " : "") + n.Signal + "%", 12, "FgDim"); info.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets, " + Theme.Font.Source);
            DockPanel.SetDock(info, Dock.Right);
            d.Children.Add(info); d.Children.Add(Text(n.Ssid + (n.Known ? "  ·  Known" : ""), 13, "Fg"));
            box.Children.Add(d);
            d.MouseLeftButtonUp += delegate
            {
                if (n.Known || !n.Secure) { OffUi(() => Sys.WifiConnect(n, null)); Refresh(4000); return; }
                if (box.Children.Count > 1) return;
                // A new secured network: ask for its password right in the row, like macOS.
                var pw = new PasswordBox { Width = 220, Height = 26, Padding = new Thickness(6, 3, 6, 3), BorderThickness = new Thickness(1), VerticalContentAlignment = VerticalAlignment.Center };
                pw.SetResourceReference(Control.BackgroundProperty, "Field"); pw.SetResourceReference(Control.ForegroundProperty, "Fg"); pw.SetResourceReference(Control.BorderBrushProperty, "MenuSep");
                var join = Pill("Join", () => { string s = pw.Password; OffUi(() => Sys.WifiConnect(n, s)); Refresh(5000); });
                var ask = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
                ask.Children.Add(Text("Password  ", 13, "FgDim")); ask.Children.Add(pw); ask.Children.Add(new Border { Width = 8 }); ask.Children.Add(join);
                box.Children.Add(ask);
                pw.Focus();
            };
            Row(g, box);
        }

        // ----- Bluetooth -----

        void BluetoothPane(StackPanel body)
        {
            Load(body, Sys.Bluetooth, (b, slot) =>
            {
                var top = Group(slot);
                InfoRow(top, "Bluetooth", !b.HasRadio ? "No Bluetooth adapter" : b.RadioOk ? "On" : "Off");
                if (b.Devices.Count == 0) return;
                slot.Children.Add(Heading("My Devices"));
                var g = Group(slot);
                foreach (var d in b.Devices) InfoRow(g, d, "Paired");
            });
        }

        // ----- Network -----

        void NetworkPane(StackPanel body)
        {
            var nics = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel && n.OperationalStatus == OperationalStatus.Up)
                .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 1 : 2).ToList();
            if (nics.Count == 0) { InfoRow(Group(body), "Network", "Not connected"); return; }
            foreach (var n in nics)
            {
                body.Children.Add(Heading(n.Name));
                var g = Group(body);
                var ip = n.GetIPProperties();
                InfoRow(g, "Status", "Connected · " + (n.Speed >= 1000000000 ? n.Speed / 1000000000.0 + " Gbps" : n.Speed / 1000000 + " Mbps"));
                InfoRow(g, "Type", n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : n.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? "Ethernet" : n.NetworkInterfaceType.ToString());
                var v4 = ip.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                if (v4 != null) InfoRow(g, "IP address", v4.Address.ToString());
                var gw = ip.GatewayAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                if (gw != null) InfoRow(g, "Router", gw.Address.ToString());
                var dns = ip.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToList();
                if (dns.Count > 0) InfoRow(g, "DNS servers", string.Join(", ", dns));
                var mac = n.GetPhysicalAddress().GetAddressBytes();
                if (mac.Length > 0) InfoRow(g, "MAC address", string.Join(":", mac.Select(x => x.ToString("X2"))));
            }
        }

        // ----- Notifications / Focus -----

        void Notifications(StackPanel body)
        {
            var top = Group(body);
            Toggle(top, "Allow notifications", Sys.Toasts, v => Sys.Toasts = v);
            Load(body, Sys.NotifyApps, (ids, slot) =>
            {
                var apps = ids.Select(id => new { Id = id, App = Apps.ById(id) }).Where(a => a.App != null).GroupBy(a => a.App.Name).Select(g => g.First()).OrderBy(a => a.App.Name).ToList();
                if (apps.Count == 0) return;
                slot.Children.Add(Heading("Application Notifications"));
                var g2 = Group(slot);
                foreach (var a in apps) { var id = a.Id; Toggle(g2, a.App.Name, Sys.NotifyOn(id), v => Sys.SetNotify(id, v)); }
            });
        }

        void Focus(StackPanel body)
        {
            var g = Group(body);
            Toggle(g, "Do Not Disturb", !Sys.Toasts, v => Sys.Toasts = !v);
            Note(body, "Do Not Disturb silences every notification banner and sound until you turn it off. Choose which apps can notify you in Notifications.");
        }

        // ----- Sound -----

        void Sound(StackPanel body)
        {
            Load(body, () => new { Out = Sys.AudioDevices(true), In = Sys.AudioDevices(false), Vol = Sys.GetVolume(true), Mute = Sys.GetMute(true), InVol = Sys.GetVolume(false) }, (s, slot) =>
            {
                var top = Group(slot);
                if (s.Vol >= 0) Level(top, "Output volume", 0, 100, s.Vol * 100, "", "", v => Sys.SetVolume(true, (float)(v / 100)));
                Toggle(top, "Mute", s.Mute, v => Sys.SetMute(true, v));
                if (s.Out.Count > 0)
                {
                    slot.Children.Add(Heading("Output"));
                    var g = Group(slot);
                    foreach (var d in s.Out) { var id = d.Id; Choice(g, d.Name, null, d.Default, () => { Sys.SetDefaultAudio(id); Refresh(300); }); }
                }
                if (s.In.Count > 0)
                {
                    slot.Children.Add(Heading("Input"));
                    var g = Group(slot);
                    if (s.InVol >= 0) Level(g, "Input volume", 0, 100, s.InVol * 100, null, null, v => Sys.SetVolume(false, (float)(v / 100)));
                    foreach (var d in s.In) { var id = d.Id; Choice(g, d.Name, null, d.Default, () => { Sys.SetDefaultAudio(id); Refresh(300); }); }
                }
            });
        }

        // ----- Accessibility -----

        void Accessibility(StackPanel body)
        {
            body.Children.Add(Heading("Display"));
            var g = Group(body);
            Toggle(g, "Reduce motion", Sys.ReduceMotion, v => Sys.ReduceMotion = v);
            Toggle(g, "Reduce transparency", Sys.ReduceTransparency, v => Sys.ReduceTransparency = v);
            body.Children.Add(Heading("Keyboard"));
            var k = Group(body);
            Toggle(k, "Sticky Keys", Sys.StickyKeys, v => Sys.StickyKeys = v);
            Toggle(k, "Underline keyboard shortcuts", Sys.KeyboardCues, v => Sys.KeyboardCues = v);
            Note(body, "Sticky Keys lets you press modifier keys like Shift and Ctrl one at a time instead of holding them.");
        }

        // ----- Displays -----

        void Displays(StackPanel body)
        {
            Load(body, Sys.Displays, (list, slot) =>
            {
                int n = 0;
                foreach (var d in list)
                {
                    var disp = d;
                    slot.Children.Add(Heading("Display " + ++n + (d.Primary ? " (Main)" : "")));
                    var g = Group(slot);
                    InfoRow(g, "Resolution", d.Width + " × " + d.Height);
                    InfoRow(g, "Refresh rate", d.Hz + " Hz");
                    if (d.Primary) InfoRow(g, "Scale", Math.Round(App.Dpi * 100) + "%");
                    if (d.Brightness >= 0) Level(g, "Brightness", 0, 100, d.Brightness, null, null, v => { int pct = (int)v; OffUi(() => Sys.SetBrightness(disp.Monitor, pct)); });
                    else InfoRow(g, "Brightness", "Adjust it on the display");
                }
            });
        }

        // ----- Wallpaper -----

        void WallpaperPane(StackPanel body)
        {
            string cur = Sys.WallpaperPath();
            cur = Sys.CopyOf(cur) ?? cur;   // the desktop may still point at the original of a picture the library keeps a copy of
            var preview = new Image { Height = 150, Stretch = Stretch.UniformToFill };
            var frame = new Border { Height = 150, Width = 260, CornerRadius = new CornerRadius(10), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left, Child = preview };
            frame.Clip = new RectangleGeometry(new Rect(0, 0, 260, 150), 10, 10);
            var top = Group(body);
            var head = new DockPanel();
            var buttons = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
            // Photos you add join the library (as a copy) and stay there until you remove them.
            buttons.Children.Add(Pill("Add Photo…", () =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp", Title = "Add a photo to your wallpapers" };
                if (dlg.ShowDialog(this) != true) return;
                try { Sys.SetWallpaper(Sys.AddToLibrary(dlg.FileName)); } catch (Exception ex) { App.Log(ex); }
                Refresh(300);
            }));
            // The desktop shows a picture that isn't in the library (set from another app): offer to keep it.
            if (File.Exists(cur) && !Sys.InLibrary(cur))
            {
                var keep = Pill("Add to Library", () => { try { Sys.AddToLibrary(cur); } catch (Exception ex) { App.Log(ex); } Refresh(100); });
                keep.Margin = new Thickness(0, 8, 0, 0);
                buttons.Children.Add(keep);
            }
            DockPanel.SetDock(buttons, Dock.Right);
            head.Children.Add(buttons); head.Children.Add(frame);
            Row(top, head);
            Thumb(cur, 520, img => preview.Source = img);

            var files = Sys.Library();
            body.Children.Add(Heading("Your Wallpapers"));
            var wrap = new WrapPanel();
            foreach (var f in files)
            {
                var file = f;
                var img = new Image { Width = 140, Height = 84, Stretch = Stretch.UniformToFill };
                img.Clip = new RectangleGeometry(new Rect(0, 0, 140, 84), 6, 6);
                // × in the corner (on hover) takes the picture out of the library.
                var remove = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 5, 0), Visibility = Visibility.Hidden, ToolTip = "Remove from Your Wallpapers",
                    Child = new TextBlock { Text = "", FontFamily = Theme.Glyphs, FontSize = 9, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                remove.MouseLeftButtonUp += (s, e) => { e.Handled = true; Sys.RemoveFromLibrary(file); Refresh(50); };
                var cell = new Grid();
                cell.Children.Add(img); cell.Children.Add(remove);
                var b = new Border { Width = 144, Height = 88, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(2), Margin = new Thickness(0, 0, 10, 10), Cursor = Cursors.Hand, Child = cell };
                if (string.Equals(f, cur, StringComparison.OrdinalIgnoreCase)) b.SetResourceReference(Border.BorderBrushProperty, "Accent"); else b.BorderBrush = Brushes.Transparent;
                b.ToolTip = Path.GetFileNameWithoutExtension(f);
                b.MouseEnter += delegate { remove.Visibility = Visibility.Visible; };
                b.MouseLeave += delegate { remove.Visibility = Visibility.Hidden; };
                b.MouseLeftButtonUp += delegate { Sys.SetWallpaper(file); Refresh(300); };
                Thumb(f, 280, s => img.Source = s);
                wrap.Children.Add(b);
            }
            body.Children.Add(wrap);
            if (files.Count == 0) Note(body, "Your library is empty. Add photos with Add Photo….");
        }

        static void Thumb(string file, int width, Action<ImageSource> done)
        {
            if (!File.Exists(file)) return;
            var ui = Dispatcher.CurrentDispatcher;
            Task.Run(() =>
            {
                try
                {
                    var bi = new BitmapImage();
                    bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.DecodePixelWidth = width; bi.UriSource = new Uri(file); bi.EndInit();
                    bi.Freeze();
                    ui.BeginInvoke(new Action(() => done(bi)));
                }
                catch { }
            });
        }

        // ----- Battery -----

        void Battery(StackPanel body)
        {
            Native.SYSTEM_POWER_STATUS ps;
            bool has = Native.GetSystemPowerStatus(out ps) && ps.BatteryFlag != 128 && ps.BatteryFlag != 255 && ps.BatteryLifePercent <= 100;
            var top = Group(body);
            if (has) { InfoRow(top, "Battery level", ps.BatteryLifePercent + "%"); InfoRow(top, "Power source", ps.ACLineStatus == 1 ? "Power adapter" : "Battery"); }
            else InfoRow(top, "Power source", "Power adapter (this PC has no battery)");
            Load(body, Sys.Plans, (plans, slot) =>
            {
                if (plans.Count == 0) return;
                slot.Children.Add(Heading("Power Mode"));
                var g = Group(slot);
                foreach (var p in plans) { var guid = p.Guid; Choice(g, p.Name, null, p.Active, () => { OffUi(() => Sys.SetPlan(guid)); Refresh(800); }); }
            });
        }

        // ----- Lock Screen -----

        static readonly string[] Waits = { "Never", "1 minute", "2 minutes", "5 minutes", "10 minutes", "15 minutes", "30 minutes", "1 hour", "2 hours", "5 hours" };
        static readonly int[] WaitMinutes = { 0, 1, 2, 5, 10, 15, 30, 60, 120, 300 };

        static int Nearest(int minutes)
        {
            if (minutes < 0) return 0;
            int best = 0;
            for (int i = 0; i < WaitMinutes.Length; i++) if (Math.Abs(WaitMinutes[i] - minutes) < Math.Abs(WaitMinutes[best] - minutes)) best = i;
            return best;
        }

        void LockScreen(StackPanel body)
        {
            Load(body, () => new[] { Sys.Timeout("SUB_VIDEO", "VIDEOIDLE"), Sys.Timeout("SUB_SLEEP", "STANDBYIDLE") }, (t, slot) =>
            {
                var g = Group(slot);
                Steps(g, "Turn display off after", Waits, Nearest(t[0]), i => Sys.SetTimeout("monitor", WaitMinutes[i]));
                Steps(g, "Put the computer to sleep after", Waits, Nearest(t[1]), i => Sys.SetTimeout("standby", WaitMinutes[i]));
                slot.Children.Add(new Border { Height = 14 });
                var now = Group(slot);
                var d = new DockPanel();
                var pill = Pill("Lock Screen", () => Native.LockWorkStation());
                DockPanel.SetDock(pill, Dock.Right);
                d.Children.Add(pill); d.Children.Add(Text("Lock this PC now", 13, "Fg"));
                Row(now, d);
            });
        }

        // ----- Privacy & Security -----

        void Privacy(StackPanel body)
        {
            body.Children.Add(Heading("Privacy"));
            var g = Group(body);
            Toggle(g, "Camera", Sys.Allowed("webcam"), v => Sys.SetAllowed("webcam", v));
            Toggle(g, "Microphone", Sys.Allowed("microphone"), v => Sys.SetAllowed("microphone", v));
            Toggle(g, "Location Services", Sys.Allowed("location"), v => Sys.SetAllowed("location", v));
            Note(body, "When one of these is off, no app can use it.");
            Load(body, Sys.Antivirus, (av, slot) =>
            {
                if (av.Count == 0) return;
                slot.Children.Add(Heading("Security"));
                var s = Group(slot);
                foreach (var a in av) InfoRow(s, a[0], a[1]);
            });
        }

        // ----- Users & Groups -----

        void Users(StackPanel body)
        {
            Load(body, Sys.Users, (users, slot) =>
            {
                var g = Group(slot);
                string me = Environment.UserName;
                foreach (var u in users.OrderByDescending(u => string.Equals(u[2], me, StringComparison.OrdinalIgnoreCase)))
                    InfoRow(g, u[0] + (string.Equals(u[2], me, StringComparison.OrdinalIgnoreCase) ? "  (you)" : ""), u[1]);
            });
        }

        // ----- Keyboard / Mouse -----

        void KeyboardPane(StackPanel body)
        {
            var g = Group(body);
            Level(g, "Key repeat rate", 0, 31, Sys.KeyRepeat, "Slow", "Fast", v => Sys.KeyRepeat = (int)Math.Round(v));
            Level(g, "Delay until repeat", 0, 3, 3 - Sys.KeyDelay, "Long", "Short", v => Sys.KeyDelay = 3 - (int)Math.Round(v));
            body.Children.Add(Heading("Input Sources"));
            var langs = Group(body);
            foreach (CultureInfo c in InputLanguageManager.Current.AvailableInputLanguages) InfoRow(langs, c.DisplayName, c.Name);
            Note(body, "Switch input sources with Win+Space.");
        }

        void MousePane(StackPanel body)
        {
            var g = Group(body);
            Level(g, "Tracking speed", 1, 20, Sys.MouseSpeed, "Slow", "Fast", v => Sys.MouseSpeed = (int)Math.Round(v));
            Level(g, "Scrolling speed", 1, 15, Math.Min(15, Sys.ScrollLines), "Slow", "Fast", v => Sys.ScrollLines = (int)Math.Round(v));
            Level(g, "Double-click speed", 0, 100, (900 - Sys.DoubleClick) / 7.0, "Slow", "Fast", v => Sys.DoubleClick = (int)(900 - v * 7));
            Toggle(g, "Swap left and right buttons", Sys.SwapButtons, v => Sys.SwapButtons = v);
        }

        // ----- Printers -----

        void Printers(StackPanel body)
        {
            Load(body, Sys.Printers, (list, slot) =>
            {
                var g = Group(slot);
                if (list.Count == 0) { InfoRow(g, "Printers", "None"); return; }
                foreach (var p in list) { var name = p[0]; Choice(g, name, p[2] == "True" ? "Offline" : p[1] == "True" ? "Default" : null, p[1] == "True", () => { OffUi(() => Sys.SetDefaultPrinter(name)); Refresh(1200); }); }
                Note(slot, "Click a printer to make it the default.");
            });
        }

        // ----- General sub-panes -----

        void AboutPane(StackPanel body)
        {
            var art = new Border { Width = 96, Height = 96, CornerRadius = new CornerRadius(22), Margin = new Thickness(0, 12, 0, 8), HorizontalAlignment = HorizontalAlignment.Center,
                Background = new LinearGradientBrush(Hex("#5AC8FA"), Hex("#0A84FF"), 90) };
            art.Child = new TextBlock { Text = "", FontFamily = Theme.Glyphs, FontSize = 52, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            body.Children.Add(art);
            Load(body, Sys.About, (i, slot) =>
            {
                var name = Text(i.Name, 22, "Fg", FontWeights.Bold); name.HorizontalAlignment = HorizontalAlignment.Center;
                var model = Text(i.Model, 13, "FgDim"); model.HorizontalAlignment = HorizontalAlignment.Center; model.Margin = new Thickness(0, 2, 0, 12);
                slot.Children.Add(name); slot.Children.Add(model);
                var g = Group(slot);
                InfoRow(g, "Name", i.Name);
                InfoRow(g, "Chip", i.Chip);
                InfoRow(g, "Memory", i.Memory);
                InfoRow(g, "Graphics", i.Graphics);
                InfoRow(g, "Serial number", i.Serial);
                InfoRow(g, "Windows", i.Windows);
                slot.Children.Add(Heading("Storage"));
                var s = Group(slot);
                NavLink(s, "System disk · " + i.Disk, null, null, "Storage");
            });
        }

        void Storage(StackPanel body)
        {
            foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            {
                body.Children.Add(Heading((d.VolumeLabel.Length > 0 ? d.VolumeLabel : "Local Disk") + " (" + d.Name.TrimEnd('\\') + ")"));
                var g = Group(body);
                var box = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
                box.Children.Add(Text(Sys.Size(d.AvailableFreeSpace) + " available of " + Sys.Size(d.TotalSize), 13, "Fg"));
                double used = 1 - d.AvailableFreeSpace / (double)d.TotalSize;
                var bar = new Grid { Height = 10, Margin = new Thickness(0, 8, 0, 2) };
                var track = new Border { CornerRadius = new CornerRadius(5) }; track.SetResourceReference(Border.BackgroundProperty, "Track");
                var fill = new Border { CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Left }; fill.SetResourceReference(Border.BackgroundProperty, "Accent");
                bar.SizeChanged += delegate { fill.Width = Math.Max(10, bar.ActualWidth * used); };
                bar.Children.Add(track); bar.Children.Add(fill);
                box.Children.Add(bar);
                Row(g, box);
            }
        }

        void LoginItems(StackPanel body)
        {
            var g = Group(body);
            Toggle(g, "Open OpenDock at login", Settings.LaunchAtLogin, v => Settings.LaunchAtLogin = v);
            Load(body, Sys.StartupItems, (items, slot) =>
            {
                if (items.Count == 0) return;
                slot.Children.Add(Heading("Open at Login"));
                var l = Group(slot);
                foreach (var it in items) InfoRow(l, it[0], it[1]);
            });
        }

        void DateTimePane(StackPanel body)
        {
            var g = Group(body);
            var now = DateTime.Now;
            InfoRow(g, "Date", now.ToString("D"));
            InfoRow(g, "Time", now.ToString("t"));
            InfoRow(g, "Time zone", TimeZoneInfo.Local.DisplayName);
            Note(body, "Windows sets the date and time automatically from the internet.");
        }
    }

    // "About This PC": a copy of macOS Sequoia's About This Mac, laid out from measurements of Apple's own screenshot.
    // A solid 280×499 window: the device, its name, five rows, More Info… and the small print.
    class AboutWindow : MacWindow
    {
        static AboutWindow open;

        public static void ShowIt()
        {
            if (open != null && open.IsLoaded)
            {
                if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
                open.Activate();
                return;
            }
            open = new AboutWindow();
            open.Show();
        }

        const double Scale = 1.25;
        readonly Canvas page = new Canvas { Width = 280, Height = 499 };
        readonly bool dark = Theme.Dark;

        static Color Hx(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }
        Brush C(string light, string darkHex) { return new SolidColorBrush(Hx(dark ? darkHex : light)); }

        AboutWindow() : base(false)
        {
            Title = "About This PC";
            Width = 280 * Scale; Height = 499 * Scale; ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = C("#E0E0E0", "#2B2B2B");
            page.LayoutTransform = new ScaleTransform(Scale, Scale);   // Apple's layout, drawn a quarter bigger
            Layout.Children.Add(page);
            Lights();
            Device();

            var more = new Border { Width = 88, Height = 20, CornerRadius = new CornerRadius(5), Background = C("#C6C6C6", "#5C5C5C"), Cursor = Cursors.Hand };
            var label = Label("More Info…", 13, C("#3B3B3B", "#E6E6E6"), FontWeights.Normal);
            label.HorizontalAlignment = HorizontalAlignment.Center; label.VerticalAlignment = VerticalAlignment.Center; label.Margin = new Thickness(0, -4, 0, 0);
            more.Child = label;
            more.MouseLeftButtonUp += delegate { App.OpenSettings("About"); };
            WindowChrome.SetIsHitTestVisibleInChrome(more, true);
            Canvas.SetLeft(more, 96); Canvas.SetTop(more, 392);
            page.Children.Add(more);

            var small = C("#A0A0A0", "#8A8A8A");
            var license = Centered("Software License Agreement", 10, small, FontWeights.Normal, 428.5);
            license.TextDecorations = TextDecorations.Underline; license.Cursor = Cursors.Hand;
            WindowChrome.SetIsHitTestVisibleInChrome(license, true);
            license.MouseLeftButtonUp += delegate { var f = Path.Combine(Environment.SystemDirectory, "license.rtf"); if (File.Exists(f)) Launcher.Shell(f); };
            Centered("™ and © 1985-" + DateTime.Now.Year + " Microsoft Corporation.", 10, small, FontWeights.Normal, 443.5);
            Centered("All Rights Reserved.", 10, small, FontWeights.Normal, 457.5);

            Task.Run(() => Sys.About()).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (t.IsFaulted) return;
                var i = t.Result;
                Centered(i.Title, 22, C("#444444", "#DFDFDF"), FontWeights.Bold, 237);
                Centered(i.Subtitle, 11, small, FontWeights.Normal, 263.5);
                string[][] rows = { new[] { "Chip", i.ChipShort }, new[] { "Memory", i.Memory }, new[] { "Startup disk", i.Volume }, new[] { "Serial number", i.Serial }, new[] { "Windows", i.WindowsShort } };
                for (int r = 0; r < rows.Length; r++)
                {
                    double cap = 297.5 + 16 * r;
                    var k = Label(rows[r][0], 11, C("#444444", "#DFDFDF"), FontWeights.Normal);
                    k.Width = 122; k.TextAlignment = TextAlignment.Right;
                    Put(k, 0, cap);
                    var v = Label(rows[r][1], 11, C("#616161", "#AFAFAF"), FontWeights.Normal);
                    v.Width = 140; v.TextTrimming = TextTrimming.CharacterEllipsis; v.ToolTip = rows[r][1];
                    Put(v, 133, cap);
                }
            })));
        }

        static TextBlock Label(string s, double size, Brush fg, FontWeight w) { return new TextBlock { Text = s ?? "", FontSize = size, Foreground = fg, FontWeight = w, FontFamily = Theme.Font }; }

        // Places text so the top of its capital letters sits at capTop (that's how the screenshot was measured).
        void Put(TextBlock t, double left, double capTop)
        {
            var face = new Typeface(t.FontFamily, t.FontStyle, t.FontWeight, t.FontStretch);
            Canvas.SetLeft(t, left);
            Canvas.SetTop(t, capTop - (t.FontFamily.Baseline - face.CapsHeight) * t.FontSize);
            page.Children.Add(t);
        }

        TextBlock Centered(string s, double size, Brush fg, FontWeight w, double capTop)
        {
            var t = Label(s, size, fg, w);
            t.Width = 260; t.TextAlignment = TextAlignment.Center; t.TextTrimming = TextTrimming.CharacterEllipsis;
            Put(t, 10, capTop);
            return t;
        }

        // Only close works in About This Mac; minimize and zoom are hollow, disabled circles.
        void Lights()
        {
            var close = new Grid { Width = 12, Height = 12, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            close.Children.Add(new Ellipse { Fill = new SolidColorBrush(Hx("#FF5F57")), Stroke = new SolidColorBrush(Hx("#E2463F")), StrokeThickness = 0.5 });
            var x = new TextBlock { Text = "×", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromArgb(160, 60, 0, 0)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0), Opacity = 0, IsHitTestVisible = false };
            close.Children.Add(x);
            close.MouseEnter += delegate { x.Opacity = 1; };
            close.MouseLeave += delegate { x.Opacity = 0; };
            close.MouseLeftButtonUp += delegate { Close(); };
            WindowChrome.SetIsHitTestVisibleInChrome(close, true);
            Canvas.SetLeft(close, 8); Canvas.SetTop(close, 8);
            page.Children.Add(close);
            foreach (double left in new[] { 28.0, 48.0 })
            {
                var ring = new Ellipse { Width = 12, Height = 12, Stroke = C("#BDBDBD", "#5A5A5A"), StrokeThickness = 1 };
                Canvas.SetLeft(ring, left); Canvas.SetTop(ring, 8);
                page.Children.Add(ring);
            }
        }

        // The device picture (Apple's sits in a 171×103 box at the top): this PC's screen in its real shape, showing
        // your wallpaper, on a stand — or a laptop if the PC has a battery.
        void Device()
        {
            Native.SYSTEM_POWER_STATUS ps;
            bool laptop = Native.GetSystemPowerStatus(out ps) && ps.BatteryFlag != 128 && ps.BatteryFlag != 255;
            double aspect = SystemParameters.PrimaryScreenWidth / SystemParameters.PrimaryScreenHeight;
            Brush screen = new LinearGradientBrush(Hx("#5AC8FA"), Hx("#0A84FF"), 90);
            string path = Sys.WallpaperPath();
            if (File.Exists(path))
                try
                {
                    var bi = new BitmapImage();
                    bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.DecodePixelWidth = 480; bi.UriSource = new Uri(path); bi.EndInit();
                    screen = new ImageBrush(bi) { Stretch = Stretch.UniformToFill };
                }
                catch { }
            const double top = 80, bottom = 183, cx = 140;
            var silver = new LinearGradientBrush(Hx("#E4E4E7"), Hx("#A5A5AA"), 90);
            var rim = new SolidColorBrush(Hx("#8E8E93"));

            var shadow = new Ellipse { Width = 130, Height = 8, Fill = new RadialGradientBrush(Color.FromArgb(70, 0, 0, 0), Color.FromArgb(0, 0, 0, 0)) };
            Canvas.SetLeft(shadow, cx - 65); Canvas.SetTop(shadow, bottom - 4);
            page.Children.Add(shadow);
            if (laptop)
            {
                double w = 150, h = Math.Min(top + 96 - top, w / aspect + 10);
                var lid = new Border { Width = w, Height = h, CornerRadius = new CornerRadius(6, 6, 1, 1), Background = new SolidColorBrush(Hx("#111111")), BorderBrush = rim, BorderThickness = new Thickness(0.75), Padding = new Thickness(4, 4, 4, 6),
                    Child = new Border { CornerRadius = new CornerRadius(2), Background = screen } };
                Canvas.SetLeft(lid, cx - w / 2); Canvas.SetTop(lid, bottom - 7 - h);
                var deck = new Border { Width = 171, Height = 6, CornerRadius = new CornerRadius(0, 0, 4, 4), Background = silver };
                Canvas.SetLeft(deck, cx - 85.5); Canvas.SetTop(deck, bottom - 7);
                var notch = new Border { Width = 26, Height = 2.5, CornerRadius = new CornerRadius(0, 0, 2, 2), Background = rim };
                Canvas.SetLeft(notch, cx - 13); Canvas.SetTop(notch, bottom - 7);
                page.Children.Add(lid); page.Children.Add(deck); page.Children.Add(notch);
            }
            else
            {
                const double neckH = 18, baseH = 6;
                double maxH = bottom - top - neckH - baseH, w = Math.Min(171, maxH * aspect), h = w / aspect, monTop = bottom - baseH - neckH - h;
                var neck = new Border { Width = 20, Height = neckH, Background = new LinearGradientBrush(Hx("#B9B9BE"), Hx("#E6E6E9"), 0) };
                Canvas.SetLeft(neck, cx - 10); Canvas.SetTop(neck, monTop + h);
                var foot = new Border { Width = 62, Height = baseH, CornerRadius = new CornerRadius(3), Background = silver };
                Canvas.SetLeft(foot, cx - 31); Canvas.SetTop(foot, bottom - baseH);
                var mon = new Border { Width = w, Height = h, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Hx("#121212")), BorderBrush = rim, BorderThickness = new Thickness(0.75), Padding = new Thickness(3),
                    Child = new Border { CornerRadius = new CornerRadius(1.5), Background = screen } };
                Canvas.SetLeft(mon, cx - w / 2); Canvas.SetTop(mon, monTop);
                page.Children.Add(neck); page.Children.Add(foot); page.Children.Add(mon);
            }
        }
    }
}
