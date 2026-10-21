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

        protected bool Dragging;   // a drag out of this window is in progress

        public MacWindow() : this(true) { }

        public MacWindow(bool glass)
        {
            WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 52, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0, 0, 0, 1), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false });
            FontFamily = Theme.Font;
            SetResourceReference(BackgroundProperty, "WinBg");
            if (glass) Glass.Attach(this, "WinGlass");
            // Click anywhere outside the window (another app, the desktop) and it minimizes out of the way.
            // OpenDock's own dialogs, menus and other windows don't count, and neither does dragging out of it.
            Deactivated += delegate
            {
                var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                t.Tick += delegate
                {
                    t.Stop();
                    if (IsActive || Dragging || !IsVisible || WindowState == WindowState.Minimized) return;
                    uint pid;
                    Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out pid);
                    if (pid != (uint)Process.GetCurrentProcess().Id) WindowState = WindowState.Minimized;
                };
                t.Start();
            };
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
            Dragging = true;
            try { DragDrop.DoDragDrop(files, new DataObject(DataFormats.FileDrop, sel.ToArray()), DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link); }
            finally { Dragging = false; }
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
    // A recreation of macOS System Settings: a searchable sidebar of categories with coloured icons, and a pane of
    // grouped rows. OpenDock's own settings get real panes; the rest open the matching Windows Settings page.
    partial class SettingsWindow : MacWindow
    {
        class Cat { public string Name, Glyph, Color; public Action<StackPanel> Build; }
        class NavRow { public Cat Cat; public Border Box; public TextBlock Label; }

        readonly DispatcherTimer apply = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        readonly List<NavRow> rows = new List<NavRow>();
        readonly List<StackPanel> sections = new List<StackPanel>();
        readonly ScrollViewer paneHost = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        readonly TextBlock title = Text("", 15, "Fg", FontWeights.Bold);
        readonly List<string> history = new List<string>();
        readonly Button back, forward;
        int at = -1;
        string current;
        Dictionary<string, Action<StackPanel>> sub;

        public SettingsWindow(string start)
        {
            Title = "System Settings";
            Width = 920; Height = 660; MinWidth = 760; MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            apply.Tick += delegate { apply.Stop(); Settings.Apply(); };

            var cats = new[]
            {
                new[] { C("Wi-Fi", "\uE701", "#0A84FF", WiFi), C("Bluetooth", "\uE702", "#0A84FF", BluetoothPane), C("Network", "\uE774", "#0A84FF", NetworkPane) },
                new[] { C("Notifications", "\uE7E7", "#FF3B30", Notifications), C("Sound", "\uE767", "#FF2D55", Sound), C("Focus", "\uE708", "#5E5CE6", Focus) },
                new[] { C("General", "\uE713", "#8E8E93", General), C("Appearance", "\uE790", "#3A3A3C", Appearance), C("Accessibility", "\uE776", "#0A84FF", Accessibility),
                        C("Control Center", "\uE9E9", "#8E8E93", ControlCenter), C("Desktop & Dock", "\uE7F4", "#3A3A3C", DesktopDock),
                        C("Displays", "\uE7F8", "#0A84FF", Displays), C("Wallpaper", "\uE91B", "#32ADE6", WallpaperPane) },
                new[] { C("Battery", "\uE83F", "#34C759", Battery), C("Lock Screen", "\uE72E", "#3A3A3C", LockScreen),
                        C("Privacy & Security", "\uEA18", "#0A84FF", Privacy), C("Users & Groups", "\uE716", "#0A84FF", Users) },
                new[] { C("Keyboard", "\uE765", "#8E8E93", KeyboardPane), C("Mouse", "\uE962", "#8E8E93", MousePane), C("Printers & Scanners", "\uE749", "#8E8E93", Printers) },
            };
            // Panes reached from General, like macOS.
            sub = new Dictionary<string, Action<StackPanel>> { { "About", AboutPane }, { "Storage", Storage }, { "Login Items", LoginItems }, { "Date & Time", DateTimePane } };
            // Panes reached from General, like macOS.
            sub = new Dictionary<string, Action<StackPanel>> { { "About", AboutPane }, { "Storage", Storage }, { "Login Items", LoginItems }, { "Date & Time", DateTimePane } };
            // Panes reached from General, like macOS.
            sub = new Dictionary<string, Action<StackPanel>> { { "About", AboutPane }, { "Storage", Storage }, { "Login Items", LoginItems }, { "Date & Time", DateTimePane } };

            Layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
            Layout.ColumnDefinitions.Add(new ColumnDefinition());

            // ----- sidebar -----
            var side = new Border();
            side.SetResourceReference(Border.BackgroundProperty, "SideBg");
            var sideGrid = new Grid();
            sideGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) });
            sideGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            sideGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            sideGrid.RowDefinitions.Add(new RowDefinition());
            sideGrid.Children.Add(TrafficLights());

            var search = new TextBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center };
            search.SetResourceReference(ForegroundProperty, "Fg"); search.SetResourceReference(TextBox.CaretBrushProperty, "Fg");
            var ph = Text("Search", 13, "FgDim"); ph.IsHitTestVisible = false;
            var mag = Text("", 12, "FgDim"); mag.FontFamily = Theme.Glyphs; mag.Margin = new Thickness(0, 0, 6, 0);
            var sg = new Grid();
            sg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            sg.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(ph, 1); Grid.SetColumn(search, 1);
            sg.Children.Add(mag); sg.Children.Add(ph); sg.Children.Add(search);
            var searchBox = new Border { Height = 28, CornerRadius = new CornerRadius(7), Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(12, 0, 12, 10), Child = sg };
            searchBox.SetResourceReference(Border.BackgroundProperty, "Field");
            search.TextChanged += delegate { ph.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Hidden; Filter(search.Text.Trim()); };
            Grid.SetRow(searchBox, 1);
            sideGrid.Children.Add(searchBox);

            var card = UserCard(() => Show("Users & Groups", true));
            Grid.SetRow(card, 2);
            sideGrid.Children.Add(card);

            var nav = new StackPanel { Margin = new Thickness(10, 4, 10, 16) };
            foreach (var group in cats)
            {
                var sec = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
                foreach (var c in group) sec.Children.Add(NavItem(c));
                sections.Add(sec);
                nav.Children.Add(sec);
            }
            var navScroll = new ScrollViewer { Content = nav, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetRow(navScroll, 3);
            sideGrid.Children.Add(navScroll);
            side.Child = sideGrid;
            Layout.Children.Add(side);

            // ----- pane -----
            var main = new Grid();
            main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) });
            main.RowDefinitions.Add(new RowDefinition());
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            back = Arrow("", () => Go(-1));
            forward = Arrow("", () => Go(1));
            title.Margin = new Thickness(10, 0, 0, 0);
            head.Children.Add(back); head.Children.Add(forward); head.Children.Add(title);
            main.Children.Add(head);
            Grid.SetRow(paneHost, 1);
            main.Children.Add(paneHost);
            Grid.SetColumn(main, 1);
            Layout.Children.Add(main);

            Show(start ?? "Desktop & Dock", true);
        }

        static Cat C(string name, string glyph, string color, Action<StackPanel> build) { return new Cat { Name = name, Glyph = glyph, Color = color, Build = build }; }

        static FrameworkElement UserCard(Action open)
        {
            string who = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            who = who.Substring(who.IndexOf('\\') + 1);
            string initials = string.Concat(who.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpper(w[0]).ToString()));
            var avatar = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(18), Background = new LinearGradientBrush(Hex("#A8A8B0"), Hex("#6E6E76"), 90), Margin = new Thickness(0, 0, 10, 0) };
            avatar.Child = new TextBlock { Text = initials, Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(Text(who, 13, "Fg", FontWeights.SemiBold));
            names.Children.Add(Text("Windows Account", 11, "FgDim"));
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(avatar); row.Children.Add(names);
            var box = new Border { Child = row, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(10, 0, 10, 10), CornerRadius = new CornerRadius(8), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            box.MouseEnter += delegate { box.SetResourceReference(Border.BackgroundProperty, "Hover"); };
            box.MouseLeave += delegate { box.Background = Brushes.Transparent; };
            box.MouseLeftButtonUp += delegate { open(); };
            return box;
        }

        static Color Hex(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }

        static Color Lighter(string hex)
        {
            var c = Hex(hex);
            return Color.FromRgb((byte)(c.R + (255 - c.R) * 0.25), (byte)(c.G + (255 - c.G) * 0.25), (byte)(c.B + (255 - c.B) * 0.25));
        }

        Border NavItem(Cat c)
        {
            var tile = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(6), Background = new LinearGradientBrush(Lighter(c.Color), Hex(c.Color), 90), Margin = new Thickness(0, 0, 9, 0) };
            tile.Child = new TextBlock { Text = c.Glyph, FontFamily = Theme.Glyphs, FontSize = 12, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var label = Text(c.Name, 13, "Fg");
            DockPanel.SetDock(tile, Dock.Left);
            var dp = new DockPanel();
            dp.Children.Add(tile); dp.Children.Add(label);
            var box = new Border { Child = dp, Padding = new Thickness(6, 4, 8, 4), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            rows.Add(new NavRow { Cat = c, Box = box, Label = label });
            box.MouseEnter += delegate { if (current != c.Name) box.SetResourceReference(Border.BackgroundProperty, "Hover"); };
            box.MouseLeave += delegate { if (current != c.Name) box.Background = Brushes.Transparent; };
            box.MouseLeftButtonUp += delegate { Show(c.Name, true); };
            return box;
        }

        Button Arrow(string glyph, Action a)
        {
            var t = Text(glyph, 13, "Fg"); t.FontFamily = Theme.Glyphs;
            var b = new Button { Content = t, Style = (Style)Application.Current.Resources["Flat"], Width = 28, Height = 28, Padding = new Thickness(0) };
            b.Click += delegate { a(); };
            WindowChrome.SetIsHitTestVisibleInChrome(b, true);
            return b;
        }

        void Filter(string q)
        {
            foreach (var r in rows) r.Box.Visibility = q.Length == 0 || r.Cat.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var s in sections) s.Visibility = s.Children.Cast<UIElement>().Any(e => e.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;
        }

        // Opens a pane; record = add it to the back/forward history.
        public void Show(string name, bool record)
        {
            Action<StackPanel> build;
            var row = rows.FirstOrDefault(r => r.Cat.Name == name);
            if (row == null && name != null && sub.TryGetValue(name, out build)) row = rows.First(r => r.Cat.Name == "General");   // a General sub-pane
            else { if (row == null) row = rows.First(r => r.Cat.Name == "Desktop & Dock"); name = row.Cat.Name; build = row.Cat.Build; }
            current = name;
            if (record && (at < 0 || history[at] != current))
            {
                history.RemoveRange(at + 1, history.Count - at - 1);
                history.Add(current);
                at = history.Count - 1;
            }
            foreach (var r in rows)
            {
                bool sel = r == row;
                if (sel) r.Box.SetResourceReference(Border.BackgroundProperty, "Accent"); else r.Box.Background = Brushes.Transparent;
                if (sel) r.Label.Foreground = Brushes.White; else r.Label.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            }
            title.Text = current;
            Title = current;
            back.IsEnabled = at > 0; back.Opacity = at > 0 ? 1 : 0.35;
            forward.IsEnabled = at < history.Count - 1; forward.Opacity = forward.IsEnabled ? 1 : 0.35;
            var body = new StackPanel { Margin = new Thickness(24, 0, 24, 28) };
            build(body);
            paneHost.Content = body;
            paneHost.ScrollToTop();
        }

        void Go(int step)
        {
            int to = at + step;
            if (to < 0 || to >= history.Count) return;
            at = to;
            Show(history[at], false);
        }

        // ----- panes -----

        void General(StackPanel body)
        {
            var about = Group(body);
            var info = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
            info.Children.Add(Text("OpenDock", 20, "Fg", FontWeights.Bold));
            var sub = Text("A free, open-source macOS-style desktop for Windows.", 13, "FgDim"); sub.Margin = new Thickness(0, 4, 0, 2);
            info.Children.Add(sub);
            info.Children.Add(Text("MIT License", 11.5, "FgDim"));
            Row(about, info);
            var links = new StackPanel { Orientation = Orientation.Horizontal };
            links.Children.Add(Pill("View on GitHub", () => Launcher.Shell("https://github.com/leemadov/OpenDock")));
            Row(about, links);

            body.Children.Add(new Border { Height = 16 });
            var nav = Group(body);
            NavLink(nav, "About", "\uE977", "#8E8E93", "About");
            NavLink(nav, "Storage", "\uEDA2", "#8E8E93", "Storage");
            NavLink(nav, "Login Items", "\uE7E8", "#8E8E93", "Login Items");
            NavLink(nav, "Date & Time", "\uE787", "#8E8E93", "Date & Time");

            body.Children.Add(Heading("Keyboard Shortcuts"));
            var keys = Group(body);
            InfoRow(keys, "Spotlight", "Alt+Space");
            InfoRow(keys, "New window of an app", "Middle-click or Shift+click its Dock icon");
        }

        void Appearance(StackPanel body)
        {
            var look = Group(body);
            var cards = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
            string[] modes = { "Auto", "Light", "Dark" };
            for (int i = 0; i < 3; i++)
            {
                int mode = i;
                bool sel = Settings.Appearance == mode;
                var preview = new Border { Width = 96, Height = 64, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(2.5), ClipToBounds = true };
                if (sel) preview.SetResourceReference(Border.BorderBrushProperty, "Accent"); else preview.SetResourceReference(Border.BorderBrushProperty, "MenuSep");
                preview.Background = mode == 1 ? new SolidColorBrush(Hex("#E8E8ED")) : mode == 2 ? new SolidColorBrush(Hex("#1C1C1E"))
                    : (Brush)new LinearGradientBrush(new GradientStopCollection { new GradientStop(Hex("#E8E8ED"), 0.5), new GradientStop(Hex("#1C1C1E"), 0.5) }, new Point(0, 0), new Point(1, 0));
                var win = new Border { Width = 52, Height = 34, CornerRadius = new CornerRadius(4), Margin = new Thickness(10, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    Background = new SolidColorBrush(Hex(mode == 2 ? "#3A3A3C" : "#FFFFFF")) };
                var dots = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 4, 0, 0) };
                foreach (var dc in new[] { "#FF5F57", "#FEBC2E", "#28C840" }) dots.Children.Add(new Ellipse { Width = 4, Height = 4, Fill = new SolidColorBrush(Hex(dc)), Margin = new Thickness(0, 0, 2, 0) });
                win.Child = dots;
                preview.Child = win;
                var label = Text(modes[i], 12, sel ? "Fg" : "FgDim", sel ? FontWeights.SemiBold : FontWeights.Normal);
                label.HorizontalAlignment = HorizontalAlignment.Center; label.Margin = new Thickness(0, 6, 0, 0);
                var card = new StackPanel { Margin = new Thickness(18, 0, 0, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent };
                card.Children.Add(preview); card.Children.Add(label);
                card.MouseLeftButtonUp += delegate { Settings.Appearance = mode; Settings.Save(); Theme.Refresh(); Show("Appearance", false); };
                cards.Children.Add(card);
            }
            var g = new DockPanel();
            var name = Text("Appearance", 13, "Fg"); name.VerticalAlignment = VerticalAlignment.Top; name.Margin = new Thickness(0, 4, 0, 0);
            DockPanel.SetDock(cards, Dock.Right);
            g.Children.Add(cards); g.Children.Add(name);
            Row(look, g);

            body.Children.Add(Heading("Icons"));
            var icons = Group(body);
            AddSwitch(icons, "Uniform icon tiles", Settings.IconTiles, v => Settings.IconTiles = v);
        }

        void ControlCenter(StackPanel body)
        {
            var bar = Group(body);
            AddSwitch(bar, "Show menu bar", Settings.ShowMenuBar, v => { Settings.ShowMenuBar = v; App.MenuBar.SetShown(v); });
            InfoRow(bar, "Background Apps", "Click ^ in the menu bar");

            body.Children.Add(Heading("More"));
            var more = Group(body);
            NavLink(more, "Notifications", "\uE7E7", "#FF3B30", "Notifications");
            NavLink(more, "Sound", "\uE767", "#FF2D55", "Sound");
            NavLink(more, "Date & Time", "\uE787", "#8E8E93", "Date & Time");
        }

        void DesktopDock(StackPanel body)
        {
            body.Children.Add(Heading("Dock"));
            var dock = Group(body);
            AddSlider(dock, "Size", 36, 96, Settings.IconSize, v => Settings.IconSize = (int)v);
            AddSwitch(dock, "Magnification", Settings.Magnify, v => Settings.Magnify = v);
            AddSlider(dock, "Magnified size", 48, 160, Settings.MagnifySize, v => Settings.MagnifySize = (int)v);
            AddSwitch(dock, "Automatically hide and show the Dock", Settings.AutoHide, v => Settings.AutoHide = v);
            AddSwitch(dock, "Animate opening applications", Settings.Bounce, v => Settings.Bounce = v);
            AddSwitch(dock, "Show indicators for open applications", Settings.Indicators, v => Settings.Indicators = v);
            AddSwitch(dock, "Show open apps that aren't in the Dock", Settings.ShowRunning, v => { Settings.ShowRunning = v; App.QueueRefresh(); });

            body.Children.Add(Heading("Desktop"));
            var desk = Group(body);
            AddSwitch(desk, "Hide the Windows taskbar", Settings.HideTaskbar, v => { Settings.HideTaskbar = v; if (v) Taskbar.Hide(); else Taskbar.Restore(); });
            NavLink(desk, "Wallpaper", "\uE91B", "#32ADE6", "Wallpaper");

            body.Children.Add(Heading("Launchpad"));
            var lp = Group(body);
            var hiddenRow = new DockPanel();
            var unhide = Pill("Show All", null);
            unhide.IsEnabled = Settings.Hidden.Count > 0;
            DockPanel.SetDock(unhide, Dock.Right);
            var hiddenLabel = Text("Apps hidden from Launchpad: " + Settings.Hidden.Count, 13, "Fg");
            unhide.Click += delegate { Settings.Hidden.Clear(); Settings.Save(); App.Launchpad.Invalidate(); hiddenLabel.Text = "Apps hidden from Launchpad: 0"; unhide.IsEnabled = false; };
            hiddenRow.Children.Add(unhide); hiddenRow.Children.Add(hiddenLabel);
            Row(lp, hiddenRow);

            var foot = Text("Drag apps off the Dock to remove them, or drop files and apps onto it.", 11.5, "FgDim");
            foot.TextWrapping = TextWrapping.Wrap; foot.Margin = new Thickness(4, 14, 4, 0);
            body.Children.Add(foot);
        }

        // ----- rows -----

        static TextBlock Heading(string s)
        {
            var t = Text(s, 13, "Fg", FontWeights.SemiBold);
            t.Margin = new Thickness(4, 20, 0, 8);
            return t;
        }

        static StackPanel Group(Panel parent)
        {
            var b = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(14, 2, 14, 2), Margin = new Thickness(0, parent.Children.Count == 0 ? 8 : 0, 0, 0) };
            b.SetResourceReference(Border.BackgroundProperty, "Card");
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

        static Button Pill(string text, Action a)
        {
            var b = new Button { Content = Text(text, 13, "Fg"), Style = (Style)Application.Current.Resources["Flat"], Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
            b.SetResourceReference(BackgroundProperty, "SelBg");
            if (a != null) b.Click += delegate { a(); };
            return b;
        }

        static void InfoRow(Panel p, string label, string value)
        {
            var d = new DockPanel();
            var v = Text(value, 13, "FgDim");
            DockPanel.SetDock(v, Dock.Right);
            d.Children.Add(v); d.Children.Add(Text(label, 13, "Fg"));
            Row(p, d);
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
            var s = new Slider { Minimum = min, Maximum = max, Value = value, VerticalAlignment = VerticalAlignment.Center };
            s.ValueChanged += delegate { set(s.Value); apply.Stop(); apply.Start(); };
            Grid.SetColumn(s, 1);
            g.Children.Add(s);
            Row(p, g);
        }
    }
}
