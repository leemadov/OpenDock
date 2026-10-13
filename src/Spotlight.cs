using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Data.OleDb;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace OpenDock
{
    // A row in Spotlight or a file in Finder.
    class Entry : INotifyPropertyChanged
    {
        public string Name { get; set; }
        public string Sub { get; set; }
        public string Path;
        public bool IsDir;
        public Action Open;
        ImageSource icon;
        public ImageSource Icon { get { return icon; } set { icon = value; if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("Icon")); } }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    class SpotlightWindow : Window
    {
        const double PanelW = 680, Pad = 40, MaxH = 560;
        readonly TextBox box = new TextBox();
        readonly TextBlock hint = new TextBlock { Text = "Spotlight Search", FontSize = 22, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
        readonly ListBox list = new ListBox();
        readonly Border divider = new Border { Height = 1, Visibility = Visibility.Collapsed };
        readonly ObservableCollection<Entry> results = new ObservableCollection<Entry>();
        readonly ImageBrush backdrop = new ImageBrush { ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill, AlignmentY = AlignmentY.Top };
        readonly Border panel;
        readonly ScaleTransform zoom = new ScaleTransform(1, 1);
        int query;
        bool closing;

        public SpotlightWindow()
        {
            Title = "Spotlight";
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
            ShowInTaskbar = false; Topmost = true; ResizeMode = ResizeMode.NoResize;
            Width = PanelW + Pad * 2; Height = MaxH + Pad * 2; FontFamily = Theme.Font;

            var glass = new TextBlock { Text = "", FontFamily = Theme.Glyphs, FontSize = 20, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            glass.SetResourceReference(TextBlock.ForegroundProperty, "FgDim");
            hint.SetResourceReference(TextBlock.ForegroundProperty, "FgDim");
            box.FontSize = 22; box.Background = Brushes.Transparent; box.BorderThickness = new Thickness(0);
            box.SetResourceReference(ForegroundProperty, "Fg");
            box.SetResourceReference(TextBox.CaretBrushProperty, "Fg");
            box.TextChanged += delegate { hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Hidden; Search(box.Text.Trim()); };
            box.PreviewKeyDown += OnKey;
            var row = new Grid { Margin = new Thickness(18, 12, 18, 12) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(box, 1); Grid.SetColumn(hint, 1);
            row.Children.Add(glass); row.Children.Add(hint); row.Children.Add(box);

            divider.SetResourceReference(Border.BackgroundProperty, "MenuSep");
            list.ItemsSource = results;
            list.ItemTemplate = (DataTemplate)Application.Current.Resources["SpotRow"];
            list.ItemContainerStyle = (Style)Application.Current.Resources["Bare"];
            list.Background = Brushes.Transparent; list.BorderThickness = new Thickness(0);
            list.MaxHeight = MaxH - 70; list.Padding = new Thickness(0, 6, 0, 8);
            list.Focusable = false;
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            list.PreviewMouseLeftButtonUp += (s, e) => { var en = list.SelectedItem as Entry; if (en != null) Run(en); };

            var stack = new StackPanel();
            stack.Children.Add(row); stack.Children.Add(divider); stack.Children.Add(list);
            var tintLayer = new Border { CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), Child = stack };
            tintLayer.SetResourceReference(Border.BackgroundProperty, "Glass");
            tintLayer.SetResourceReference(Border.BorderBrushProperty, "MenuBorder");
            panel = new Border
            {
                CornerRadius = new CornerRadius(16), Background = backdrop, Child = tintLayer, Width = PanelW, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(Pad),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 40, ShadowDepth = 10, Direction = 270, Opacity = 0.4 },
                RenderTransform = zoom, RenderTransformOrigin = new Point(0.5, 0.3)
            };
            Content = panel;
            Deactivated += delegate { Close2(false); };
            SourceInitialized += delegate { Native.MakeToolWindow(new WindowInteropHelper(this).Handle, false); };
        }

        public void Open()
        {
            closing = false;
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = SystemParameters.PrimaryScreenHeight * 0.2 - Pad;
            // Frosted glass: blur what's behind the panel right now.
            int px = (int)Math.Round((Left + Pad) * App.Dpi), py = (int)Math.Round((Top + Pad) * App.Dpi);
            var shot = Icons.CaptureScreen(px, py, (int)Math.Round(PanelW * App.Dpi), (int)Math.Round(MaxH * App.Dpi));
            backdrop.ImageSource = Icons.Blur(shot, (int)(PanelW / 3), (int)(MaxH / 3), 8);
            backdrop.Viewport = new Rect(0, 0, PanelW, MaxH);
            box.Text = "";
            Apps.Refresh();
            panel.Opacity = 0;
            Show();
            Activate();
            box.Focus();
            Keyboard.Focus(box);
            var d = TimeSpan.FromMilliseconds(170);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d));
            zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, d) { EasingFunction = ease });
            zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, d) { EasingFunction = ease });
        }

        public void Close2(bool refocus = true)
        {
            if (!IsVisible || closing) return;
            closing = true;
            if (refocus && App.LastForeground != IntPtr.Zero && Native.IsWindow(App.LastForeground)) Native.Activate(App.LastForeground);
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
            fade.Completed += delegate { if (closing) Hide(); };
            panel.BeginAnimation(OpacityProperty, fade);
        }

        void Run(Entry e)
        {
            Close2(false);
            if (e.Open != null) e.Open();
        }

        void OnKey(object s, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { if (box.Text.Length > 0) box.Text = ""; else Close2(); e.Handled = true; }
            else if (e.Key == Key.Down || e.Key == Key.Up)
            {
                if (results.Count == 0) return;
                list.SelectedIndex = (list.SelectedIndex + (e.Key == Key.Down ? 1 : -1) + results.Count) % results.Count;
                list.ScrollIntoView(list.SelectedItem);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                var en = list.SelectedItem as Entry;
                if (en != null)
                {
                    if (Keyboard.Modifiers == ModifierKeys.Control && en.Path != null) { Close2(false); App.ShowInFinder(en.Path); }
                    else Run(en);
                }
                e.Handled = true;
            }
        }

        static readonly Regex math = new Regex(@"^[\d\s\.\+\-\*/\(\)%]+$");

        void Search(string q)
        {
            int id = ++query;
            results.Clear();
            if (q.Length == 0) { divider.Visibility = Visibility.Collapsed; return; }

            if (math.IsMatch(q) && q.Any(char.IsDigit) && q.IndexOfAny("+-*/%".ToCharArray()) > 0)
            {
                try
                {
                    var v = Convert.ToDouble(new DataTable().Compute(q, ""));
                    if (!double.IsInfinity(v) && !double.IsNaN(v))
                    {
                        string text = v.ToString("0.##########");
                        var calc = Apps.All.FirstOrDefault(a => a.Name == "Calculator");
                        Add(new Entry { Name = "= " + text, Sub = "Calculator — Enter to copy", Open = () => Clipboard.SetText(text) }, calc != null ? calc.Icon : null, true);
                    }
                }
                catch { }
            }

            var apps = Apps.All.Where(a => a.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0)
                .OrderBy(a => a.Name.StartsWith(q, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1).ThenBy(a => a.Name.Length).Take(6);
            foreach (var a in apps)
            {
                var app = a;
                Add(new Entry { Name = a.Name, Sub = "Application", Path = a.Exe, Open = () => Launcher.Open(app.Target) }, a.Icon, true);
            }
            Add(new Entry { Name = "Search the web for “" + q + "”", Sub = "Web", Open = () => Launcher.Shell("https://www.google.com/search?q=" + Uri.EscapeDataString(q)) }, null, false);
            list.SelectedIndex = 0;
            divider.Visibility = Visibility.Visible;

            Task.Run(() => FindFiles(q)).ContinueWith(t =>
            {
                if (id != query || t.Result == null) return;
                int at = results.Count - 1; // keep "search the web" last
                foreach (var f in t.Result)
                {
                    var path = f;
                    var en = new Entry { Name = System.IO.Path.GetFileName(f), Sub = ShortDir(f), Path = f, Open = () => { if (Directory.Exists(path)) App.NewFinder(path); else Launcher.Open(path); } };
                    results.Insert(at++, en);
                    Icons.Get(f, false, img => en.Icon = img, 64, false);
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void Add(Entry e, string iconPath, bool tile)
        {
            results.Add(e);
            if (iconPath != null) Icons.Get(iconPath, tile, img => e.Icon = img, 96);
        }

        static string ShortDir(string f)
        {
            string dir = System.IO.Path.GetDirectoryName(f) ?? "", home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return dir.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + dir.Substring(home.Length) : dir;
        }

        // Windows Search index: instant filename lookup without crawling the disk ourselves.
        static List<string> FindFiles(string q)
        {
            var found = new List<string>();
            try
            {
                string like = q.Replace("'", "''").Replace("%", "[%]").Replace("_", "[_]");
                string sql = "SELECT TOP 8 System.ItemPathDisplay FROM SystemIndex WHERE SCOPE='file:' AND System.ItemNameDisplay LIKE '%" + like + "%' ORDER BY System.DateModified DESC";
                using (var c = new OleDbConnection("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';"))
                {
                    c.Open();
                    using (var cmd = new OleDbCommand(sql, c))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) if (!r.IsDBNull(0)) found.Add(r.GetString(0));
                }
            }
            catch { }
            return found;
        }
    }
}
