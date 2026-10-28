using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace OpenDock
{
    // Full-screen app grid. A screenshot taken just before opening is shown sharp, then a blurred copy fades in
    // over it, so the "desktop blurs away" effect is smooth without any DWM tricks.
    class LaunchpadWindow : Window
    {
        public bool IsOpen;
        readonly Image sharp = new Image { Stretch = Stretch.Fill }, blurred = new Image { Stretch = Stretch.Fill };
        readonly Border tint = new Border { Background = new SolidColorBrush(Color.FromArgb(105, 0, 0, 0)) };
        readonly Grid content = new Grid { RenderTransformOrigin = new Point(0.5, 0.5) };
        readonly ScaleTransform zoom = new ScaleTransform(1, 1);
        readonly TextBox search = new TextBox();
        readonly TextBlock hint = new TextBlock { Text = "Search", IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), FontSize = 14 };
        readonly StackPanel strip = new StackPanel { Orientation = Orientation.Horizontal };
        readonly TranslateTransform slide = new TranslateTransform();
        readonly StackPanel dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        const int Cols = 7, Rows = 5;
        double W, H;
        int page, pages;
        bool stale = true;
        Point? pressAt; AppEntry pressedApp; Image pressedImg; bool swiping;
        DateTime lastWheel;

        public LaunchpadWindow()
        {
            Title = "Launchpad";
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
            Background = Brushes.Black; FontFamily = Theme.Font;
            RenderOptions.SetBitmapScalingMode(content, BitmapScalingMode.HighQuality);

            var searchBox = new Border
            {
                Width = 250, Height = 32, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 46, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), BorderBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)), BorderThickness = new Thickness(1)
            };
            var sg = new Grid { Margin = new Thickness(10, 0, 10, 0) };
            sg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            sg.ColumnDefinitions.Add(new ColumnDefinition());
            sg.Children.Add(new TextBlock { Text = "", FontFamily = Theme.Glyphs, FontSize = 13, Foreground = hint.Foreground, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            search.Background = Brushes.Transparent; search.BorderThickness = new Thickness(0); search.Foreground = Brushes.White; search.CaretBrush = Brushes.White;
            search.FontSize = 14; search.VerticalContentAlignment = VerticalAlignment.Center;
            search.TextChanged += delegate { hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Hidden; Build(); };
            Grid.SetColumn(search, 1); Grid.SetColumn(hint, 1);
            sg.Children.Add(hint); sg.Children.Add(search);
            searchBox.Child = sg;

            var host = new Canvas { ClipToBounds = false };
            strip.RenderTransform = slide;
            host.Children.Add(strip);
            content.RenderTransform = zoom;
            content.Children.Add(host);
            content.Children.Add(searchBox);
            content.Children.Add(dots);

            var root = new Grid();
            root.Children.Add(sharp); root.Children.Add(blurred); root.Children.Add(tint); root.Children.Add(content);
            Content = root;

            Deactivated += delegate { Close2(null, false); };
            PreviewKeyDown += OnKey;
            PreviewMouseWheel += (s, e) =>
            {
                if ((DateTime.Now - lastWheel).TotalMilliseconds < 350) return;
                lastWheel = DateTime.Now;
                Go(page + (e.Delta < 0 ? 1 : -1));
            };
            root.PreviewMouseLeftButtonDown += OnDown;
            root.PreviewMouseMove += OnMove;
            root.PreviewMouseLeftButtonUp += OnUp;
            SourceInitialized += delegate
            {
                var h = new WindowInteropHelper(this).Handle;
                Native.MakeToolWindow(h, false);
                int pref = 1; Native.DwmSetWindowAttribute(h, 33 /* corner preference: don't round */, ref pref, 4);
            };
            Apps.Changed += delegate { stale = true; if (IsOpen) Build(); };
            Settings.Changed += delegate { stale = true; };
            Theme.Changed += delegate { stale = true; };
        }

        public void Open()
        {
            if (IsOpen) return;
            var bounds = DisplayLayout.ActiveBounds;
            W = bounds.Width; H = bounds.Height;
            Left = bounds.Left; Top = bounds.Top; Width = W; Height = H;
            var shot = Icons.CaptureScreen((int)Math.Round(Left * App.Dpi), (int)Math.Round(Top * App.Dpi), (int)Math.Round(W * App.Dpi), (int)Math.Round(H * App.Dpi));
            sharp.Source = shot;
            blurred.Source = Icons.Blur(shot, (int)(W / 5), (int)(H / 5), 7);
            if (search.Text.Length > 0) { search.Text = ""; stale = true; }
            page = 0;
            Apps.Refresh();
            if (stale || Math.Abs(strip.Children.Count > 0 ? ((FrameworkElement)strip.Children[0]).Width - W : 1) > 0.5) Build(); else Go(0, false);
            blurred.Opacity = 0; tint.Opacity = 0; content.Opacity = 0;
            IsOpen = true;
            Show();
            Activate();
            search.Focus();
            App.Dock.BringToFront();

            var d = TimeSpan.FromMilliseconds(380);
            var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
            blurred.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)));
            tint.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)));
            content.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d) { EasingFunction = ease });
            zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.12, 1, d) { EasingFunction = ease });
            zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.12, 1, d) { EasingFunction = ease });
        }

        public void Close2(AppEntry launch, bool refocus = true)
        {
            if (!IsOpen) return;
            IsOpen = false;
            if (launch != null) Launcher.Open(launch.Target);
            else if (refocus && App.LastForeground != IntPtr.Zero && Native.IsWindow(App.LastForeground)) Native.Activate(App.LastForeground);
            var d = TimeSpan.FromMilliseconds(240);
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            blurred.BeginAnimation(OpacityProperty, new DoubleAnimation(0, d));
            tint.BeginAnimation(OpacityProperty, new DoubleAnimation(0, d));
            zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.08, d) { EasingFunction = ease });
            zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.08, d) { EasingFunction = ease });
            var fade = new DoubleAnimation(0, d);
            fade.Completed += delegate { if (!IsOpen) { Hide(); sharp.Source = blurred.Source = null; } };
            content.BeginAnimation(OpacityProperty, fade);
        }

        public void Invalidate() { stale = true; }

        void Build()
        {
            stale = false;
            string q = search.Text.Trim();
            var visible = Apps.All.Where(a => !Settings.Hidden.Contains(a.Id)).ToList();
            var apps = q.Length == 0 ? visible : visible.Where(a => a.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0)
                .OrderBy(a => a.Name.StartsWith(q, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1).ToList();
            double gridW = Math.Min(W * 0.8, H * 1.55), gridH = H - 130 - (Settings.IconSize + 70);
            double cellW = gridW / Cols, cellH = gridH / Rows;
            double icon = Math.Max(48, Math.Min(116, Math.Min(cellW * 0.56, cellH * 0.62)));
            int per = Cols * Rows;
            pages = Math.Max(1, (apps.Count + per - 1) / per);
            strip.Children.Clear();
            for (int p = 0; p < pages; p++)
            {
                var grid = new UniformGrid { Columns = Cols, Rows = Rows, Width = gridW, Height = gridH, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center };
                foreach (var a in apps.Skip(p * per).Take(per)) grid.Children.Add(Tile(a, icon, cellW));
                strip.Children.Add(new Grid { Width = W, Height = H, Children = { new Border { Child = grid, Margin = new Thickness(0, 110, 0, 0), VerticalAlignment = VerticalAlignment.Top } } });
            }
            dots.Children.Clear();
            dots.VerticalAlignment = VerticalAlignment.Top;
            dots.Margin = new Thickness(0, 110 + gridH + 8, 0, 0);
            for (int p = 0; p < pages && pages > 1; p++)
            {
                int target = p;
                var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(5), Fill = Brushes.White, Cursor = Cursors.Hand };
                dot.MouseLeftButtonUp += (s, e) => { Go(target); e.Handled = true; };
                dots.Children.Add(dot);
            }
            page = Math.Min(page, pages - 1);
            Go(page, false);
        }

        FrameworkElement Tile(AppEntry a, double size, double cellW)
        {
            var img = new Image { Width = size, Height = size, Opacity = 0 };
            Icons.Get(CustomIcons.Get(a.Target) ?? a.Icon, true, s => { img.Source = s; img.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180))); }, 192);
            var name = new TextBlock
            {
                Text = a.Name, Foreground = Brushes.White, FontSize = 13, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(6, 6, 6, 0), Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = 0.6 }
            };
            var cell = new StackPanel { Width = cellW, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, Tag = a };
            cell.Children.Add(img); cell.Children.Add(name);
            cell.MouseRightButtonUp += (s, e) =>
            {
                var cm = new ContextMenu();
                var open = new MenuItem { Header = "Open" }; open.Click += delegate { Close2(a); }; cm.Items.Add(open);
                var pin = new MenuItem { Header = "Add to Dock" }; pin.Click += delegate { App.Dock.Pin(a); }; cm.Items.Add(pin);
                var hide = new MenuItem { Header = "Hide from Launchpad" };
                hide.Click += delegate { Settings.Hidden.Add(a.Id); Settings.Save(); Build(); };
                cm.Items.Add(hide);
                if (a.Exe != null) { var show = new MenuItem { Header = "Show in Finder" }; show.Click += delegate { Close2(null, false); App.ShowInFinder(a.Exe); }; cm.Items.Add(show); }
                cm.IsOpen = true;
                e.Handled = true;
            };
            return cell;
        }

        void Go(int p, bool animate = true)
        {
            page = Math.Max(0, Math.Min(pages - 1, p));
            for (int i = 0; i < dots.Children.Count; i++) ((Ellipse)dots.Children[i]).Opacity = i == page ? 1 : 0.35;
            double to = -page * W;
            if (!animate) { slide.BeginAnimation(TranslateTransform.XProperty, null); slide.X = to; return; }
            slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(450)) { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } });
        }

        void OnKey(object s, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { if (search.Text.Length > 0) search.Text = ""; else Close2(null); e.Handled = true; }
            else if (e.Key == Key.Right && search.Text.Length == 0) { Go(page + 1); e.Handled = true; }
            else if (e.Key == Key.Left && search.Text.Length == 0) { Go(page - 1); e.Handled = true; }
            else if (e.Key == Key.Enter)
            {
                var first = strip.Children.Count > 0 ? ((Panel)((Border)((Grid)strip.Children[page]).Children[0]).Child).Children.OfType<FrameworkElement>().FirstOrDefault() : null;
                if (first != null) Close2((AppEntry)first.Tag);
                e.Handled = true;
            }
        }

        // One gesture handler for click-to-launch, click-empty-to-close and swipe-between-pages.
        void OnDown(object s, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is TextBox || IsInside<TextBox>(e.OriginalSource as DependencyObject) || IsInside<Ellipse>(e.OriginalSource as DependencyObject)) return;
            pressAt = e.GetPosition(this);
            swiping = false;
            var cell = Ancestor(e.OriginalSource as DependencyObject);
            pressedApp = cell != null ? (AppEntry)cell.Tag : null;
            pressedImg = cell != null ? (Image)((StackPanel)cell).Children[0] : null;
            if (pressedImg != null) pressedImg.Opacity = 0.6;
            ((UIElement)s).CaptureMouse();
        }

        void OnMove(object s, MouseEventArgs e)
        {
            if (pressAt == null) return;
            double dx = e.GetPosition(this).X - pressAt.Value.X;
            if (!swiping && Math.Abs(dx) > 8) { swiping = true; if (pressedImg != null) pressedImg.Opacity = 1; }
            if (!swiping) return;
            bool edgePage = (page == 0 && dx > 0) || (page == pages - 1 && dx < 0);
            slide.BeginAnimation(TranslateTransform.XProperty, null);
            slide.X = -page * W + (edgePage ? dx * 0.3 : dx);
        }

        void OnUp(object s, MouseButtonEventArgs e)
        {
            if (pressAt == null) return;
            double dx = e.GetPosition(this).X - pressAt.Value.X;
            pressAt = null;
            ((UIElement)s).ReleaseMouseCapture();
            if (pressedImg != null) pressedImg.Opacity = 1;
            if (swiping) Go(Math.Abs(dx) > W * 0.08 ? page + (dx < 0 ? 1 : -1) : page);
            else if (pressedApp != null) Close2(pressedApp);
            else Close2(null);
        }

        static FrameworkElement Ancestor(DependencyObject d)
        {
            while (d != null && !(d is FrameworkElement && ((FrameworkElement)d).Tag is AppEntry)) d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            return d as FrameworkElement;
        }

        static bool IsInside<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null) { if (d is T) return true; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d); }
            return false;
        }
    }
}
