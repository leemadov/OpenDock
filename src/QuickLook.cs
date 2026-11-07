using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace OpenDock
{
    // Quick Look, as in the Mac's Finder: select a file and press Space to see it -- pictures, videos and music, text
    // and code, and for anything else the big preview Windows makes of it (PDF pages, documents) -- in a floating
    // window over the Finder. Space or Esc closes it; the arrow keys keep moving through the files and the preview
    // follows, because the window never takes the keyboard from the Finder.
    class QuickLook : Window
    {
        static QuickLook open;
        static readonly string[] Pictures = { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".ico", ".webp", ".heic", ".jfif" };
        static readonly string[] Media = { ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".mp3", ".m4a", ".wav", ".wma", ".aac", ".flac" };
        static readonly string[] Texts = { ".txt", ".md", ".log", ".ini", ".cfg", ".csv", ".json", ".xml", ".yml", ".yaml", ".cs", ".js", ".ts", ".py", ".html", ".css", ".c", ".cpp", ".h", ".java", ".go", ".rs", ".ps1", ".bat", ".sh", ".sql", ".toml", ".gitignore" };

        readonly Window owner;
        readonly Grid stage = new Grid();
        readonly TextBlock name = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        readonly Border frame;
        MediaElement player;
        string shown;

        public static bool IsOpen { get { return open != null; } }

        // Space in the Finder: open on `path`, or close if it's already open.
        public static void Toggle(Window owner, string path)
        {
            if (open != null) { open.Leave(); return; }
            if (string.IsNullOrEmpty(path)) return;
            open = new QuickLook(owner);
            open.Show(path);
        }

        // The Finder's selection moved: the preview follows.
        public static void Follow(string path) { if (open != null && !string.IsNullOrEmpty(path)) open.Show(path); }
        public static void CloseIfOpen() { if (open != null) open.Leave(); }

        QuickLook(Window owner)
        {
            this.owner = owner;
            Owner = owner;
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false; ShowActivated = false; Focusable = false; FontFamily = Theme.Font; Title = "Quick Look";
            var bar = new DockPanel { Height = 40, Margin = new Thickness(14, 0, 10, 0) };
            var close = Button("", "Close", Leave); DockPanel.SetDock(close, Dock.Left);
            var openWith = Button("", "Open", () => { if (shown != null) Launcher.Open(shown); Leave(); }); DockPanel.SetDock(openWith, Dock.Right);
            bar.Children.Add(close); bar.Children.Add(openWith); bar.Children.Add(name);
            name.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            var layout = new DockPanel();
            DockPanel.SetDock(bar, Dock.Top);
            layout.Children.Add(bar); layout.Children.Add(stage);
            frame = new Border { CornerRadius = new CornerRadius(14), Child = layout, Margin = new Thickness(30), BorderThickness = new Thickness(1), ClipToBounds = true,
                Effect = new DropShadowEffect { BlurRadius = 34, ShadowDepth = 8, Direction = 270, Opacity = 0.35 } };
            frame.SetResourceReference(Border.BackgroundProperty, "WinBg");
            frame.SetResourceReference(Border.BorderBrushProperty, "MenuBorder");
            Content = frame;
            MouseLeftButtonDown += (s, e) => { if (e.OriginalSource == frame || e.OriginalSource is TextBlock) try { DragMove(); } catch { } };
            Closed += delegate { if (player != null) player.Close(); if (open == this) open = null; };
            SourceInitialized += delegate { Native.MakeToolWindow(new WindowInteropHelper(this).Handle, true); };
        }

        static FrameworkElement Button(string glyph, string tip, Action click)
        {
            var b = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = tip, VerticalAlignment = VerticalAlignment.Center };
            var t = new TextBlock { Text = glyph, FontFamily = Theme.Glyphs, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            b.Child = t;
            b.MouseEnter += delegate { b.SetResourceReference(Border.BackgroundProperty, "Hover"); };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; };
            b.MouseLeftButtonUp += (s, e) => { e.Handled = true; click(); };
            return b;
        }

        void Show(string path)
        {
            if (path == shown) return;
            shown = path;
            name.Text = System.IO.Path.GetFileName(path.TrimEnd('\\')); if (name.Text.Length == 0) name.Text = path;
            if (player != null) { player.Close(); player = null; }
            stage.Children.Clear();
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            Size want;
            try
            {
                if (Pictures.Contains(ext) && File.Exists(path)) want = ShowPicture(path);
                else if (Media.Contains(ext) && File.Exists(path)) want = ShowMedia(path, ext);
                else if (Texts.Contains(ext) && File.Exists(path)) want = ShowText(path);
                else want = ShowPreview(path);
            }
            catch (Exception ex) { App.Log(ex); want = ShowPreview(path); }
            Place(want);
            if (!IsVisible)
            {
                Opacity = 0; base.Show();
                var zoom = new ScaleTransform(0.92, 0.92); frame.RenderTransform = zoom; frame.RenderTransformOrigin = new Point(0.5, 0.5);
                var time = TimeSpan.FromMilliseconds(200);
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, time) { EasingFunction = ease });
                zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, time) { EasingFunction = ease });
                BeginAnimation(OpacityProperty, new DoubleAnimation(1, time));
            }
        }

        Size ShowPicture(string path)
        {
            var bi = new BitmapImage(); bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.UriSource = new Uri(path); bi.DecodePixelWidth = 1800; bi.EndInit(); bi.Freeze();
            var img = new Image { Source = bi, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            stage.Children.Add(img);
            return new Size(bi.PixelWidth, bi.PixelHeight);
        }

        Size ShowMedia(string path, string ext)
        {
            bool audio = new[] { ".mp3", ".m4a", ".wav", ".wma", ".aac", ".flac" }.Contains(ext);
            player = new MediaElement { Source = new Uri(path), LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.Uniform };
            player.MediaEnded += delegate { player.Position = TimeSpan.Zero; player.Play(); };
            if (audio)
            {
                // music: its icon (or cover) big, and the sound playing
                var g = new Grid();
                g.Children.Add(player);
                var icon = new Image { Width = 220, Height = 220 };
                Icons.Get(path, false, i => icon.Source = i, 512, false);
                g.Children.Add(icon);
                stage.Children.Add(g);
                player.Play();
                return new Size(520, 360);
            }
            stage.Children.Add(player);
            player.MediaOpened += delegate { if (player != null && player.NaturalVideoWidth > 0) Place(new Size(player.NaturalVideoWidth, player.NaturalVideoHeight)); };
            player.Play();
            return new Size(960, 540);
        }

        Size ShowText(string path)
        {
            string text;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs, Encoding.UTF8, true))
            {
                var buf = new char[200000];
                int n = sr.ReadBlock(buf, 0, buf.Length);
                text = new string(buf, 0, n) + (sr.EndOfStream ? "" : "\n…");
            }
            var t = new TextBox { Text = text, IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13, BorderThickness = new Thickness(0),
                Background = Brushes.Transparent, Padding = new Thickness(18, 6, 18, 14), TextWrapping = TextWrapping.NoWrap, Focusable = false,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
            t.SetResourceReference(TextBox.ForegroundProperty, "Fg");
            stage.Children.Add(t);
            return new Size(820, 620);
        }

        // Anything else: the large preview Windows draws (a PDF's first page, a document's), with its name and details.
        Size ShowPreview(string path)
        {
            var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24) };
            var img = new Image { Width = 380, Height = 380, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            Icons.Get(path, false, i => img.Source = i, 768, false);
            panel.Children.Add(img);
            string details = "";
            try
            {
                if (Directory.Exists(path)) details = "Folder · " + new DirectoryInfo(path).EnumerateFileSystemInfos().Take(10000).Count() + " items";
                else { var fi = new FileInfo(path); details = Sys.Size(fi.Length) + " · Modified " + fi.LastWriteTime.ToString("g"); }
            }
            catch { }
            var d = new TextBlock { Text = details, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
            d.SetResourceReference(TextBlock.ForegroundProperty, "FgDim");
            panel.Children.Add(d);
            stage.Children.Add(panel);
            return new Size(520, 520);
        }

        // Sized to the content (within most of the screen) and centred over the Finder.
        void Place(Size content)
        {
            var b = DisplayLayout.Dip(DisplayLayout.Selected.Bounds);
            double maxW = b.Width * 0.62, maxH = b.Height * 0.72 - 40;
            double k = Math.Min(1, Math.Min(maxW / Math.Max(1, content.Width), maxH / Math.Max(1, content.Height)));
            double w = Math.Max(360, content.Width * k), h = Math.Max(240, content.Height * k) + 40;
            Width = w + 60; Height = h + 60;
            double cx = owner != null ? owner.Left + owner.ActualWidth / 2 : b.Left + b.Width / 2, cy = owner != null ? owner.Top + owner.ActualHeight / 2 : b.Top + b.Height / 2;
            Left = Math.Max(b.Left, Math.Min(b.Right - Width, cx - Width / 2)); Top = Math.Max(b.Top, Math.Min(b.Bottom - Height, cy - Height / 2));
        }

        void Leave()
        {
            if (open == this) open = null;
            if (player != null) player.Pause();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
            fade.Completed += delegate { Close(); };
            BeginAnimation(OpacityProperty, fade);
        }
    }
}
