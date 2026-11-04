using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace OpenDock
{
    class Note
    {
        public string App, Title, Body, Glyph;
        public ImageSource Icon;
        public Color Tint = Color.FromRgb(0x0A, 0x84, 0xFF);
        public DateTime At = DateTime.Now;
        public Action Open;
    }

    // macOS notifications: a glass banner slides in at the top right and slips away after a few seconds; every
    // one is kept in Notification Center. Apps' Windows notifications are mirrored here too, and a Focus holds
    // the banners back (they still collect in Notification Center).
    static class Notify
    {
        public static readonly List<Note> History = new List<Note>();
        public static event Action Changed;

        public static void Post(Note n, bool banner = true)
        {
            History.Insert(0, n);
            if (History.Count > 60) History.RemoveRange(60, History.Count - 60);
            if (Changed != null) Changed();
            if (banner && !FocusMode.Active) Banner.Show(n);
        }

        public static void Clear() { History.Clear(); if (Changed != null) Changed(); }

        // OpenDock's own events: the Dynamic Island shows them when it's on, a banner otherwise.
        public static void Downloaded(string name)
        {
            if (Settings.DynamicIsland && !FocusMode.Active) Island.Downloaded(name);
            Post(new Note { App = "Downloads", Title = "Download finished", Body = name, Icon = Art.Downloads, Open = () => App.ShowInFinder(Path.Combine(Launcher.Downloads, name)) }, !Settings.DynamicIsland);
        }

        public static void TimerDone()
        {
            if (Settings.DynamicIsland && !FocusMode.Active) Island.TimerDone();
            Post(new Note { App = "Clock", Title = "Timer", Body = "Your timer has finished.", Glyph = "", Tint = Color.FromRgb(0xFF, 0x9F, 0x0A) }, !Settings.DynamicIsland);
        }

        // ---------- Windows notifications ----------

        static UserNotificationListener listener;
        static readonly HashSet<uint> seen = new HashSet<uint>();
        static DispatcherTimer poll;
        static bool first = true, busy;

        public static void Start()
        {
            try
            {
                listener = UserNotificationListener.Current;
                if (listener.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed) { listener = null; return; }
            }
            catch (Exception ex) { App.Log("notification listener unavailable: " + ex.Message); listener = null; return; }
            poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            poll.Tick += delegate { Check(); };
            poll.Start();
            Check();
        }

        static void Check()
        {
            if (listener == null || busy || !Settings.Banners) return;
            busy = true;
            try
            {
                WinRT.Then(listener.GetNotificationsAsync(NotificationKinds.Toast), list =>
                {
                    busy = false;
                    var fresh = new List<UserNotification>();
                    foreach (var n in list) if (seen.Add(n.Id) && !first) fresh.Add(n);
                    first = false;
                    foreach (var n in fresh.OrderBy(x => x.CreationTime)) Mirror(n);
                });
            }
            catch (Exception ex) { busy = false; App.Log(ex); }
            // WinRT.Then drops failures silently; never stay stuck waiting for one.
            var reset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            reset.Tick += delegate { reset.Stop(); busy = false; };
            reset.Start();
        }

        static void Mirror(UserNotification n)
        {
            string app = null, aumid = null;
            try { app = n.AppInfo.DisplayInfo.DisplayName; aumid = n.AppInfo.AppUserModelId; } catch { }
            var texts = new List<string>();
            try
            {
                var binding = n.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
                if (binding != null) texts = binding.GetTextElements().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            }
            catch { }
            if (texts.Count == 0) return;
            var note = new Note { App = app ?? "Notification", Title = texts[0], Body = string.Join("\n", texts.Skip(1)), At = n.CreationTime.LocalDateTime };
            if (aumid != null)
            {
                string id = aumid;
                note.Open = () => Launcher.Open("app:" + id);
                Icons.Get("shell:AppsFolder\\" + id, true, img => { note.Icon = img; Banner.IconLoaded(note); });
            }
            Post(note);
        }
    }

    // A Focus: Do Not Disturb, Work, Personal or Sleep. Banners wait, Windows' own notifications go quiet, and the
    // menu bar shows the Focus' symbol. It can end on its own after an hour, this evening or tomorrow morning.
    static class FocusMode
    {
        public static readonly string[] Names = { "Off", "Do Not Disturb", "Work", "Personal", "Sleep" };
        public static readonly string[] Glyphs = { "", "", "", "", "" };   // moon, briefcase, person, crescent
        public static readonly Color[] Colors2 = { Colors.Gray, Color.FromRgb(0x5E, 0x5C, 0xE6), Color.FromRgb(0x32, 0xAD, 0xE6), Color.FromRgb(0xAF, 0x52, 0xDE), Color.FromRgb(0x30, 0xD1, 0x58) };
        public static event Action Changed;
        static DispatcherTimer timer;

        public static bool Active { get { return Settings.Focus > 0 && Settings.Focus < Names.Length; } }
        public static string Glyph { get { return Active ? Glyphs[Settings.Focus] : ""; } }
        public static string Name { get { return Active ? Names[Settings.Focus] : ""; } }

        public static void Start()
        {
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            timer.Tick += delegate { Expire(); };
            timer.Start();
            Expire();
        }

        static void Expire()
        {
            long until;
            if (Active && long.TryParse(Settings.FocusUntil, out until) && DateTime.UtcNow.Ticks >= until) Set(0, null);
        }

        public static void Set(int mode, DateTime? until)
        {
            bool was = Active;
            if (mode > 0 && !was) Settings.FocusSavedToasts = Sys.Toasts ? 1 : 0;
            Settings.Focus = mode;
            Settings.FocusUntil = mode > 0 && until.HasValue ? until.Value.ToUniversalTime().Ticks.ToString() : "";
            try
            {
                if (mode > 0) Sys.Toasts = false;
                else if (Settings.FocusSavedToasts >= 0) { Sys.Toasts = Settings.FocusSavedToasts == 1; Settings.FocusSavedToasts = -1; }
            }
            catch (Exception ex) { App.Log(ex); }
            Settings.Save();
            if (Changed != null) Changed();
        }

        // The Focus menu, as in macOS Control Center: each Focus, and how long it lasts.
        public static ContextMenu Menu()
        {
            var cm = new ContextMenu();
            for (int i = 1; i < Names.Length; i++)
            {
                int mode = i;
                var mi = new MenuItem { Header = Names[i], IsChecked = Settings.Focus == i };
                Add(mi, "On", () => Set(mode, null));
                Add(mi, "For 1 hour", () => Set(mode, DateTime.Now.AddHours(1)));
                var now = DateTime.Now;
                if (now.Hour < 19) Add(mi, "Until this evening", () => Set(mode, DateTime.Today.AddHours(19)));
                Add(mi, "Until tomorrow morning", () => Set(mode, DateTime.Today.AddDays(1).AddHours(7)));
                cm.Items.Add(mi);
            }
            if (Active)
            {
                cm.Items.Add(new Separator());
                var off = new MenuItem { Header = "Turn Off " + Name };
                off.Click += delegate { Set(0, null); };
                cm.Items.Add(off);
            }
            cm.Items.Add(new Separator());
            var settings = new MenuItem { Header = "Focus Settings…" };
            settings.Click += delegate { App.OpenSettings("Focus"); };
            cm.Items.Add(settings);
            return cm;
        }

        static void Add(MenuItem parent, string text, Action a)
        {
            var mi = new MenuItem { Header = text };
            mi.Click += delegate { a(); };
            parent.Items.Add(mi);
        }
    }

    class Banner : Window
    {
        static readonly List<Banner> shown = new List<Banner>();
        const double W = 356, Margin2 = 16;
        readonly Note note;
        readonly TranslateTransform slide = new TranslateTransform();
        readonly Border card = new Border { CornerRadius = new CornerRadius(20), BorderThickness = new Thickness(1) };
        readonly DispatcherTimer life = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5.5) };
        readonly Border close;
        Image iconImage;
        bool leaving;

        public static void Show(Note n)
        {
            var b = new Banner(n);
            shown.Insert(0, b);
            while (shown.Count > 3) shown[shown.Count - 1].Leave();
            Restack();   // its spot before it appears
            b.Show();
            Restack();   // and the others move down by its real height
        }

        public static void IconLoaded(Note n)
        {
            foreach (var b in shown) if (b.note == n && b.iconImage != null) b.iconImage.Source = n.Icon;
        }

        static void Restack()
        {
            var area = DisplayLayout.Bounds;
            double top = area.Top + (Settings.ShowMenuBar ? MenuBarWindow.H : 0) + 6 - Margin2;
            foreach (var b in shown.ToList())
            {
                if (b.leaving) continue;
                double left = area.Right - W - 8 - Margin2;
                b.Left = left;
                if (!b.IsVisible) b.Top = top;
                else if (Math.Abs(b.Top - top) > 0.5) b.BeginAnimation(TopProperty, new DoubleAnimation(top, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                top += (b.IsVisible && b.ActualHeight > 2 * Margin2 ? b.ActualHeight - 2 * Margin2 : 72) + 8;
            }
        }

        Banner(Note n)
        {
            note = n;
            Title = "OpenDock Notification";
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false; Topmost = true;
            ShowActivated = false; ResizeMode = ResizeMode.NoResize; SizeToContent = SizeToContent.Height; Width = W + 2 * Margin2;
            WindowStartupLocation = WindowStartupLocation.Manual; FontFamily = Theme.Font;
            bool dark = Theme.Dark;
            var fg = dark ? Brushes.White : Theme.B("#E6000000");
            var dim = dark ? Theme.B("#A6FFFFFF") : Theme.B("#8C000000");
            card.BorderBrush = new LinearGradientBrush(new GradientStopCollection {
                new GradientStop(Color.FromArgb(dark ? (byte)0x80 : (byte)0xF0, 255, 255, 255), 0), new GradientStop(Color.FromArgb(0x18, 255, 255, 255), 0.5), new GradientStop(Color.FromArgb(dark ? (byte)0x50 : (byte)0xB0, 255, 255, 255), 1) },
                new Point(0, 0), new Point(1, 1));
            card.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = dark ? 0.35 : 0.18 };
            card.Margin = new Thickness(Margin2);
            card.RenderTransform = slide;

            var g = new Grid { Margin = new Thickness(12, 11, 14, 12) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            FrameworkElement icon;
            if (n.Icon != null || n.Glyph == null)
            {
                iconImage = new Image { Source = n.Icon, Width = 36, Height = 36, VerticalAlignment = VerticalAlignment.Top };
                RenderOptions.SetBitmapScalingMode(iconImage, BitmapScalingMode.HighQuality);
                icon = iconImage;
            }
            else icon = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(9), Background = new SolidColorBrush(n.Tint), VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = n.Glyph, FontFamily = Theme.Glyphs, FontSize = 18, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            icon.Margin = new Thickness(0, 1, 11, 0);
            g.Children.Add(icon);

            var words = new StackPanel();
            Grid.SetColumn(words, 1);
            var head = new DockPanel();
            var when = new TextBlock { Text = "now", FontSize = 11.5, Foreground = dim };
            DockPanel.SetDock(when, Dock.Right);
            head.Children.Add(when);
            head.Children.Add(new TextBlock { Text = n.App ?? "", FontSize = 11.5, Foreground = dim, TextTrimming = TextTrimming.CharacterEllipsis });
            words.Children.Add(head);
            words.Children.Add(new TextBlock { Text = n.Title ?? "", FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = fg, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
            if (!string.IsNullOrEmpty(n.Body))
                words.Children.Add(new TextBlock { Text = n.Body, FontSize = 13, Foreground = fg, TextWrapping = TextWrapping.Wrap, MaxHeight = 36, TextTrimming = TextTrimming.CharacterEllipsis });
            g.Children.Add(words);

            // macOS' close button sits on the top-left corner, shown while you hover.
            close = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Opacity = 0, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(-6, -6, 0, 0), Background = dark ? Theme.B("#F23A3A3C") : Theme.B("#F2F4F4F6"), BorderBrush = dim, BorderThickness = new Thickness(0.5), Cursor = Cursors.Hand,
                Child = new TextBlock { Text = "", FontFamily = Theme.Glyphs, FontSize = 8, Foreground = fg, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            close.MouseLeftButtonUp += (s, e) => { e.Handled = true; Leave(); };
            var layers = new Grid();
            layers.Children.Add(g); layers.Children.Add(close);
            card.Child = layers;
            Content = card;

            card.MouseEnter += delegate { life.Stop(); close.Opacity = 1; };
            card.MouseLeave += delegate { close.Opacity = 0; life.Start(); };
            card.MouseLeftButtonUp += delegate { Leave(); if (note.Open != null) try { note.Open(); } catch (Exception ex) { App.Log(ex); } };
            life.Tick += delegate { Leave(); };
            SourceInitialized += delegate { Native.MakeToolWindow(new WindowInteropHelper(this).Handle, true); };
            Loaded += delegate
            {
                // Frosted glass from the wallpaper (no screen capture: a banner must never flicker).
                var tint = Theme.Tint(dark ? "#B82A2A2D" : "#C7F6F6F8");
                card.Background = Glass.WallpaperPane(tint, new Rect(Left + Margin2, Top + Margin2, W, Math.Max(1, ActualHeight - 2 * Margin2)));
                slide.X = W + 40;
                slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(W + 40, 0, TimeSpan.FromMilliseconds(460)) { EasingFunction = new BackEase { Amplitude = 0.28, EasingMode = EasingMode.EaseOut } });
                life.Start();
            };
            Closed += delegate { life.Stop(); };
        }

        void Leave()
        {
            if (leaving) return;
            leaving = true;
            life.Stop();
            shown.Remove(this);
            var out1 = new DoubleAnimation(W + 40, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            out1.Completed += delegate { Close(); };
            slide.BeginAnimation(TranslateTransform.XProperty, out1);
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)));
            Restack();
        }
    }
}
