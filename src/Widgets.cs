using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace OpenDock
{
    // macOS desktop widgets: Liquid Glass cards on the wallpaper, behind every window -- a clock, this month,
    // the weather, now playing, the battery and a note. Drag one to move it; right-click to remove it.
    static class Widgets
    {
        public static readonly string[] Kinds = { "clock", "calendar", "weather", "music", "battery", "note" };
        public static readonly string[] Names = { "Clock", "Calendar", "Weather", "Now Playing", "Battery", "Notes" };
        public const double S = 164, Gap = 16, M = 2 * S + Gap;   // small and medium sizes, as on macOS
        static readonly Dictionary<string, WidgetWindow> open = new Dictionary<string, WidgetWindow>();
        static DispatcherTimer tick;
        static string shownTheme;

        public static bool HasBattery
        {
            get { Native.SYSTEM_POWER_STATUS ps; return Native.GetSystemPowerStatus(out ps) && ps.BatteryFlag != 128 && ps.BatteryFlag != 255 && ps.BatteryLifePercent <= 100; }
        }

        public static List<string> List
        {
            get
            {
                if (Settings.WidgetList == "") return HasBattery ? new List<string> { "clock", "calendar", "weather", "battery", "music" } : new List<string> { "clock", "calendar", "weather", "music" };
                return Settings.WidgetList.Split(',').Where(k => Kinds.Contains(k)).Distinct().ToList();
            }
        }

        public static void Set(string kind, bool on)
        {
            var l = List;
            if (on && !l.Contains(kind)) l.Add(kind);
            if (!on) l.Remove(kind);
            Settings.WidgetList = l.Count == 0 ? "none" : string.Join(",", l);
        }

        public static void Start()
        {
            tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            tick.Tick += delegate { foreach (var w in open.Values) w.Tick(); };
            tick.Start();
            Settings.Changed += Sync;
            Wallpaper.Changed += delegate { foreach (var w in open.Values) w.Paint(); };
            Theme.Changed += Rebuild;
            NowPlaying.Changed += delegate { WidgetWindow w; if (open.TryGetValue("music", out w)) w.Build(); };
            Weather.Changed += delegate { WidgetWindow w; if (open.TryGetValue("weather", out w)) w.Build(); };
            NowPlaying.Start();
            Weather.Start();
            Sync();
        }

        static void Rebuild() { foreach (var w in open.Values) { w.Build(); w.Paint(); } }

        public static void Sync()
        {
            var want = Settings.Widgets ? List : new List<string>();
            foreach (var k in open.Keys.Where(k => !want.Contains(k)).ToList()) { open[k].Close(); open.Remove(k); }
            foreach (var k in want.Where(k => !open.ContainsKey(k)))
            {
                var w = new WidgetWindow(k);
                open[k] = w;
                w.Place(Spot(k));
                w.Show();
            }
            foreach (var w in open.Values) w.Exclude();
            string theme = Theme.Dark + "|" + Settings.GlassTint + "|" + (IconLook.OneColor ? IconLook.Accent.ToString() : "");
            if (theme != shownTheme) { shownTheme = theme; foreach (var w in open.Values) { w.Paint(); w.Build(); } }
        }

        public static Size SizeOf(string kind) { return kind == "weather" || kind == "music" || kind == "note" ? new Size(M, S) : new Size(S, S); }

        // ---------- where they sit ----------

        static Rect Area { get { return DisplayLayout.WorkArea; } }

        static Dictionary<string, Point> Saved()
        {
            var d = new Dictionary<string, Point>();
            foreach (var part in (Settings.WidgetPos ?? "").Split(';'))
            {
                var kv = part.Split(':');
                if (kv.Length != 2) continue;
                var xy = kv[1].Split(',');
                double x, y;
                if (xy.Length == 2 && double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x) && double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)) d[kv[0]] = new Point(x, y);
            }
            return d;
        }

        // Saved spots are kept relative to the work area's top-right corner (Windows' desktop icons start at the
        // left, so widgets go on the right). New ones fill a two-column grid down the right side.
        static Point Spot(string kind)
        {
            Point p;
            var a = Area;
            if (Saved().TryGetValue(kind, out p)) return Fit(new Point(a.Right - p.X, a.Top + p.Y), SizeOf(kind));
            double x = a.Right - Gap - M, y = a.Top + Gap, rowH = 0, col = 0;
            foreach (var k in List)
            {
                var sz = SizeOf(k);
                if (col + sz.Width > M + 0.5) { y += rowH + Gap; col = 0; rowH = 0; }
                if (k == kind) return new Point(x + col, y);
                col += sz.Width + Gap; rowH = Math.Max(rowH, sz.Height);
            }
            return new Point(x, y);
        }

        static Point Fit(Point p, Size s)
        {
            var a = Area;
            return new Point(Math.Max(a.Left, Math.Min(a.Right - s.Width, p.X)), Math.Max(a.Top, Math.Min(a.Bottom - s.Height, p.Y)));
        }

        // After a drag: on an 8 px grid, inside the screen, remembered.
        public static Point Moved(string kind, Point at)
        {
            var a = Area;
            var p = Fit(new Point(a.Left + Math.Round((at.X - a.Left) / 8) * 8, a.Top + Math.Round((at.Y - a.Top) / 8) * 8), SizeOf(kind));
            var saved = Saved();
            saved[kind] = new Point(a.Right - p.X, p.Y - a.Top);
            Settings.WidgetPos = string.Join(";", saved.Select(kv => kv.Key + ":" + kv.Value.X.ToString(CultureInfo.InvariantCulture) + "," + kv.Value.Y.ToString(CultureInfo.InvariantCulture)));
            Settings.Save();
            return p;
        }

        public static void ResetPositions()
        {
            Settings.WidgetPos = "";
            Settings.Save();
            foreach (var kv in open) kv.Value.Place(Spot(kv.Key));
        }
    }

    // One widget on the desktop. It stays just above the desktop itself: over the wallpaper, under every window.
    class WidgetWindow : Window
    {
        [StructLayout(LayoutKind.Sequential)] struct WINDOWPOS { public IntPtr hwnd, after; public int x, y, cx, cy; public uint flags; }

        public readonly string Kind;
        const double Pad = 12;   // room for the shadow
        readonly Border card = new Border { CornerRadius = new CornerRadius(22), BorderThickness = new Thickness(1), ClipToBounds = false };
        readonly Grid body = new Grid { ClipToBounds = true };
        readonly Border sheen = new Border { CornerRadius = new CornerRadius(21), IsHitTestVisible = false };
        Brush fg, dim;
        IntPtr hwnd;
        int lastMinute = -1, lastDay = -1, ticks;
        Line hourHand, minuteHand, secondHand;
        TextBox noteBox;
        DispatcherTimer noteSave;

        public WidgetWindow(string kind)
        {
            Kind = kind;
            Title = "GlassDock Widget";
            var size = Widgets.SizeOf(kind);
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false; ShowActivated = false; FontFamily = Theme.Font;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = size.Width + 2 * Pad; Height = size.Height + 2 * Pad;
            card.Margin = new Thickness(Pad);
            card.Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.25 };
            body.Clip = new RectangleGeometry(new Rect(0, 0, size.Width - 2, size.Height - 2), 21, 21);
            var layers = new Grid();
            layers.Children.Add(sheen); layers.Children.Add(body);
            card.Child = layers;
            Content = card;
            Build();
            SourceInitialized += delegate
            {
                hwnd = new WindowInteropHelper(this).Handle;
                Native.MakeToolWindow(hwnd, kind != "note");   // the note takes typing, the rest never take focus
                Exclude();
                HwndSource.FromHwnd(hwnd).AddHook(Hook);
                Sink();
            };
            card.MouseLeftButtonDown += (s, e) => Drag(e);
            card.MouseRightButtonUp += (s, e) => { e.Handled = true; Menu(); };
            LocationChanged += delegate { Paint(); };
            Closed += delegate { if (noteSave != null && noteSave.IsEnabled) { noteSave.Stop(); SaveNote(); } };
        }

        // ---------- staying on the desktop ----------

        static IntPtr desktop;
        static IntPtr Desktop()
        {
            if (desktop != IntPtr.Zero && Native.IsWindow(desktop)) return desktop;
            desktop = IntPtr.Zero;
            Native.EnumWindows((h, l) => { if (Native.FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) { desktop = h; return false; } return true; }, IntPtr.Zero);
            return desktop;
        }

        // Any time Windows would raise it (a click, a drag), it goes straight back to just above the desktop.
        IntPtr Hook(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == 0x0046 /* WM_WINDOWPOSCHANGING */)
            {
                var pos = (WINDOWPOS)Marshal.PtrToStructure(l, typeof(WINDOWPOS));
                if ((pos.flags & 0x4 /* NOZORDER */) == 0 || (pos.flags & 0x40 /* SHOWWINDOW */) != 0)
                {
                    var d = Desktop();
                    var above = d == IntPtr.Zero ? (IntPtr)1 /* HWND_BOTTOM */ : Native.GetWindow(d, 3 /* GW_HWNDPREV */);
                    if (above == hwnd) pos.flags |= 0x4;
                    else { pos.after = above == IntPtr.Zero ? (IntPtr)1 : above; pos.flags &= ~0x4u; }
                    Marshal.StructureToPtr(pos, l, false);
                }
            }
            return IntPtr.Zero;
        }

        void Sink()
        {
            var d = Desktop();
            var above = d == IntPtr.Zero ? (IntPtr)1 : Native.GetWindow(d, 3);
            if (above != hwnd) Native.SetWindowPos(hwnd, above == IntPtr.Zero ? (IntPtr)1 : above, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        public void Place(Point p) { Left = p.X - Pad; Top = p.Y - Pad; Paint(); }

        // Control Center, Notification Center and banners open right over the widgets and build their liquid glass
        // from what's behind them: they should see the wallpaper there, not milky widget cards.
        public void Exclude() { if (hwnd != IntPtr.Zero) Glass.Exclude(hwnd); }

        // Drag to move; a click without moving opens the widget's app.
        void Drag(MouseButtonEventArgs e)
        {
            if (e.Handled) return;
            var start = new Point(Left, Top);
            try { DragMove(); } catch (InvalidOperationException) { return; }
            if (Math.Abs(Left - start.X) < 3 && Math.Abs(Top - start.Y) < 3) { Left = start.X; Top = start.Y; Open(); return; }
            Place(Widgets.Moved(Kind, new Point(Left + Pad, Top + Pad)));
            Sink();
        }

        void Open()
        {
            switch (Kind)
            {
                case "clock": Launcher.Shell("ms-clock:"); break;
                case "calendar": ClockPanel.Toggle(null); break;
                case "weather": if (Weather.Place == null) App.OpenSettings("Desktop & Dock"); else Launcher.Shell("msnweather://forecast"); break;
                case "battery": App.OpenSettings("Battery"); break;
            }
        }

        void Menu()
        {
            var cm = new ContextMenu { PlacementTarget = card, Placement = PlacementMode.MousePoint };
            var remove = new MenuItem { Header = "Remove Widget" };
            remove.Click += delegate { Widgets.Set(Kind, false); Settings.Apply(); };
            cm.Items.Add(remove);
            cm.Items.Add(new Separator());
            var edit = new MenuItem { Header = "Edit Widgets…" };
            edit.Click += delegate { App.OpenSettings("Desktop & Dock"); };
            cm.Items.Add(edit);
            cm.IsOpen = true;
        }

        // ---------- look ----------

        // Frosted from the wallpaper right behind it (no screen capture needed: it sits on the wallpaper).
        public void Paint()
        {
            bool dark = Theme.Dark;
            var at = new Rect(Left + Pad, Top + Pad, Math.Max(1, Width - 2 * Pad), Math.Max(1, Height - 2 * Pad));
            // One color everywhere: the glass takes the icon colour, like Tinted widgets on macOS.
            var a = IconLook.Accent;
            card.Background = Glass.WallpaperPane(IconLook.OneColor
                ? Theme.Tint(string.Format("#{0:X2}{1:X2}{2:X2}{3:X2}", dark ? 0xA6 : 0x99, (int)(a.R * (dark ? 0.55 : 0.92)), (int)(a.G * (dark ? 0.55 : 0.92)), (int)(a.B * (dark ? 0.55 : 0.92))))
                : Theme.Tint(dark ? "#8C1C1C1E" : "#8CF5F5F7"), at);
            sheen.Background = new LinearGradientBrush(new GradientStopCollection { new GradientStop(Color.FromArgb(dark ? (byte)0x26 : (byte)0x66, 255, 255, 255), 0),
                new GradientStop(Color.FromArgb(0x08, 255, 255, 255), 0.45), new GradientStop(Color.FromArgb(0, 255, 255, 255), 1) }, new Point(0, 0), new Point(0, 1));
            card.BorderBrush = new LinearGradientBrush(new GradientStopCollection {
                new GradientStop(Color.FromArgb(dark ? (byte)0x8C : (byte)0xF2, 255, 255, 255), 0), new GradientStop(Color.FromArgb(0x14, 255, 255, 255), 0.5),
                new GradientStop(Color.FromArgb(dark ? (byte)0x55 : (byte)0xB3, 255, 255, 255), 1) }, new Point(0, 0), new Point(1, 1));
        }

        public void Build()
        {
            fg = Theme.Dark || IconLook.OneColor ? Brushes.White : Theme.B("#E6000000");
            dim = Theme.Dark || IconLook.OneColor ? Theme.B("#B3FFFFFF") : Theme.B("#8C000000");
            body.Children.Clear();
            hourHand = minuteHand = secondHand = null;
            FrameworkElement c;
            switch (Kind)
            {
                case "clock": c = Clock(); break;
                case "calendar": c = Calendar(); break;
                case "weather": c = WeatherView(); break;
                case "music": c = Music(); break;
                case "battery": c = Battery(); break;
                default: c = Note(); break;
            }
            body.Children.Add(c);
            lastMinute = -1;
            Tick();
        }

        public void Tick()
        {
            var now = DateTime.Now;
            if (Kind == "clock" && hourHand != null)
            {
                double s = now.Second, m = now.Minute + s / 60, h = now.Hour % 12 + m / 60;
                Turn(secondHand, s * 6); Turn(minuteHand, m * 6); Turn(hourHand, h * 30);
            }
            if (Kind == "calendar" && now.Day != lastDay && lastDay != -1) { lastDay = now.Day; Build(); return; }
            lastDay = now.Day;
            if (Kind == "battery" && ++ticks % 30 == 0) Build();
            lastMinute = now.Minute;
        }

        static void Turn(Line hand, double deg) { ((RotateTransform)hand.RenderTransform).Angle = deg; }

        TextBlock T(string text, double size, FontWeight weight, Brush brush)
        {
            return new TextBlock { Text = text ?? "", FontSize = size, FontWeight = weight, Foreground = brush, TextTrimming = TextTrimming.CharacterEllipsis };
        }

        // ---------- the widgets ----------

        FrameworkElement Clock()
        {
            double size = Widgets.S - 20, c = size / 2;
            var face = new Canvas { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            bool dark = Theme.Dark;
            bool mono = IconLook.OneColor;   // one color everywhere: a see-through dial, everything in white
            var dial = new Ellipse { Width = size, Height = size, Fill = mono ? Theme.B("#1FFFFFFF") : dark ? Theme.B("#FF1C1C1E") : Brushes.White, Stroke = dark || mono ? Theme.B("#33FFFFFF") : Theme.B("#14000000"), StrokeThickness = 1 };
            face.Children.Add(dial);
            for (int i = 1; i <= 12; i++)
            {
                double a = i * Math.PI / 6, r = c - 15;
                var n = T(i.ToString(), 12.5, FontWeights.SemiBold, fg);
                n.Measure(new Size(40, 40));
                Canvas.SetLeft(n, c + r * Math.Sin(a) - n.DesiredSize.Width / 2);
                Canvas.SetTop(n, c - r * Math.Cos(a) - n.DesiredSize.Height / 2);
                face.Children.Add(n);
            }
            for (int i = 0; i < 60; i++)
            {
                if (i % 5 == 0) continue;
                double a = i * Math.PI / 30, r1 = c - 4, r2 = c - 6.5;
                face.Children.Add(new Line { X1 = c + r1 * Math.Sin(a), Y1 = c - r1 * Math.Cos(a), X2 = c + r2 * Math.Sin(a), Y2 = c - r2 * Math.Cos(a), Stroke = dim, StrokeThickness = 0.8 });
            }
            hourHand = Hand(c, c * 0.48, 4.2, fg);
            minuteHand = Hand(c, c * 0.74, 3.2, fg);
            Brush hand2 = mono ? (Brush)Brushes.White : new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A));
            secondHand = Hand(c, c * 0.82, 1.3, hand2);
            face.Children.Add(hourHand); face.Children.Add(minuteHand); face.Children.Add(secondHand);
            var pin = new Ellipse { Width = 7, Height = 7, Fill = hand2, Stroke = fg, StrokeThickness = 1.5 };
            Canvas.SetLeft(pin, c - 3.5); Canvas.SetTop(pin, c - 3.5);
            face.Children.Add(pin);
            return face;
        }

        static Line Hand(double c, double length, double width, Brush brush)
        {
            return new Line { X1 = c, Y1 = c + Math.Min(10, length * 0.18), X2 = c, Y2 = c - length, Stroke = brush, StrokeThickness = width,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, RenderTransform = new RotateTransform(0, c, c) };
        }

        FrameworkElement Calendar()
        {
            var now = DateTime.Now;
            bool mono = IconLook.OneColor;
            Brush red = mono ? (Brush)Brushes.White : new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
            var root = new StackPanel { Margin = new Thickness(14, 12, 14, 8) };
            root.Children.Add(T(now.ToString("MMMM").ToUpper(), 11.5, FontWeights.Bold, red));
            var format = CultureInfo.CurrentCulture.DateTimeFormat;
            int first = (int)format.FirstDayOfWeek;
            var grid = new UniformGrid { Columns = 7, Margin = new Thickness(0, 5, 0, 0) };
            for (int i = 0; i < 7; i++)
            {
                var t = T(format.ShortestDayNames[(first + i) % 7].Substring(0, 1), 9.5, FontWeights.SemiBold, dim);
                t.HorizontalAlignment = HorizontalAlignment.Center;
                grid.Children.Add(t);
            }
            var start = new DateTime(now.Year, now.Month, 1);
            var day = start.AddDays(-(((int)start.DayOfWeek - first + 7) % 7));
            int weeks = (int)Math.Ceiling((((int)start.DayOfWeek - first + 7) % 7 + DateTime.DaysInMonth(now.Year, now.Month)) / 7.0);
            for (int i = 0; i < weeks * 7; i++, day = day.AddDays(1))
            {
                bool today = day.Date == now.Date, other = day.Month != now.Month;
                var t = T(other ? "" : day.Day.ToString(), 10.5, today ? FontWeights.Bold : FontWeights.Medium, today ? (mono ? new SolidColorBrush(IconLook.Accent) : Brushes.White) : fg);
                t.HorizontalAlignment = HorizontalAlignment.Center; t.VerticalAlignment = VerticalAlignment.Center;
                var cell = new Border { Width = 19, Height = 19, CornerRadius = new CornerRadius(9.5), Child = t, Margin = new Thickness(0, 0.5, 0, 0.5), Background = today ? red : Brushes.Transparent };
                grid.Children.Add(cell);
            }
            root.Children.Add(grid);
            return root;
        }

        FrameworkElement WeatherView()
        {
            var w = Weather.Now;
            var root = new Grid { Margin = new Thickness(16, 13, 16, 12) };
            if (Weather.Place == null || w == null)
            {
                var msg = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                var icon = WeatherIcon.Draw(2, true, 46); icon.HorizontalAlignment = HorizontalAlignment.Center;
                msg.Children.Add(icon);
                var line = T(Weather.Place == null ? "Click to choose your city" : Weather.Error ?? "Loading the weather…", 13, FontWeights.Medium, fg);
                line.HorizontalAlignment = HorizontalAlignment.Center; line.Margin = new Thickness(0, 8, 0, 0);
                msg.Children.Add(line);
                root.Children.Add(msg);
                return root;
            }
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            var top = new DockPanel();
            var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            var big = WeatherIcon.Draw(w.Code, w.Day, 30); big.HorizontalAlignment = HorizontalAlignment.Right;
            right.Children.Add(big);
            var cond = T(WeatherIcon.Name(w.Code), 12, FontWeights.SemiBold, fg); cond.HorizontalAlignment = HorizontalAlignment.Right; cond.Margin = new Thickness(0, 3, 0, 0);
            right.Children.Add(cond);
            var hl = T("H:" + w.High + "°  L:" + w.Low + "°", 12, FontWeights.Medium, fg); hl.HorizontalAlignment = HorizontalAlignment.Right;
            right.Children.Add(hl);
            DockPanel.SetDock(right, Dock.Right);
            top.Children.Add(right);
            var left = new StackPanel();
            var city = T(Weather.Place.Name, 13.5, FontWeights.SemiBold, fg);
            left.Children.Add(city);
            left.Children.Add(T(w.Temp + "°", 40, FontWeights.Light, fg));
            top.Children.Add(left);
            root.Children.Add(top);
            var hours = new UniformGrid { Rows = 1, Columns = Math.Max(1, w.Hours.Count), VerticalAlignment = VerticalAlignment.Bottom };
            Grid.SetRow(hours, 1);
            foreach (var hr in w.Hours)
            {
                var col = new StackPanel();
                var time = T(hr.Label, 11, FontWeights.SemiBold, dim); time.HorizontalAlignment = HorizontalAlignment.Center;
                var ic = WeatherIcon.Draw(hr.Code, hr.Day, 20); ic.HorizontalAlignment = HorizontalAlignment.Center; ic.Margin = new Thickness(0, 3, 0, 3);
                var temp = T(hr.Temp + "°", 12, FontWeights.SemiBold, fg); temp.HorizontalAlignment = HorizontalAlignment.Center;
                col.Children.Add(time); col.Children.Add(ic); col.Children.Add(temp);
                hours.Children.Add(col);
            }
            root.Children.Add(hours);
            return root;
        }

        FrameworkElement Music()
        {
            var row = new DockPanel { Margin = new Thickness(14) };
            var art = new Border { Width = Widgets.S - 28, Height = Widgets.S - 28, CornerRadius = new CornerRadius(12), Background = Theme.Dark ? Theme.B("#33FFFFFF") : Theme.B("#1A000000") };
            if (NowPlaying.Active && NowPlaying.Art != null) art.Background = new ImageBrush(NowPlaying.Art) { Stretch = Stretch.UniformToFill };
            else art.Child = new TextBlock { Text = "", FontFamily = Theme.Glyphs, FontSize = 40, Foreground = dim, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(art, Dock.Left);
            row.Children.Add(art);
            var words = new Grid { Margin = new Thickness(14, 2, 0, 0) };
            words.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            words.RowDefinitions.Add(new RowDefinition());
            var info = new StackPanel();
            info.Children.Add(T(NowPlaying.Active ? NowPlaying.Title : "Not Playing", 14, FontWeights.SemiBold, fg));
            if (NowPlaying.Active) info.Children.Add(T(NowPlaying.Artist ?? "", 12.5, FontWeights.Normal, dim));
            words.Children.Add(info);
            if (NowPlaying.Active)
            {
                var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(-6, 0, 0, 2) };
                controls.Children.Add(Control("M11,0 L5,6 L11,12 Z M6,0 L0,6 L6,12 Z", 13, NowPlaying.Previous));
                controls.Children.Add(Control(NowPlaying.Playing ? "M0,0 H3.6 V12 H0 Z M6.4,0 H10 V12 H6.4 Z" : "M0,0 L10,6 L0,12 Z", 18, NowPlaying.Toggle));
                controls.Children.Add(Control("M0,0 L6,6 L0,12 Z M5,0 L11,6 L5,12 Z", 13, NowPlaying.Next));
                Grid.SetRow(controls, 1);
                words.Children.Add(controls);
            }
            row.Children.Add(words);
            return row;
        }

        Border Control(string shape, double height, Action click)
        {
            var data = Geometry.Parse(shape);
            var path = new System.Windows.Shapes.Path { Data = data, Fill = fg, Stretch = Stretch.Fill, Height = height, Width = height * data.Bounds.Width / data.Bounds.Height, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var b = new Border { Width = 40, Height = 36, Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = path };
            b.MouseLeftButtonDown += (s, e) => e.Handled = true;   // not a drag
            b.MouseLeftButtonUp += (s, e) => { e.Handled = true; click(); };
            return b;
        }

        FrameworkElement Battery()
        {
            Native.SYSTEM_POWER_STATUS ps;
            bool has = Native.GetSystemPowerStatus(out ps) && ps.BatteryFlag != 128 && ps.BatteryLifePercent <= 100;
            int pct = has ? ps.BatteryLifePercent : 100;
            bool charging = ps.ACLineStatus == 1;
            var green = Color.FromRgb(0x30, 0xD1, 0x58);
            var color = new SolidColorBrush(pct <= 20 && !charging ? Color.FromRgb(0xFF, 0x45, 0x3A) : IconLook.OneColor ? Colors.White : green);
            var root = new Grid();
            double size = 92, c = size / 2, r = c - 5;
            var ring = new Canvas { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            ring.Children.Add(new Ellipse { Width = size - 10, Height = size - 10, Stroke = Theme.Dark ? Theme.B("#33FFFFFF") : Theme.B("#1A000000"), StrokeThickness = 8, Margin = new Thickness(5) });
            Canvas.SetLeft(ring.Children[0], 0); Canvas.SetTop(ring.Children[0], 0);
            double a = Math.Max(0.001, Math.Min(0.9999, pct / 100.0)) * 2 * Math.PI;
            var arc = new PathFigure { StartPoint = new Point(c, c - r) };
            arc.Segments.Add(new ArcSegment(new Point(c + r * Math.Sin(a), c - r * Math.Cos(a)), new Size(r, r), 0, a > Math.PI, SweepDirection.Clockwise, true));
            ring.Children.Add(new System.Windows.Shapes.Path { Data = new PathGeometry(new[] { arc }), Stroke = color, StrokeThickness = 8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
            var glyph = new TextBlock { Text = charging ? "" : "", FontFamily = Theme.Glyphs, FontSize = 24, Foreground = charging ? color : fg };
            glyph.Measure(new Size(100, 100));
            Canvas.SetLeft(glyph, c - glyph.DesiredSize.Width / 2); Canvas.SetTop(glyph, c - glyph.DesiredSize.Height / 2);
            ring.Children.Add(glyph);
            root.Children.Add(ring);
            var label = T(has ? pct + "%" : "Plugged In", 20, FontWeights.SemiBold, fg);
            label.HorizontalAlignment = HorizontalAlignment.Center; label.VerticalAlignment = VerticalAlignment.Bottom; label.Margin = new Thickness(0, 0, 0, 14);
            root.Children.Add(label);
            return root;
        }

        static string NoteFile { get { return System.IO.Path.Combine(Settings.DataDir, "note.txt"); } }

        FrameworkElement Note()
        {
            var root = new DockPanel { Margin = new Thickness(16, 12, 12, 10) };
            var head = T("Notes", 13, FontWeights.Bold, IconLook.OneColor ? (Brush)Brushes.White : new SolidColorBrush(Color.FromRgb(0xFF, 0xCC, 0x00)));
            DockPanel.SetDock(head, Dock.Top);
            root.Children.Add(head);
            string text = "";
            try { if (File.Exists(NoteFile)) text = File.ReadAllText(NoteFile); } catch { }
            noteBox = new TextBox { Text = text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Foreground = fg, CaretBrush = fg, FontSize = 13.5, Margin = new Thickness(-2, 4, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
            noteBox.PreviewMouseLeftButtonDown += (s, e) => { Native.SetForegroundWindow(hwnd); };
            noteBox.MouseLeftButtonDown += (s, e) => e.Handled = true;
            noteSave = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            noteSave.Tick += delegate { noteSave.Stop(); SaveNote(); };
            var hint = T("Click to write a note", 13.5, FontWeights.Normal, dim);
            hint.Margin = new Thickness(0, 5, 0, 0); hint.VerticalAlignment = VerticalAlignment.Top; hint.IsHitTestVisible = false;
            hint.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            noteBox.TextChanged += delegate { noteSave.Stop(); noteSave.Start(); hint.Visibility = noteBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; };
            var layers = new Grid();
            layers.Children.Add(hint); layers.Children.Add(noteBox);
            root.Children.Add(layers);
            return root;
        }

        void SaveNote()
        {
            if (noteBox == null) return;
            try { Directory.CreateDirectory(Settings.DataDir); Workspace.AtomicWrite(NoteFile, noteBox.Text); } catch (Exception ex) { App.Log(ex); }
        }
    }

    // macOS-style weather symbols, drawn: sun, moon, clouds, rain, snow, lightning, fog.
    static class WeatherIcon
    {
        public static string Name(int code)
        {
            if (code == 0) return "Clear";
            if (code <= 2) return "Partly Cloudy";
            if (code == 3) return "Cloudy";
            if (code == 45 || code == 48) return "Fog";
            if (code >= 51 && code <= 57) return "Drizzle";
            if (code >= 61 && code <= 67) return "Rain";
            if (code >= 71 && code <= 77) return "Snow";
            if (code >= 80 && code <= 82) return "Showers";
            if (code >= 85 && code <= 86) return "Snow Showers";
            if (code >= 95) return "Thunderstorms";
            return "Cloudy";
        }

        public static FrameworkElement Draw(int code, bool day, double size)
        {
            var c = new Canvas { Width = size, Height = size };
            double k = size / 32;
            var sunColor = new SolidColorBrush(Color.FromRgb(0xFF, 0xCC, 0x00));
            bool clear = code == 0, partly = code == 1 || code == 2, fog = code == 45 || code == 48;
            bool rain = (code >= 51 && code <= 67) || (code >= 80 && code <= 82), snow = (code >= 71 && code <= 77) || code == 85 || code == 86, storm = code >= 95;
            if (clear || partly)
            {
                double cx = (clear ? 16 : 12) * k, cy = (clear ? 16 : 11) * k, r = (clear ? 7.5 : 6) * k;
                if (day)
                {
                    for (int i = 0; i < 8; i++)
                    {
                        double a = i * Math.PI / 4;
                        c.Children.Add(new Line { X1 = cx + (r + 2.5 * k) * Math.Cos(a), Y1 = cy + (r + 2.5 * k) * Math.Sin(a), X2 = cx + (r + 5 * k) * Math.Cos(a), Y2 = cy + (r + 5 * k) * Math.Sin(a), Stroke = sunColor, StrokeThickness = 2 * k, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
                    }
                    var sun = new Ellipse { Width = 2 * r, Height = 2 * r, Fill = sunColor };
                    Canvas.SetLeft(sun, cx - r); Canvas.SetTop(sun, cy - r); c.Children.Add(sun);
                }
                else
                {
                    var moon = new System.Windows.Shapes.Path { Fill = Brushes.WhiteSmoke, Data = new CombinedGeometry(GeometryCombineMode.Exclude, new EllipseGeometry(new Point(cx, cy), r + 1, r + 1), new EllipseGeometry(new Point(cx + r * 0.75, cy - r * 0.45), r, r)) };
                    c.Children.Add(moon);
                }
                if (clear) return c;
            }
            if (fog)
            {
                for (int i = 0; i < 3; i++) c.Children.Add(new Line { X1 = 5 * k, X2 = 27 * k, Y1 = (11 + i * 5) * k, Y2 = (11 + i * 5) * k, Stroke = Brushes.LightGray, StrokeThickness = 2.2 * k, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
                return c;
            }
            // The cloud: three puffs on a flat base.
            double dy = rain || snow || storm ? -3 * k : partly ? 2 * k : 0;
            var cloud = new GeometryGroup { FillRule = FillRule.Nonzero };
            cloud.Children.Add(new EllipseGeometry(new Point(11 * k, 19 * k + dy), 6 * k, 6 * k));
            cloud.Children.Add(new EllipseGeometry(new Point(17.5 * k, 14.5 * k + dy), 7.5 * k, 7.5 * k));
            cloud.Children.Add(new EllipseGeometry(new Point(23.5 * k, 19.5 * k + dy), 5.5 * k, 5.5 * k));
            cloud.Children.Add(new RectangleGeometry(new Rect(11 * k, 17 * k + dy, 12.5 * k, 8 * k), 2 * k, 2 * k));
            // White clouds get a soft outline on light glass, where white alone would vanish.
            c.Children.Add(new System.Windows.Shapes.Path { Data = cloud, Fill = new LinearGradientBrush(Colors.White, Color.FromRgb(0xD0, 0xD6, 0xE0), 90),
                Stroke = Theme.Dark ? null : Theme.B("#40000000"), StrokeThickness = 0.8 * k });
            var blue = new SolidColorBrush(Color.FromRgb(0x4D, 0xA3, 0xFF));
            if (rain) for (int i = 0; i < 3; i++) c.Children.Add(new Line { X1 = (12 + i * 5) * k, Y1 = 25 * k, X2 = (10.5 + i * 5) * k, Y2 = 29.5 * k, Stroke = blue, StrokeThickness = 2 * k, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
            if (snow) for (int i = 0; i < 3; i++) { var f = new Ellipse { Width = 3 * k, Height = 3 * k, Fill = Brushes.White }; Canvas.SetLeft(f, (10.5 + i * 5) * k); Canvas.SetTop(f, (25.5 + (i % 2) * 2) * k); c.Children.Add(f); }
            if (storm) c.Children.Add(new System.Windows.Shapes.Path { Fill = sunColor, Data = Geometry.Parse(string.Format(CultureInfo.InvariantCulture, "M{0},{1} L{2},{3} L{4},{3} L{5},{6} L{7},{8} L{9},{8} Z", 17 * k, 23 * k, 13 * k, 28.5 * k, 16 * k, 14.5 * k, 32 * k, 19.5 * k, 26.5 * k, 16.5 * k)) });
            return c;
        }
    }

    // The weather for the widget, from Open-Meteo (free, no account): the city you pick in Settings is looked up
    // once, then the forecast refreshes every half hour.
    static class Weather
    {
        public class Hour { public string Label; public int Temp, Code; public bool Day; }
        public class Report { public int Temp, High, Low, Code; public bool Day; public List<Hour> Hours = new List<Hour>(); }
        public class Spot { public string Name; public double Lat, Lon; }

        public static Report Now;
        public static string Error;
        public static event Action Changed;
        static DispatcherTimer timer;
        static string fetched;

        public static Spot Place
        {
            get
            {
                var p = (Settings.WeatherPlace ?? "").Split('|');
                double lat, lon;
                if (p.Length == 3 && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out lat) && double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out lon))
                    return new Spot { Name = p[0], Lat = lat, Lon = lon };
                return null;
            }
        }

        static bool loading;

        public static void Start()
        {
            timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
            timer.Tick += delegate { Refresh(); };
            timer.Start();
            // a new place, or the widget just turned on (or added) with nothing loaded yet
            Settings.Changed += delegate
            {
                bool wanted = Settings.Widgets && Widgets.List.Contains("weather");
                if (Settings.WeatherPlace != fetched || (wanted && Now == null && Error == null && !loading)) Refresh();
            };
            Refresh();
        }

        static bool Imperial { get { try { return !new RegionInfo(CultureInfo.CurrentCulture.Name).IsMetric; } catch { return false; } } }

        public static void Refresh()
        {
            var place = Place;
            fetched = Settings.WeatherPlace;
            if (place == null || !Settings.Widgets || !Widgets.List.Contains("weather")) { if (place == null) { Now = null; if (Changed != null) Changed(); } return; }
            string url = string.Format(CultureInfo.InvariantCulture,
                "https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}&current=temperature_2m,weather_code,is_day&hourly=temperature_2m,weather_code,is_day&daily=temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=2{2}",
                place.Lat, place.Lon, Imperial ? "&temperature_unit=fahrenheit" : "");
            loading = true;
            Task.Run(() => Get(url)).ContinueWith(t =>
            {
                loading = false;
                try
                {
                    if (t.Result == null) { Error = "Weather isn't available right now"; if (Changed != null) Changed(); return; }
                    Now = Parse(t.Result); Error = null;
                }
                catch (Exception ex) { App.Log(ex); Error = "Weather isn't available right now"; }
                if (Changed != null) Changed();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        static string Get(string url)
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                using (var wc = new WebClient()) { wc.Headers[HttpRequestHeader.UserAgent] = "GlassDock"; wc.Encoding = System.Text.Encoding.UTF8; return wc.DownloadString(url); }
            }
            catch { return null; }
        }

        static double Num(object o) { return Convert.ToDouble(o, CultureInfo.InvariantCulture); }

        static Report Parse(string json)
        {
            var root = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
            var cur = (Dictionary<string, object>)root["current"];
            var daily = (Dictionary<string, object>)root["daily"];
            var hourly = (Dictionary<string, object>)root["hourly"];
            var r = new Report
            {
                Temp = (int)Math.Round(Num(cur["temperature_2m"])), Code = (int)Num(cur["weather_code"]), Day = Num(cur["is_day"]) > 0,
                High = (int)Math.Round(Num(((object[])daily["temperature_2m_max"])[0])), Low = (int)Math.Round(Num(((object[])daily["temperature_2m_min"])[0]))
            };
            // The next hours, starting with the current one (times are the place's local time).
            var times = (object[])hourly["time"]; var temps = (object[])hourly["temperature_2m"]; var codes = (object[])hourly["weather_code"]; var days = (object[])hourly["is_day"];
            string nowStamp = ((string)cur["time"]).Substring(0, 13);
            int i0 = Array.FindIndex(times, x => ((string)x).StartsWith(nowStamp));
            if (i0 < 0) i0 = 0;
            for (int i = i0; i < Math.Min(times.Length, i0 + 6); i++)
            {
                var when = DateTime.Parse((string)times[i], CultureInfo.InvariantCulture);
                r.Hours.Add(new Hour { Label = i == i0 ? "Now" : when.ToString(CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains("H") ? "HH" : "h tt").Replace(" ", "").ToUpper(),
                    Temp = (int)Math.Round(Num(temps[i])), Code = (int)Num(codes[i]), Day = Num(days[i]) > 0 });
            }
            return r;
        }

        // Looks a city up by name; `done` gets its display name, or null if nothing matched.
        public static void Find(string city, Action<string> done)
        {
            string url = "https://geocoding-api.open-meteo.com/v1/search?count=1&format=json&name=" + Uri.EscapeDataString(city.Trim());
            Task.Run(() => Get(url)).ContinueWith(t =>
            {
                string name = null;
                try
                {
                    if (t.Result != null)
                    {
                        var root = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(t.Result);
                        object res;
                        if (root.TryGetValue("results", out res) && ((object[])res).Length > 0)
                        {
                            var hit = (Dictionary<string, object>)((object[])res)[0];
                            name = (string)hit["name"];
                            Settings.WeatherPlace = name.Replace("|", " ") + "|" + Num(hit["latitude"]).ToString(CultureInfo.InvariantCulture) + "|" + Num(hit["longitude"]).ToString(CultureInfo.InvariantCulture);
                            object country;
                            if (hit.TryGetValue("country", out country) && country != null) name += ", " + country;
                            Settings.Apply();
                        }
                    }
                }
                catch (Exception ex) { App.Log(ex); }
                done(name);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }
}
