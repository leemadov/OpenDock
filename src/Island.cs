using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace OpenDock
{
    // What's playing, from Windows' media sessions (the ones the volume flyout shows). Windows reports on
    // background threads; everything public here changes, and Changed fires, on the UI thread only.
    static class NowPlaying
    {
        public static string Title, Artist;
        public static bool Playing;
        public static DateTime PausedAt = DateTime.MinValue;
        public static ImageSource Art;
        public static Color Accent = Colors.White;
        public static bool Active { get { return session != null && !string.IsNullOrEmpty(Title); } }
        public static event Action Changed;

        static GlobalSystemMediaTransportControlsSessionManager manager;
        static GlobalSystemMediaTransportControlsSession session;
        static Dispatcher ui;
        static int generation;
        static readonly global::Windows.Foundation.TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs> onProps = (s, e) => Post(ReadProps);
        static readonly global::Windows.Foundation.TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs> onPlayback = (s, e) => Post(ReadPlayback);

        public static void Start()
        {
            if (ui != null) return;
            ui = Dispatcher.CurrentDispatcher;
            try { Begin(); }
            catch (Exception ex) { App.Log(ex); }   // media sessions need Windows 10 1809 or later
        }

        static void Begin()
        {
            Then(GlobalSystemMediaTransportControlsSessionManager.RequestAsync(), m =>
            {
                manager = m;
                m.CurrentSessionChanged += (s, e) => Post(Pick);
                Pick();
            });
        }

        public static void Toggle() { Send(s => s.TryTogglePlayPauseAsync()); }
        public static void Next() { Send(s => s.TrySkipNextAsync()); }
        public static void Previous() { Send(s => s.TrySkipPreviousAsync()); }

        static void Send(Func<GlobalSystemMediaTransportControlsSession, global::Windows.Foundation.IAsyncOperation<bool>> command)
        {
            var s = session;
            if (s == null) return;
            try { var op = command(s); } catch (Exception ex) { App.Log(ex); }
        }

        static void Pick()
        {
            var s = manager.GetCurrentSession();
            if (s != session)
            {
                if (session != null) try { session.MediaPropertiesChanged -= onProps; session.PlaybackInfoChanged -= onPlayback; } catch { }
                session = s;
                if (s != null) { s.MediaPropertiesChanged += onProps; s.PlaybackInfoChanged += onPlayback; }
            }
            ReadPlayback();
            ReadProps();
        }

        static void ReadPlayback()
        {
            bool playing = false;
            try { playing = session != null && session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; } catch { }
            if (Playing && !playing) PausedAt = DateTime.UtcNow;
            Playing = playing;
            Raise();
        }

        static void ReadProps()
        {
            var s = session;
            int gen = ++generation;
            if (s == null) { Title = Artist = null; Art = null; Raise(); return; }
            try
            {
                Then(s.TryGetMediaPropertiesAsync(), p =>
                {
                    if (gen != generation) return;
                    string artist = string.IsNullOrEmpty(p.Artist) ? p.AlbumArtist : p.Artist;
                    bool sameSong = p.Title == Title && artist == Artist;
                    Title = p.Title; Artist = artist;
                    if (!sameSong) { Art = null; Accent = Colors.White; LoadArt(p.Thumbnail, gen); }
                    Raise();
                });
            }
            catch (Exception ex) { App.Log(ex); }
        }

        static void LoadArt(IRandomAccessStreamReference art, int gen)
        {
            if (art == null) return;
            Then(art.OpenReadAsync(), stream =>
            {
                var reader = new DataReader(stream);
                Then(reader.LoadAsync((uint)Math.Min(stream.Size, 8u << 20)), n =>
                {
                    var bytes = new byte[n];
                    reader.ReadBytes(bytes);
                    reader.Dispose(); stream.Dispose();
                    if (gen != generation) return;
                    try
                    {
                        var bi = new BitmapImage();
                        bi.BeginInit();
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        bi.StreamSource = new MemoryStream(bytes);
                        bi.DecodePixelWidth = 120;
                        bi.EndInit();
                        bi.Freeze();
                        Art = bi; Accent = Vivid(bi);
                        Raise();
                    }
                    catch { }
                });
            });
        }

        // The cover's average colour, brightened, for the waveform -- the way the iPhone tints it.
        static Color Vivid(BitmapSource b)
        {
            var c = new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0);
            var px = new byte[c.PixelWidth * c.PixelHeight * 4];
            c.CopyPixels(px, c.PixelWidth * 4, 0);
            double r = 0, g = 0, bl = 0; int n = 0;
            for (int i = 0; i < px.Length; i += 16) { bl += px[i]; g += px[i + 1]; r += px[i + 2]; n++; }
            if (n == 0) return Colors.White;
            r /= n; g /= n; bl /= n;
            double max = Math.Max(r, Math.Max(g, bl)), min = Math.Min(r, Math.Min(g, bl));
            if (max - min < 24) return Color.FromRgb(235, 235, 240);              // greyish cover: plain white bars
            double k = 255 / Math.Max(1, max), mid = (max + min) / 2;
            Func<double, byte> boost = v => (byte)Math.Max(60, Math.Min(255, (mid + (v - mid) * 1.4) * k));
            return Color.FromRgb(boost(r), boost(g), boost(bl));
        }

        static void Then<T>(global::Windows.Foundation.IAsyncOperation<T> op, Action<T> done) { WinRT.Then(op, done); }
        static void Post(Action a) { WinRT.Post(a); }

        static void Raise() { if (Changed != null) Changed(); }
    }

    // An optional Dynamic Island: a black pill in the middle of the menu bar that grows into whatever is going
    // on -- now playing, a running timer, charging, a finished download -- and opens up on hover.
    static class Island
    {
        static IslandWindow window;

        public static void Start()
        {
            Settings.Changed += Apply;
            Apply();
        }

        static void Apply()
        {
            if (!Settings.DynamicIsland) { if (window != null) window.Hide(); return; }
            if (window == null)
            {
                NowPlaying.Start();
                window = new IslandWindow();
                SystemEvents.PowerModeChanged += OnPower;
                Native.SYSTEM_POWER_STATUS ps;
                if (Native.GetSystemPowerStatus(out ps)) plugged = ps.ACLineStatus == 1;
            }
            window.Place();
            window.Update();
        }

        public static void Refresh() { if (window != null && Settings.DynamicIsland) window.Update(); }
        public static void Place() { if (window != null && Settings.DynamicIsland) window.Place(); }
        public static void SetCovered(bool covered) { if (window != null) window.SetCovered(covered); }

        public static void Alert(string glyph, string text, Color color)
        {
            if (window != null && Settings.DynamicIsland) window.Alert(glyph, text, color);
        }

        public static void Downloaded(string name) { Alert("\uE896", name, Color.FromRgb(0x0A, 0x84, 0xFF)); }
        public static void TimerDone() { Alert("\uE916", "Timer finished", Color.FromRgb(0xFF, 0x9F, 0x0A)); }

        static bool plugged;
        static void OnPower(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.StatusChange || window == null) return;
            window.Dispatcher.BeginInvoke(new Action(() =>
            {
                Native.SYSTEM_POWER_STATUS ps;
                if (!Native.GetSystemPowerStatus(out ps) || ps.BatteryFlag == 128 || ps.BatteryFlag == 255) return;   // no battery
                bool now = ps.ACLineStatus == 1;
                if (now == plugged) return;
                plugged = now;
                string level = ps.BatteryLifePercent <= 100 ? ps.BatteryLifePercent + "%" : "";
                if (now) Alert("\uEBB5", ("Charging  " + level).Trim(), Color.FromRgb(0x30, 0xD1, 0x58));
                else Alert("\uEBA7", ("On battery  " + level).Trim(), Colors.White);
            }));
        }
    }

    class IslandWindow : Window
    {
        const double Frame = 440, FrameH = 210;      // the window; only the pill inside it is drawn or clickable
        readonly Grid pill = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
        // What the cursor has to touch: the pill plus a margin that reaches the very top of the screen, where a
        // cursor thrown upward ends up. Nearly invisible, but solid enough for Windows to send it the mouse.
        readonly Border zone = new Border { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
        readonly Rectangle shape = new Rectangle { Fill = Brushes.Black };
        readonly Grid body = new Grid { ClipToBounds = true };
        readonly DispatcherTimer hoverIn = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        readonly DispatcherTimer hoverOut = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        readonly DispatcherTimer alertEnd = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.2) };
        readonly DispatcherTimer pauseEnd = new DispatcherTimer();
        bool expanded, covered;
        string shown;                                    // what the pill shows now; rebuilt only when it changes
        string alertGlyph, alertText; Color alertColor;
        TextBlock timerText;

        static readonly TimeSpan PausedLinger = TimeSpan.FromMinutes(2);

        public IslandWindow()
        {
            Title = "OpenDock Island";
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = null;
            ShowInTaskbar = false; Topmost = true; ResizeMode = ResizeMode.NoResize; ShowActivated = false;
            Width = Frame; Height = FrameH;
            FontFamily = Theme.Font; Foreground = Brushes.White;
            pill.Children.Add(shape);
            pill.Children.Add(body);
            pill.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.35, Color = Colors.Black };
            zone.Child = pill;
            Content = zone;

            // Touching it with the cursor opens it: a quick peek at the time when idle, the full view of an activity.
            zone.MouseEnter += delegate { hoverOut.Stop(); if (!expanded && CanExpand) { hoverIn.Interval = TimeSpan.FromMilliseconds(MediaShown || TimerShown ? 140 : 70); hoverIn.Start(); } };
            zone.MouseLeave += delegate { hoverIn.Stop(); if (expanded) hoverOut.Start(); };
            zone.MouseLeftButtonUp += (s, e) => { if (!expanded && CanExpand) { hoverIn.Stop(); expanded = true; Update(); } };
            hoverIn.Tick += delegate { hoverIn.Stop(); if (CanExpand) { expanded = true; Update(); } };
            hoverOut.Tick += delegate { hoverOut.Stop(); expanded = false; Update(); };
            alertEnd.Tick += delegate { alertEnd.Stop(); alertText = null; Update(); if (zone.IsMouseOver && !expanded) hoverIn.Start(); };
            pauseEnd.Tick += delegate { pauseEnd.Stop(); Update(); };

            SourceInitialized += delegate { Native.MakeToolWindow(new WindowInteropHelper(this).Handle, true); };
            NowPlaying.Changed += Update;
            App.ForegroundChanged += OnForeground;
            Closed += delegate { NowPlaying.Changed -= Update; App.ForegroundChanged -= OnForeground; };

            var start = CompactSize(IdleWidth);
            pill.Width = start.Width; pill.Height = start.Height;
            shape.RadiusX = shape.RadiusY = start.Height / 2;
        }

        // Small and rounded, like the MacBook's: just inside the menu bar, about two and a half times as wide as tall.
        static double H { get { return Settings.ShowMenuBar ? Math.Max(20, MenuBarWindow.H - 4) : 30; } }
        static double IdleWidth { get { return Math.Round(H * 2.6); } }
        static Size CompactSize(double w) { return new Size(w, H); }

        bool MediaShown { get { return NowPlaying.Active && (NowPlaying.Playing || DateTime.UtcNow - NowPlaying.PausedAt < PausedLinger); } }
        bool TimerShown { get { return Living.TimerText != ""; } }
        bool CanExpand { get { return alertText == null; } }

        public void Place()
        {
            var b = DisplayLayout.Dip(DisplayLayout.Selected.Bounds);
            Left = b.Left + (b.Width - Frame) / 2;
            Top = b.Top;
            zone.Padding = new Thickness(10, Settings.ShowMenuBar ? (MenuBarWindow.H - H) / 2 : 6, 10, 8);
        }

        // Both are always-on-top windows; whichever Windows raised last wins. If the menu bar ends up above the
        // island it gets the mouse and the island never hears the cursor, so the island steps back on top.
        void OnForeground(IntPtr h) { if (IsVisible) Raise(); }

        void Raise()
        {
            Native.SetWindowPos(new WindowInteropHelper(this).Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        public void SetCovered(bool value)
        {
            if (covered == value) return;
            covered = value;
            Update();
        }

        public void Alert(string glyph, string text, Color color)
        {
            alertGlyph = glyph; alertText = text; alertColor = color;
            expanded = false;
            alertEnd.Stop(); alertEnd.Start();
            Update();
        }

        public void Update()
        {
            if (!Settings.DynamicIsland || covered) { if (IsVisible) Hide(); return; }
            if (!IsVisible)
            {
                Show();
                Raise();   // above the menu bar
            }
            bool media = MediaShown, timer = TimerShown;
            if (NowPlaying.Active && !NowPlaying.Playing && media) { pauseEnd.Stop(); pauseEnd.Interval = PausedLinger - (DateTime.UtcNow - NowPlaying.PausedAt) + TimeSpan.FromSeconds(0.5); pauseEnd.Start(); }

            string state = alertText != null ? "alert|" + alertGlyph + alertText
                : (expanded ? "open|" + (media || timer ? "" : DateTime.Now.ToString("t")) : "compact|") + (media ? NowPlaying.Title + "|" + NowPlaying.Artist + "|" + NowPlaying.Playing + "|" + (NowPlaying.Art != null) : "") + "|" + timer + "|" + Living.TimerRunning + "|" + H;
            if (state == shown) { if (timerText != null) timerText.Text = Living.TimerText; return; }
            shown = state;
            timerText = null;

            FrameworkElement content; Size size; double radius;
            if (alertText != null) { content = AlertView(); size = new Size(Math.Max(IdleWidth + 2 * H + 40, Measure(alertText, 13) + H + 52), H); radius = H / 2; }
            else if (expanded && media) { content = MediaView(timer); size = new Size(372, timer ? 160 : 128); radius = 32; }
            else if (expanded && timer) { content = TimerView(); size = new Size(280, 76); radius = 30; }
            else if (expanded) { content = PeekView(); size = new Size(Math.Max(196, IdleWidth * 2.6), H + 14); radius = (H + 14) / 2; }
            else if (media || timer) { content = CompactView(media, timer); size = new Size(IdleWidth + 2 * (H + 2), H); radius = H / 2; }
            else { content = null; size = new Size(IdleWidth, H); radius = H / 2; }

            body.Children.Clear();
            if (content != null)
            {
                content.Opacity = 0;
                body.Children.Add(content);
                content.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { BeginTime = TimeSpan.FromMilliseconds(120) });
            }
            Morph(size, radius);
        }

        // A springy morph, like the iPhone's: a little overshoot, then settle.
        void Morph(Size to, double radius)
        {
            var ease = new BackEase { Amplitude = 0.28, EasingMode = EasingMode.EaseOut };
            var time = TimeSpan.FromMilliseconds(460);
            pill.BeginAnimation(WidthProperty, new DoubleAnimation(to.Width, time) { EasingFunction = ease });
            pill.BeginAnimation(HeightProperty, new DoubleAnimation(to.Height, time) { EasingFunction = ease });
            shape.BeginAnimation(Rectangle.RadiusXProperty, new DoubleAnimation(radius, time) { EasingFunction = ease });
            shape.BeginAnimation(Rectangle.RadiusYProperty, new DoubleAnimation(radius, time) { EasingFunction = ease });
        }

        // ---------- views ----------

        FrameworkElement CompactView(bool media, bool timer)
        {
            var g = new Grid { Margin = new Thickness(7, 0, 12, 0) };
            FrameworkElement left = media ? Cover(H - 8, 6) : (FrameworkElement)Glyph("\uE916", 14, Color.FromRgb(0xFF, 0x9F, 0x0A));
            left.HorizontalAlignment = HorizontalAlignment.Left; left.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(left);
            FrameworkElement right;
            if (timer) right = timerText = Label(Living.TimerText, 13, FontWeights.SemiBold, Color.FromRgb(0xFF, 0x9F, 0x0A));
            else right = Waveform(NowPlaying.Playing, NowPlaying.Accent, H - 12);
            right.HorizontalAlignment = HorizontalAlignment.Right; right.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(right);
            return g;
        }

        // Idle, under the cursor: the time and date, the way the island shows a glance when nothing is going on.
        FrameworkElement PeekView()
        {
            var g = new Grid { Margin = new Thickness(16, 0, 16, 0) };
            var time = Label(DateTime.Now.ToString("t"), 15, FontWeights.SemiBold, Colors.White);
            time.HorizontalAlignment = HorizontalAlignment.Left; time.VerticalAlignment = VerticalAlignment.Center;
            var date = Label(DateTime.Now.ToString("ddd, MMM d"), 13, FontWeights.Normal, Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF));
            date.HorizontalAlignment = HorizontalAlignment.Right; date.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(time); g.Children.Add(date);
            return g;
        }

        FrameworkElement AlertView()
        {
            var g = new Grid { Margin = new Thickness(10, 0, 14, 0) };
            var icon = Glyph(alertGlyph, 14, alertColor);
            icon.HorizontalAlignment = HorizontalAlignment.Left; icon.VerticalAlignment = VerticalAlignment.Center;
            var text = Label(alertText, 13, FontWeights.SemiBold, Colors.White);
            text.HorizontalAlignment = HorizontalAlignment.Right; text.VerticalAlignment = VerticalAlignment.Center;
            text.TextTrimming = TextTrimming.CharacterEllipsis; text.MaxWidth = 300;
            g.Children.Add(icon); g.Children.Add(text);
            return g;
        }

        FrameworkElement MediaView(bool timer)
        {
            var root = new StackPanel { Margin = new Thickness(18, 16, 18, 12) };
            var top = new DockPanel();
            var cover = Cover(56, 12);
            DockPanel.SetDock(cover, Dock.Left);
            top.Children.Add(cover);
            var wave = Waveform(NowPlaying.Playing, NowPlaying.Accent, 20);
            wave.VerticalAlignment = VerticalAlignment.Center; wave.Margin = new Thickness(10, 0, 2, 0);
            DockPanel.SetDock(wave, Dock.Right);
            top.Children.Add(wave);
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            var title = Label(NowPlaying.Title, 15, FontWeights.SemiBold, Colors.White); title.TextTrimming = TextTrimming.CharacterEllipsis;
            var artist = Label(NowPlaying.Artist ?? "", 13, FontWeights.Normal, Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)); artist.TextTrimming = TextTrimming.CharacterEllipsis;
            words.Children.Add(title);
            if (!string.IsNullOrEmpty(NowPlaying.Artist)) words.Children.Add(artist);
            top.Children.Add(words);
            root.Children.Add(top);

            var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
            controls.Children.Add(Button("\uE892", 18, NowPlaying.Previous));
            controls.Children.Add(Button(NowPlaying.Playing ? "\uE769" : "\uE768", 24, NowPlaying.Toggle));
            controls.Children.Add(Button("\uE893", 18, NowPlaying.Next));
            root.Children.Add(controls);

            if (timer)
            {
                var row = TimerRow(13);
                row.Margin = new Thickness(0, 8, 0, 0);
                root.Children.Add(row);
            }
            return root;
        }

        FrameworkElement TimerView()
        {
            var row = TimerRow(26);
            row.Margin = new Thickness(22, 0, 16, 0);
            row.VerticalAlignment = VerticalAlignment.Center;
            return row;
        }

        DockPanel TimerRow(double size)
        {
            var orange = Color.FromRgb(0xFF, 0x9F, 0x0A);
            var row = new DockPanel();
            var icon = Glyph("\uE916", size * 0.7, orange);
            icon.VerticalAlignment = VerticalAlignment.Center; icon.Margin = new Thickness(0, 0, 10, 0);
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            var pause = Button(Living.TimerRunning ? "\uE769" : "\uE768", size * 0.7, Living.PauseTimer);
            DockPanel.SetDock(pause, Dock.Right);
            row.Children.Add(pause);
            timerText = Label(Living.TimerText, size, FontWeights.SemiBold, orange);
            timerText.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(timerText);
            return row;
        }

        // ---------- pieces ----------

        FrameworkElement Cover(double size, double radius)
        {
            var b = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(radius), Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E)) };
            if (NowPlaying.Art != null) b.Background = new ImageBrush(NowPlaying.Art) { Stretch = Stretch.UniformToFill };
            else b.Child = Glyph("\uE8D6", size * 0.45, Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
            return b;
        }

        // Four bars bouncing while music plays; still (and free to draw) when paused.
        static FrameworkElement Waveform(bool playing, Color color, double height)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Height = height };
            var brush = new SolidColorBrush(color); brush.Freeze();
            double[] rest = { 0.35, 0.6, 0.45, 0.3 }, period = { 0.42, 0.55, 0.37, 0.5 };
            for (int i = 0; i < 4; i++)
            {
                var scale = new ScaleTransform(1, rest[i]);
                var bar = new Rectangle { Width = 3, Height = height, RadiusX = 1.5, RadiusY = 1.5, Fill = brush, Margin = new Thickness(1.2, 0, 1.2, 0),
                    RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = scale };
                if (playing)
                {
                    var a = new DoubleAnimation(0.2, 1, TimeSpan.FromSeconds(period[i])) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
                    Timeline.SetDesiredFrameRate(a, 24);   // smooth enough, and a fraction of the redraws
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
                }
                panel.Children.Add(bar);
            }
            return panel;
        }

        static TextBlock Glyph(string glyph, double size, Color color)
        {
            return new TextBlock { Text = glyph, FontFamily = Theme.Glyphs, FontSize = size, Foreground = new SolidColorBrush(color), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }

        static TextBlock Label(string text, double size, FontWeight weight, Color color)
        {
            return new TextBlock { Text = text ?? "", FontSize = size, FontWeight = weight, Foreground = new SolidColorBrush(color) };
        }

        static FrameworkElement Button(string glyph, double size, Action click)
        {
            var b = new Border { Width = size + 22, Height = size + 16, CornerRadius = new CornerRadius((size + 16) / 2), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            b.Child = Glyph(glyph, size, Colors.White);
            b.MouseEnter += delegate { b.Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)); };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; };
            b.MouseLeftButtonUp += (s, e) => { e.Handled = true; click(); };
            return b;
        }

        double Measure(string text, double size)
        {
            var t = Label(text, size, FontWeights.SemiBold, Colors.White);
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return Math.Min(300, t.DesiredSize.Width);
        }
    }
}
