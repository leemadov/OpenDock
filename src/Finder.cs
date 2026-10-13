using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using VB = Microsoft.VisualBasic.FileIO;

namespace OpenDock
{
    // Borderless window with macOS traffic lights; Windows 11 still supplies rounded corners, shadow and snapping.
    class MacWindow : Window
    {
        protected readonly Grid Layout = new Grid();
        readonly List<Ellipse> lights = new List<Ellipse>();
        readonly List<TextBlock> marks = new List<TextBlock>();
        static readonly string[] On = { "#FF5F57", "#FEBC2E", "#28C840" }, Rim = { "#E2463F", "#E1A116", "#14AE2C" };

        public MacWindow()
        {
            WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 52, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0, 0, 0, 1), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false });
            FontFamily = Theme.Font;
            SetResourceReference(BackgroundProperty, "WinBg");
            Content = Layout;
            SourceInitialized += delegate
            {
                var h = new WindowInteropHelper(this).Handle;
                int round = 2; Native.DwmSetWindowAttribute(h, 33, ref round, 4);
                int dark = Theme.Dark ? 1 : 0; Native.DwmSetWindowAttribute(h, 20, ref dark, 4);
            };
            StateChanged += delegate { Layout.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0); };
            Activated += delegate { Paint(true); };
            Deactivated += delegate { Paint(false); };
        }

        protected FrameworkElement TrafficLights()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 20, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Background = Brushes.Transparent };
            WindowChrome.SetIsHitTestVisibleInChrome(row, true);
            string[] glyphs = { "×", "−", "+" };
            Action[] acts = { Close, () => WindowState = WindowState.Minimized, () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized };
            for (int i = 0; i < 3; i++)
            {
                var act = acts[i];
                var e = new Ellipse { Width = 12, Height = 12, StrokeThickness = 0.8 };
                var t = new TextBlock { Text = glyphs[i], FontSize = 11, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromArgb(160, 60, 0, 0)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0), Opacity = 0, IsHitTestVisible = false };
                var g = new Grid { Width = 12, Height = 12, Margin = new Thickness(0, 0, 8, 0), Background = Brushes.Transparent };
                g.Children.Add(e); g.Children.Add(t);
                g.MouseLeftButtonUp += delegate { act(); };
                lights.Add(e); marks.Add(t);
                row.Children.Add(g);
            }
            row.MouseEnter += delegate { foreach (var m in marks) m.Opacity = 1; };
            row.MouseLeave += delegate { foreach (var m in marks) m.Opacity = 0; };
            Paint(true);
            return row;
        }

        void Paint(bool active)
        {
            for (int i = 0; i < lights.Count; i++)
            {
                lights[i].Fill = Theme.B(active ? On[i] : Theme.Dark ? "#4A4A4D" : "#D5D5D7");
                lights[i].Stroke = Theme.B(active ? Rim[i] : Theme.Dark ? "#5A5A5D" : "#C5C5C7");
            }
        }

        protected static TextBlock Text(string s, double size, string brush, FontWeight? w = null)
        {
            var t = new TextBlock { Text = s, FontSize = size, VerticalAlignment = VerticalAlignment.Center, FontWeight = w ?? FontWeights.Normal };
            t.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return t;
        }
    }

    class SideEntry { public string Name { get; set; } public string Glyph { get; set; } public string Path; public bool IsHeader { get; set; } }

    class FinderWindow : MacWindow
    {
        const string AppsLocation = "::apps";
        readonly ListBox side = new ListBox(), files = new ListBox();
        readonly ObservableCollection<Entry> entries = new ObservableCollection<Entry>();
        readonly TextBlock title = Text("", 15, "Fg", FontWeights.Bold), status = Text("", 11, "FgDim");
        readonly TextBox filter = new TextBox();
        readonly Button back, forward;
        readonly Stack<string> history = new Stack<string>(), future = new Stack<string>();
        List<Entry> all = new List<Entry>();
        FileSystemWatcher watcher;
        DispatcherTimer reloadSoon;
        string path, selectAfterLoad;
        Point? dragFrom;
        int loadId;

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] static extern int StrCmpLogicalW(string a, string b);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool SHObjectProperties(IntPtr h, int type, string name, string page);

        public FinderWindow(string start, string select = null)
        {
            Width = 1000; Height = 640; MinWidth = 560; MinHeight = 340;
            int n = App.Finders.Count;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = (SystemParameters.WorkArea.Width - Width) / 2 + n * 28; Top = Math.Max(SystemParameters.WorkArea.Top + 40, (SystemParameters.WorkArea.Height - Height) / 2 + n * 28);

            Layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            Layout.ColumnDefinitions.Add(new ColumnDefinition());

            // sidebar
            var sideBg = new Border();
            sideBg.SetResourceReference(Border.BackgroundProperty, "SideBg");
            var sideGrid = new Grid();
            sideGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) });
            sideGrid.RowDefinitions.Add(new RowDefinition());
            sideGrid.Children.Add(TrafficLights());
            side.ItemTemplate = (DataTemplate)Application.Current.Resources["SideRow"];
            side.ItemContainerStyle = (Style)Application.Current.Resources["Bare"];
            side.Background = Brushes.Transparent; side.BorderThickness = new Thickness(0);
            ScrollViewer.SetHorizontalScrollBarVisibility(side, ScrollBarVisibility.Disabled);
            side.ItemsSource = Sidebar();
            side.SelectionChanged += delegate
            {
                var s = side.SelectedItem as SideEntry;
                if (s == null || s.IsHeader) return;
                if (s.Path == "trash") { Launcher.Shell("shell:RecycleBinFolder"); return; }
                if (s.Path != path) Go(s.Path);
            };
            Grid.SetRow(side, 1);
            sideGrid.Children.Add(side);
            sideBg.Child = sideGrid;
            Layout.Children.Add(sideBg);

            // toolbar + content
            var main = new Grid();
            Grid.SetColumn(main, 1);
            main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) });
            main.RowDefinitions.Add(new RowDefinition());
            main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });
            var toolbar = new DockPanel { LastChildFill = true, Margin = new Thickness(10, 0, 14, 0) };
            back = Tool("", () => Back());
            forward = Tool("", () => Forward());
            toolbar.Children.Add(back); toolbar.Children.Add(forward);
            title.Margin = new Thickness(10, 0, 0, 0);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            var searchBox = new Border { Width = 200, Height = 28, CornerRadius = new CornerRadius(7), Padding = new Thickness(8, 0, 8, 0) };
            searchBox.SetResourceReference(Border.BackgroundProperty, "Field");
            DockPanel.SetDock(searchBox, Dock.Right);
            var sg = new Grid();
            sg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            sg.ColumnDefinitions.Add(new ColumnDefinition());
            var mag = Text("", 12, "FgDim"); mag.FontFamily = Theme.Glyphs; mag.Margin = new Thickness(0, 0, 6, 0);
            var ph = Text("Search", 13, "FgDim"); ph.IsHitTestVisible = false;
            filter.Background = Brushes.Transparent; filter.BorderThickness = new Thickness(0); filter.FontSize = 13; filter.VerticalContentAlignment = VerticalAlignment.Center;
            filter.SetResourceReference(ForegroundProperty, "Fg"); filter.SetResourceReference(TextBox.CaretBrushProperty, "Fg");
            filter.TextChanged += delegate { ph.Visibility = filter.Text.Length == 0 ? Visibility.Visible : Visibility.Hidden; ApplyFilter(); };
            Grid.SetColumn(ph, 1); Grid.SetColumn(filter, 1);
            sg.Children.Add(mag); sg.Children.Add(ph); sg.Children.Add(filter);
            searchBox.Child = sg;
            WindowChrome.SetIsHitTestVisibleInChrome(searchBox, true);
            toolbar.Children.Add(searchBox);
            toolbar.Children.Add(title);
            var toolBg = new Border { Child = toolbar, BorderThickness = new Thickness(0, 0, 0, 1) };
            toolBg.SetResourceReference(Border.BackgroundProperty, "ToolBg");
            toolBg.SetResourceReference(Border.BorderBrushProperty, "MenuSep");
            main.Children.Add(toolBg);

            files.ItemsSource = entries;
            files.ItemTemplate = (DataTemplate)Application.Current.Resources["FinderTile"];
            files.ItemContainerStyle = (Style)Application.Current.Resources["Bare"];
            files.SelectionMode = SelectionMode.Extended;
            files.Background = Brushes.Transparent; files.BorderThickness = new Thickness(0); files.Padding = new Thickness(10, 8, 10, 8);
            files.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel)));
            ScrollViewer.SetHorizontalScrollBarVisibility(files, ScrollBarVisibility.Disabled);
            files.AllowDrop = true;
            side.AllowDrop = true;
            side.DragOver += (s, e) =>
            {
                var t = SideUnder(e.OriginalSource);
                e.Effects = !e.Data.GetDataPresent(DataFormats.FileDrop) || !SideAccepts(t) ? DragDropEffects.None
                    : t.Path == "trash" || (e.KeyStates & DragDropKeyStates.ControlKey) == 0 ? DragDropEffects.Move : DragDropEffects.Copy;
                e.Handled = true;
            };
            side.Drop += (s, e) =>
            {
                var t = SideUnder(e.OriginalSource);
                var dropped = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (dropped == null || !SideAccepts(t)) return;
                if (t.Path == "trash") Launcher.Recycle(dropped); else DropInto(dropped, t.Path, e);
            };
            files.MouseDoubleClick += (s, e) => { var en = Under(e.OriginalSource); if (en != null) Open(en); };
            files.PreviewMouseLeftButtonDown += (s, e) =>
            {
                dragFrom = Under(e.OriginalSource) != null ? (Point?)e.GetPosition(files) : null;
                if (dragFrom == null) files.SelectedItems.Clear();
            };
            files.PreviewMouseMove += OnDragOut;
            files.KeyDown += OnKey;
            files.MouseRightButtonUp += OnContext;
            files.Drop += OnDrop;
            files.DragOver += (s, e) => { e.Effects = path == AppsLocation ? DragDropEffects.None : (e.KeyStates & DragDropKeyStates.ControlKey) != 0 ? DragDropEffects.Copy : DragDropEffects.Move; e.Handled = true; };
            files.SelectionChanged += delegate { UpdateStatus(); };
            Grid.SetRow(files, 1);
            main.Children.Add(files);
            status.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetRow(status, 2);
            main.Children.Add(status);
            Layout.Children.Add(main);

            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { filter.Focus(); e.Handled = true; }
                else if (e.SystemKey == Key.Left || (e.Key == Key.Back && !filter.IsKeyboardFocused)) { Back(); e.Handled = true; }
                else if (e.SystemKey == Key.Right) { Forward(); e.Handled = true; }
                else if (e.SystemKey == Key.Up) { Up(); e.Handled = true; }
                else if (e.Key == Key.W && Keyboard.Modifiers == ModifierKeys.Control) Close();
                else if (e.Key == Key.F5) Load();
            };
            reloadSoon = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            reloadSoon.Tick += delegate { reloadSoon.Stop(); Load(); };
            Loaded += delegate { App.Finders.Add(this); App.QueueRefresh(); };
            Closed += delegate { App.Finders.Remove(this); if (watcher != null) watcher.Dispose(); App.QueueRefresh(); };
            Apps.Changed += delegate { if (path == AppsLocation) Load(); };

            selectAfterLoad = select;
            Go(start, false);
        }

        Button Tool(string glyph, Action a)
        {
            var b = new Button { Content = new TextBlock { Text = glyph, FontFamily = Theme.Glyphs, FontSize = 14 }, Style = (Style)Application.Current.Resources["Flat"], Width = 32, Height = 28, Margin = new Thickness(0, 0, 2, 0) };
            WindowChrome.SetIsHitTestVisibleInChrome(b, true);
            b.Click += delegate { a(); };
            return b;
        }

        static List<SideEntry> Sidebar()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Func<Environment.SpecialFolder, string> sf = Environment.GetFolderPath;
            var list = new List<SideEntry>
            {
                new SideEntry { Name = "Favorites", IsHeader = true },
                new SideEntry { Name = "Applications", Glyph = "", Path = AppsLocation },
                new SideEntry { Name = "Desktop", Glyph = "", Path = sf(Environment.SpecialFolder.DesktopDirectory) },
                new SideEntry { Name = "Documents", Glyph = "", Path = sf(Environment.SpecialFolder.MyDocuments) },
                new SideEntry { Name = "Downloads", Glyph = "", Path = Launcher.Downloads },
                new SideEntry { Name = "Pictures", Glyph = "", Path = sf(Environment.SpecialFolder.MyPictures) },
                new SideEntry { Name = "Music", Glyph = "", Path = sf(Environment.SpecialFolder.MyMusic) },
                new SideEntry { Name = "Movies", Glyph = "", Path = sf(Environment.SpecialFolder.MyVideos) },
                new SideEntry { Name = Environment.UserName, Glyph = "", Path = home },
                new SideEntry { Name = "Locations", IsHeader = true },
            };
            foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
                list.Add(new SideEntry { Name = DriveName(d), Glyph = d.DriveType == DriveType.Removable ? "" : "", Path = d.RootDirectory.FullName });
            list.Add(new SideEntry { Name = "Trash", Glyph = "", Path = "trash" });
            return list;
        }

        static string DriveName(DriveInfo d)
        {
            string label = "";
            try { label = d.VolumeLabel; } catch { }
            return (string.IsNullOrEmpty(label) ? "Local Disk" : label) + " (" + d.Name.TrimEnd('\\') + ")";
        }

        // ---------- navigation ----------

        void Go(string target, bool record = true)
        {
            if (target != AppsLocation && !Directory.Exists(target)) return;
            if (record && path != null) { history.Push(path); future.Clear(); }
            path = target;
            filter.Text = "";
            Load();
        }

        void Back() { if (history.Count == 0) return; future.Push(path); path = history.Pop(); Load(); }
        void Forward() { if (future.Count == 0) return; history.Push(path); path = future.Pop(); Load(); }
        void Up() { if (path == AppsLocation) return; var p = Directory.GetParent(path); if (p != null) Go(p.FullName); }

        void Load()
        {
            int id = ++loadId;
            back.IsEnabled = history.Count > 0; forward.IsEnabled = future.Count > 0;
            var sideMatch = ((List<SideEntry>)side.ItemsSource).FirstOrDefault(s => s.Path != null && string.Equals(s.Path.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            side.SelectedItem = sideMatch;
            Title = title.Text = path == AppsLocation ? "Applications" : sideMatch != null ? sideMatch.Name : (new DirectoryInfo(path).Name);
            Watch();
            if (path == AppsLocation)
            {
                Fill(Apps.All.Select(a => { var app = a; return new Entry { Name = a.Name, Path = a.Exe, Open = () => Launcher.Open(app.Target), Sub = a.Icon }; }).ToList(), id);
                return;
            }
            string dir = path;
            Task.Run(() =>
            {
                var list = new List<Entry>();
                try
                {
                    foreach (var fi in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                    {
                        if ((fi.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 || fi.Name.StartsWith(".")) continue;
                        list.Add(new Entry { Name = fi.Name, Path = fi.FullName, IsDir = (fi.Attributes & FileAttributes.Directory) != 0 });
                    }
                }
                catch (Exception ex) { App.Log(ex.Message); }
                list.Sort((a, b) => a.IsDir != b.IsDir ? (a.IsDir ? -1 : 1) : StrCmpLogicalW(a.Name, b.Name));
                return list;
            }).ContinueWith(t => Fill(t.Result, id), TaskScheduler.FromCurrentSynchronizationContext());
        }

        static readonly HashSet<string> thumbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".tif", ".tiff", ".ico", ".mp4", ".mov", ".mkv", ".avi", ".wmv", ".pdf", ".psd", ".svg" };
        static readonly HashSet<string> ownIcon = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".exe", ".lnk", ".url", ".msc", ".appref-ms", ".cpl", ".scr" };

        void Fill(List<Entry> list, int id)
        {
            if (id != loadId) return;
            all = list;
            ApplyFilter();
            foreach (var en in list)
            {
                var e = en;
                if (path == AppsLocation) { Icons.Get(e.Sub, false, i => e.Icon = i, 96); e.Sub = null; continue; }
                string ext = System.IO.Path.GetExtension(e.Path);
                if (e.IsDir) Icons.Get(e.Path, false, i => e.Icon = i, 96, false);
                else if (thumbs.Contains(ext)) Icons.Get(e.Path, false, i => e.Icon = i, 128, false);
                else if (ownIcon.Contains(ext)) Icons.Get(e.Path, false, i => e.Icon = i, 96);
                else Icons.Get(e.Path, false, i => e.Icon = i, 96, true, "ext" + ext);
            }
            if (selectAfterLoad != null)
            {
                var s = entries.FirstOrDefault(x => string.Equals(x.Path, selectAfterLoad, StringComparison.OrdinalIgnoreCase));
                selectAfterLoad = null;
                if (s != null) { files.SelectedItem = s; files.ScrollIntoView(s); }
            }
        }

        void ApplyFilter()
        {
            string q = filter.Text.Trim();
            entries.Clear();
            foreach (var e in q.Length == 0 ? all : all.Where(x => x.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0)) entries.Add(e);
            UpdateStatus();
        }

        void UpdateStatus()
        {
            string s = entries.Count + (entries.Count == 1 ? " item" : " items");
            if (files.SelectedItems.Count > 0) s = files.SelectedItems.Count + " of " + s + " selected";
            if (path != AppsLocation && path != null)
            {
                try { var d = new DriveInfo(System.IO.Path.GetPathRoot(path)); s += ", " + Size(d.AvailableFreeSpace) + " available"; } catch { }
            }
            status.Text = s;
        }

        static string Size(long b)
        {
            string[] u = { "bytes", "KB", "MB", "GB", "TB" };
            double v = b; int i = 0;
            while (v >= 1000 && i < u.Length - 1) { v /= 1000; i++; }
            return v.ToString(i == 0 ? "0" : "0.#") + " " + u[i];
        }

        void Watch()
        {
            if (watcher != null) { watcher.Dispose(); watcher = null; }
            if (path == AppsLocation) return;
            try
            {
                watcher = new FileSystemWatcher(path) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName };
                FileSystemEventHandler h = delegate { Dispatcher.BeginInvoke(new Action(() => { reloadSoon.Stop(); reloadSoon.Start(); })); };
                watcher.Created += h; watcher.Deleted += h; watcher.Renamed += (s, e) => h(s, e);
                watcher.EnableRaisingEvents = true;
            }
            catch { }
        }

        // ---------- actions ----------

        void Open(Entry e)
        {
            if (e.Open != null) e.Open();
            else if (e.IsDir) Go(e.Path);
            else Launcher.Open(e.Path);
        }

        List<string> Selected() { return files.SelectedItems.Cast<Entry>().Where(x => x.Path != null && x.Open == null).Select(x => x.Path).ToList(); }

        static Entry Under(object source)
        {
            var d = source as DependencyObject;
            while (d != null && !(d is ListBoxItem)) d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            return d != null ? ((ListBoxItem)d).DataContext as Entry : null;
        }

        void OnKey(object s, KeyEventArgs e)
        {
            var sel = files.SelectedItem as Entry;
            if (e.Key == Key.Enter && sel != null) { foreach (var x in files.SelectedItems.Cast<Entry>().ToList()) Open(x); e.Handled = true; }
            else if (e.Key == Key.Delete && path != AppsLocation) { Launcher.Recycle(Selected()); e.Handled = true; }
            else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) CopySelection();
            else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control) Paste();
        }

        void CopySelection()
        {
            var sel = Selected();
            if (sel.Count == 0) return;
            var sc = new StringCollection(); sc.AddRange(sel.ToArray());
            Clipboard.SetFileDropList(sc);
        }

        void Paste()
        {
            if (path == AppsLocation || !Clipboard.ContainsFileDropList()) return;
            Transfer(Clipboard.GetFileDropList().Cast<string>().ToArray(), true);
        }

        void Transfer(string[] sources, bool copy)
        {
            foreach (var src in sources)
            {
                string dest = System.IO.Path.Combine(path, System.IO.Path.GetFileName(src.TrimEnd('\\')));
                if (string.Equals(dest, src, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    bool dir = Directory.Exists(src);
                    if (copy) { if (dir) VB.FileSystem.CopyDirectory(src, dest, VB.UIOption.AllDialogs); else VB.FileSystem.CopyFile(src, dest, VB.UIOption.AllDialogs); }
                    else { if (dir) VB.FileSystem.MoveDirectory(src, dest, VB.UIOption.AllDialogs); else VB.FileSystem.MoveFile(src, dest, VB.UIOption.AllDialogs); }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { MessageBox.Show(ex.Message, "Finder"); }
            }
        }

        void OnDragOut(object s, MouseEventArgs e)
        {
            if (dragFrom == null || e.LeftButton != MouseButtonState.Pressed) return;
            if ((e.GetPosition(files) - dragFrom.Value).Length < 8) return;
            dragFrom = null;
            var sel = Selected();
            if (sel.Count == 0) return;
            DragDrop.DoDragDrop(files, new DataObject(DataFormats.FileDrop, sel.ToArray()), DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }

        void OnDrop(object s, DragEventArgs e)
        {
            var dropped = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (dropped == null || path == AppsLocation) return;
            var target = Under(e.OriginalSource);
            DropInto(dropped, target != null && target.IsDir ? target.Path : path, e);   // dropping onto a folder moves into it
        }

        // Same drive moves, another drive copies; holding Ctrl always copies (like Explorer).
        void DropInto(string[] dropped, string folder, DragEventArgs e)
        {
            string saved = path;
            path = folder;
            bool sameDrive = dropped.All(f => string.Equals(System.IO.Path.GetPathRoot(f), System.IO.Path.GetPathRoot(folder), StringComparison.OrdinalIgnoreCase));
            Transfer(dropped, (e.KeyStates & DragDropKeyStates.ControlKey) != 0 || !sameDrive);
            path = saved;
        }

        static SideEntry SideUnder(object source)
        {
            var d = source as DependencyObject;
            while (d != null && !(d is ListBoxItem)) d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            return d != null ? ((ListBoxItem)d).DataContext as SideEntry : null;
        }

        // Sidebar folders (and Trash) accept drops too, like the macOS Finder sidebar.
        static bool SideAccepts(SideEntry t)
        {
            return t != null && !t.IsHeader && (t.Path == "trash" || (t.Path != null && t.Path != AppsLocation && Directory.Exists(t.Path)));
        }

        void OnContext(object s, MouseButtonEventArgs e)
        {
            var en = Under(e.OriginalSource);
            var cm = new ContextMenu();
            Action<string, Action, bool> add = (text, a, enabled) => { var mi = new MenuItem { Header = text, IsEnabled = enabled }; mi.Click += delegate { a(); }; cm.Items.Add(mi); };
            if (en != null)
            {
                if (!files.SelectedItems.Contains(en)) files.SelectedItem = en;
                add("Open", () => Open(en), true);
                if (en.Path != null && en.Open == null && !en.IsDir) add("Open With…", () => Process.Start("rundll32.exe", "shell32.dll,OpenAs_RunDLL " + en.Path), true);
                cm.Items.Add(new Separator());
                if (en.Path != null)
                {
                    add("Get Info", () => SHObjectProperties(new WindowInteropHelper(this).Handle, 2, en.Path, null), true);
                    add("Show in Explorer", () => Process.Start("explorer.exe", "/select,\"" + en.Path + "\""), true);
                    add("Copy", CopySelection, en.Open == null);
                    add("Copy Path", () => Clipboard.SetText(en.Path), true);
                    if (en.Open == null) { cm.Items.Add(new Separator()); add("Move to Trash", () => Launcher.Recycle(Selected()), true); }
                }
                if (en.Open != null)
                {
                    var app = Apps.All.FirstOrDefault(a => a.Name == en.Name);
                    if (app != null) add("Add to Dock", () => App.Dock.Pin(app), true);
                }
            }
            else if (path != AppsLocation)
            {
                files.SelectedItems.Clear();
                add("New Folder", NewFolder, true);
                add("Paste", Paste, Clipboard.ContainsFileDropList());
                cm.Items.Add(new Separator());
                add("Open in Explorer", () => Process.Start("explorer.exe", "\"" + path + "\""), true);
                add("Get Info", () => SHObjectProperties(new WindowInteropHelper(this).Handle, 2, path, null), true);
            }
            if (cm.Items.Count > 0) cm.IsOpen = true;
            e.Handled = true;
        }

        void NewFolder()
        {
            string name = "untitled folder";
            for (int i = 2; Directory.Exists(System.IO.Path.Combine(path, name)); i++) name = "untitled folder " + i;
            try { Directory.CreateDirectory(System.IO.Path.Combine(path, name)); selectAfterLoad = System.IO.Path.Combine(path, name); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Finder"); }
        }
    }

    // "Desktop & Dock" preferences.
    class SettingsWindow : MacWindow
    {
        readonly DispatcherTimer apply = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };

        public SettingsWindow()
        {
            Title = "Desktop & Dock";
            Width = 560; Height = 830; ResizeMode = ResizeMode.CanMinimize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            apply.Tick += delegate { apply.Stop(); Settings.Apply(); };

            Layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) });
            Layout.RowDefinitions.Add(new RowDefinition());
            var head = new Border();
            head.SetResourceReference(Border.BackgroundProperty, "ToolBg");
            var headGrid = new Grid();
            headGrid.Children.Add(TrafficLights());
            var t = Text("Desktop & Dock", 13, "Fg", FontWeights.SemiBold); t.HorizontalAlignment = HorizontalAlignment.Center;
            headGrid.Children.Add(t);
            head.Child = headGrid;
            Layout.Children.Add(head);

            var body = new StackPanel { Margin = new Thickness(24, 8, 24, 24) };
            body.Children.Add(Heading("Appearance"));
            var look = Group(body);
            var seg = new StackPanel { Orientation = Orientation.Horizontal };
            string[] modes = { "Auto", "Light", "Dark" };
            var segButtons = new List<Button>();
            for (int i = 0; i < 3; i++)
            {
                int mode = i;
                var b = new Button { Content = Text(modes[i], 13, "Fg"), Style = (Style)Application.Current.Resources["Flat"], Padding = new Thickness(18, 5, 18, 5), Margin = new Thickness(0, 0, 4, 0) };
                b.Click += delegate
                {
                    Settings.Appearance = mode;
                    Settings.Save();
                    Theme.Refresh();
                    for (int j = 0; j < 3; j++) segButtons[j].SetResourceReference(BackgroundProperty, j == mode ? "SelBg" : "Hover");
                };
                segButtons.Add(b);
                seg.Children.Add(b);
            }
            for (int j = 0; j < 3; j++) segButtons[j].SetResourceReference(BackgroundProperty, j == Settings.Appearance ? "SelBg" : "Hover");
            Row(look, seg);
            body.Children.Add(Heading("Dock"));
            var dock = Group(body);
            AddSlider(dock, "Size", 36, 96, Settings.IconSize, v => Settings.IconSize = (int)v);
            AddSwitch(dock, "Magnification", Settings.Magnify, v => Settings.Magnify = v);
            AddSlider(dock, "Magnified size", 48, 160, Settings.MagnifySize, v => Settings.MagnifySize = (int)v);
            AddSwitch(dock, "Automatically hide and show the Dock", Settings.AutoHide, v => Settings.AutoHide = v);
            AddSwitch(dock, "Animate opening applications", Settings.Bounce, v => Settings.Bounce = v);
            AddSwitch(dock, "Show indicators for open applications", Settings.Indicators, v => Settings.Indicators = v);
            AddSwitch(dock, "Show open apps that aren't in the Dock", Settings.ShowRunning, v => { Settings.ShowRunning = v; App.QueueRefresh(); });
            AddSwitch(dock, "Uniform icon tiles", Settings.IconTiles, v => Settings.IconTiles = v);

            body.Children.Add(Heading("Desktop & Menu Bar"));
            var desk = Group(body);
            AddSwitch(desk, "Show menu bar", Settings.ShowMenuBar, v => { Settings.ShowMenuBar = v; App.MenuBar.SetShown(v); });
            AddSwitch(desk, "Hide the Windows taskbar", Settings.HideTaskbar, v => { Settings.HideTaskbar = v; if (v) Taskbar.Hide(); else Taskbar.Restore(); });
            AddSwitch(desk, "Open OpenDock at login", Settings.LaunchAtLogin, v => Settings.LaunchAtLogin = v);

            var hiddenRow = new DockPanel();
            var unhide = new Button { Content = Text("Show All", 13, "Fg"), Style = (Style)Application.Current.Resources["Flat"], Padding = new Thickness(12, 4, 12, 4), IsEnabled = Settings.Hidden.Count > 0 };
            unhide.SetResourceReference(BackgroundProperty, "SelBg");
            DockPanel.SetDock(unhide, Dock.Right);
            var hiddenLabel = Text("Apps hidden from Launchpad: " + Settings.Hidden.Count, 13, "Fg");
            unhide.Click += delegate { Settings.Hidden.Clear(); Settings.Save(); App.Launchpad.Invalidate(); hiddenLabel.Text = "Apps hidden from Launchpad: 0"; unhide.IsEnabled = false; };
            hiddenRow.Children.Add(unhide); hiddenRow.Children.Add(hiddenLabel);
            Row(desk, hiddenRow);

            var foot = Text("Alt+Space opens Spotlight. Drag apps off the Dock to remove them, or drop files and apps onto it.", 11.5, "FgDim");
            foot.TextWrapping = TextWrapping.Wrap; foot.Margin = new Thickness(4, 14, 4, 0);
            body.Children.Add(foot);
            var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetRow(scroll, 1);
            Layout.Children.Add(scroll);
        }

        static TextBlock Heading(string s)
        {
            var t = Text(s, 13, "Fg", FontWeights.SemiBold);
            t.Margin = new Thickness(4, 18, 0, 8);
            return t;
        }

        static StackPanel Group(Panel parent)
        {
            var b = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(14, 4, 14, 4) };
            b.SetResourceReference(Border.BackgroundProperty, "Hover");
            b.SetResourceReference(Border.BorderBrushProperty, "MenuSep");
            var s = new StackPanel();
            b.Child = s;
            parent.Children.Add(b);
            return s;
        }

        static Border Row(Panel p, UIElement content)
        {
            var r = new Border { Child = content, Padding = new Thickness(0, 9, 0, 9), BorderThickness = new Thickness(0, p.Children.Count == 0 ? 0 : 1, 0, 0) };
            r.SetResourceReference(Border.BorderBrushProperty, "MenuSep");
            p.Children.Add(r);
            return r;
        }

        void AddSwitch(Panel p, string label, bool value, Action<bool> set)
        {
            var cb = new CheckBox { Content = Text(label, 13, "Fg"), IsChecked = value, Style = (Style)Application.Current.Resources["Switch"], Cursor = Cursors.Hand };
            cb.Click += delegate { set(cb.IsChecked == true); apply.Stop(); apply.Start(); };
            Row(p, cb);
        }

        void AddSlider(Panel p, string label, double min, double max, double value, Action<double> set)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(Text(label, 13, "Fg"));
            var s = new Slider { Minimum = min, Maximum = max, Value = value };
            s.ValueChanged += delegate { set(s.Value); apply.Stop(); apply.Start(); };
            Grid.SetColumn(s, 1);
            g.Children.Add(s);
            Row(p, g);
        }
    }
}
