using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace OpenDock
{
    class DockItem
    {
        public string Kind = "app";               // app, internal, finder, launchpad, stack, trash, sep
        public string Name, Target, IconPath, RunKey;
        public bool Pinned;
        public readonly HashSet<string> Keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<WinInfo> Wins = new List<WinInfo>();
        public Border Root; public Image Img; public Ellipse Dot;
        public Border Badge; public TextBlock BadgeText;
        public int IconVersion;
        public readonly ScaleTransform Scale = new ScaleTransform(1, 1);
        // P = presence (0..1, grows/shrinks the slot), X = smoothed resting centre, CX/S = magnified centre/scale.
        public double P, PWant = 1, X = double.NaN, CX, S = 1, DotA, BounceT0 = -1, BounceStop;
        public double PFrom, PTarget = -1, PT0, GoneAt = -1;   // presence tween + grace period after the last window closes
        public bool Attention;                                  // a window flashed for attention (red dot until you switch to it)

        public string IconKey { get { return Kind=="app" ? Target??RunKey : "fixed:"+Kind; } }
        public bool IsSep { get { return Kind == "sep"; } }
        public bool Running { get { return Kind == "finder" ? App.Finders.Count > 0 : Wins.Count > 0; } }

        public bool Matches(WinInfo w)
        {
            return (w.Key != null && Keys.Contains(w.Key)) || (w.Aumid != null && Keys.Contains(w.Aumid)) || (!w.IsFrameHost && w.Exe != null && Keys.Contains(w.Exe));
        }

        public void ComputeKeys()
        {
            Keys.Clear();
            if (RunKey != null) Keys.Add(RunKey);
            if (Target == null) return;
            if (Target.StartsWith("app:"))
            {
                string id = Target.Substring(4);
                Keys.Add(id);
                var e = Apps.ById(id);
                if (e != null && e.Exe != null) Keys.Add(e.Exe);
                var x = Apps.ExpandKnownFolder(id);
                if (x != null) Keys.Add(x);
            }
            else
            {
                Keys.Add(Target);
                if (Target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                        string t = sh.CreateShortcut(Target).TargetPath;
                        if (!string.IsNullOrEmpty(t)) Keys.Add(t);
                    }
                    catch { }
                }
            }
        }

        public string FilePath
        {
            get
            {
                if (Target != null && !Target.StartsWith("app:")) return Target;
                if (Target != null) { var e = Apps.ById(Target.Substring(4)); if (e != null && e.Exe != null) return e.Exe; }
                return Wins.Count > 0 && !Wins[0].IsFrameHost ? Wins[0].Exe : null;
            }
        }
    }

    class DockWindow : Window
    {
        public AppBar Bar;
        public readonly string Device;
        public Sys.Display Screen { get { return Device == null ? DisplayLayout.Selected : DisplayLayout.ForDevice(Device); } }
        public Rect MonitorBounds { get { return DisplayLayout.Dip(Screen.Bounds); } }
        public Rect MonitorWorkArea { get { return DisplayLayout.Dip(Screen.Work); } }
        int iconRevision=Settings.IconRevision;
        bool closed, pinsNeedRefresh, repairingPlacement;
        readonly DispatcherTimer trashTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        readonly Canvas canvas = new Canvas(), layer = new Canvas();
        readonly Image wall = new Image { Stretch = Stretch.Fill };
        readonly RectangleGeometry wallClip = new RectangleGeometry();
        readonly Border bg = new Border { BorderThickness = new Thickness(1) }, sheen = new Border { IsHitTestVisible = false }, outline = new Border { BorderThickness = new Thickness(1), IsHitTestVisible = false };
        readonly Border label = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 3, 10, 4), Opacity = 0, IsHitTestVisible = false };
        readonly TextBlock labelText = new TextBlock { FontSize = 13, FontFamily = Theme.Font, MaxWidth = 180, TextTrimming = TextTrimming.CharacterEllipsis };
        readonly Rectangle edge = new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), Height = 2 };
        readonly List<DockItem> items = new List<DockItem>();
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly GhostWindow ghost = new GhostWindow();
        const double Gap = 6;   // space between dock and screen edge
        double slot, magSlot, bgH, H, W, last, dt, mag, hide, labelA, hoverLeftAt, bgL, bgR, bgTop;
        bool looping, hovering, menuOpen, dropActive, dragOut, trashFull, tiles;
        DockItem press, drag, labelItem, dropHot, trash;
        Point pressAt;
        StackWindow stack;
        ContextMenu openMenu;
        readonly PreviewWindow preview = new PreviewWindow();
        DockItem hoverItem, lastHot;
        double hoverSince;
        DispatcherTimer grace;
        int position;
        Rect monitor, reserved;
        // Original live sampling; wallpaper frost remains available when Live glass is switched off.
        bool live;
        long glassSum;
        double wallX, wallY;   // where the backdrop image sits on the canvas
        readonly DispatcherTimer glassTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };

        public DockWindow(string device = null)
        {
            Device = device;
            // WPF chooses its native creation monitor before SourceInitialized registers the appbar.
            var initial = MonitorBounds;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = initial.Left; Top = initial.Top;
            Title = "OpenDock";
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
            ShowInTaskbar = false; Topmost = true; ResizeMode = ResizeMode.NoResize; ShowActivated = false;
            AllowDrop = true;
            RenderOptions.SetBitmapScalingMode(canvas, BitmapScalingMode.HighQuality);
            wall.Clip = wallClip;
            label.Child = labelText;
            label.SetResourceReference(Border.BackgroundProperty, "Bubble");
            label.SetResourceReference(Border.BorderBrushProperty, "MenuBorder");
            label.BorderThickness = new Thickness(1);
            labelText.SetResourceReference(TextBlock.ForegroundProperty, "BubbleFg");
            bg.SetResourceReference(Border.BackgroundProperty, "DockTint");
            bg.SetResourceReference(Border.BorderBrushProperty, "DockBorder");
            sheen.SetResourceReference(Border.BackgroundProperty, "DockSheen");
            bg.Child = sheen;
            outline.SetResourceReference(Border.BorderBrushProperty, "DockOutline");
            canvas.Children.Add(wall); canvas.Children.Add(outline); canvas.Children.Add(bg); canvas.Children.Add(layer); canvas.Children.Add(label); canvas.Children.Add(edge);
            Content = canvas;

            MouseEventHandler enter = delegate { App.Dock = this; hovering = true; StartLoop(); };
            bg.MouseEnter += enter; edge.MouseEnter += enter;
            bg.MouseRightButtonUp += (s, e) => { var cm = new ContextMenu(); DockOptions(cm); OpenMenu(cm, bg, true); e.Handled = true; };
            DragEnter += OnDragOver; DragOver += OnDragOver; Drop += OnDrop;
            DragLeave += delegate { dropActive = false; SetDropHot(null); springItem = null; };

            SourceInitialized += delegate
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                Native.MakeToolWindow(hwnd, true);
                Bar = new AppBar(this, Native.ABE_BOTTOM, () => Screen.Bounds);
                Bar.Placed = rc => { reserved = DisplayLayout.Dip(rc); Position(); };
                Bar.FullScreen = OnFullScreen;
                Place();
            };

            AddFixed("finder", "Finder");
            AddFixed("launchpad", "Launchpad");
            if(Workspace.Data.ShowShelf) AddFixed("shelf", "Shelf");
            foreach (var p in Workspace.Pins(Device)) AddPin(p, items.Count);
            AddFixed("sep", "");
            AddFixed("stack", "Downloads");
            trash = AddFixed("trash", "Trash");
            tiles = Settings.IconTiles;

            App.WindowsChanged += Sync;
            preview.StillHovered = it => it != null && it == lastHot;
            App.WindowFlashed += OnFlash;
            App.ForegroundChanged += OnForeground;
            Apps.Changed += OnAppsLoaded;
            Wallpaper.Changed += OnWallpaper;
            glassTimer.Tick += delegate { RefreshGlass(); };
            wall.Source = Wallpaper.Blurred;
            Settings.Changed += OnSettings;
            Theme.Changed += ReloadIcons;
            trashTimer.Tick += delegate { UpdateTrash(); };
            trashTimer.Start();
            Closed += delegate
            {
                closed = true;
                if (Bar != null) Bar.Remove();
                CompositionTarget.Rendering -= Frame; looping = false;
                glassTimer.Stop(); trashTimer.Stop(); if (grace != null) grace.Stop();
                App.WindowsChanged -= Sync; App.WindowFlashed -= OnFlash; App.ForegroundChanged -= OnForeground;
                Apps.Changed -= OnAppsLoaded; Wallpaper.Changed -= OnWallpaper; Settings.Changed -= OnSettings; Theme.Changed -= ReloadIcons;
                if (openMenu != null) openMenu.IsOpen = false;
                if (stack != null) stack.Close();
                preview.Close2(); preview.Close(); ghost.Close();
            };
        }

        void OnForeground(IntPtr h)
        {
            glassTimer.Interval = TimeSpan.FromMilliseconds(80);
            h = Native.GetAncestor(h, 2);
            foreach (var it in items.Where(i => i.Attention && i.Wins.Any(w => w.Hwnd == h)))
            {
                it.Attention = false;
                it.Dot.SetResourceReference(Shape.FillProperty, "Fg");
            }
            OnFullScreen(true);
        }

        void OnWallpaper() { if (!live) { wall.Source = Wallpaper.Blurred; StartLoop(); } }
        void OnSettings()
        {
            if (closed) return;
            if (tiles != Settings.IconTiles || iconRevision != Settings.IconRevision) { tiles = Settings.IconTiles; iconRevision=Settings.IconRevision; ReloadIcons(); }
            Place();
        }

        // ---------- placement ----------

        public void Place()
        {
            if (closed || Bar == null) return;
            position = Settings.DockPosition >= 0 && Settings.DockPosition <= 2 ? Settings.DockPosition : 0;
            monitor = MonitorBounds;
            preview.Close2();
            if (stack != null && stack.IsVisible) stack.Dismiss();
            if (openMenu != null) openMenu.IsOpen = false;
            live = Glass.Exclude(new WindowInteropHelper(this).Handle);
            glassSum = 0;
            if (live) glassTimer.Start(); else { glassTimer.Stop(); wall.Source = Wallpaper.Blurred; }
            slot = Math.Max(36, Math.Min(96, Settings.IconSize));
            magSlot = Settings.Magnify ? Math.Max(Settings.MagnifySize, slot) : slot;
            bgH = Math.Round(slot + 12);
            H = Math.Ceiling(Math.Max(magSlot, slot * 1.65) + (position == 0 ? 72 : 220));
            W = position == 0 ? monitor.Width : monitor.Height;
            Bar.SetEdge(position == 1 ? Native.ABE_LEFT : position == 2 ? Native.ABE_RIGHT : Native.ABE_BOTTOM);
            foreach (var it in items) SizeRoot(it);
            if (Settings.AutoHide)
            {
                Bar.Remove();
                reserved = monitor;
                Position();
            }
            else Bar.Reserve((int)Math.Round((bgH + Gap + 4) * App.Dpi)); // calls Position via Placed
        }

        void Position()
        {
            W = position == 0 ? reserved.Width : reserved.Height;
            Width = position == 0 ? W : H; Height = position == 0 ? H : W;
            Left = position == 1 ? reserved.Left : position == 2 ? reserved.Right - H : reserved.Left;
            Top = position == 0 ? reserved.Bottom - H : reserved.Top;
            if (!live)
            {
                wall.Width = monitor.Width; wall.Height = monitor.Height;
                wallX = monitor.Left - Left; wallY = monitor.Top - Top;
                Canvas.SetLeft(wall, wallX); Canvas.SetTop(wall, wallY);
            }
            SetRect(edge, new Rect(0, H - 2, W, 2));
            foreach (var it in items)
            {
                it.X = double.NaN;
                it.Root.RenderTransformOrigin = position == 1 ? new Point(0, 0.5) : position == 2 ? new Point(1, 0.5) : new Point(0.5, 1);
            }
            StartLoop();
        }

        Point ToWindow(Point p) { return DisplayLayout.ToWindow(p, position, W, H); }
        Point Logical(Point p) { return DisplayLayout.FromWindow(p, position, W, H); }
        Point ScreenPoint(Point p) { var at = ToWindow(p); return new Point(Left + at.X, Top + at.Y); }
        Rect Physical(Rect r) { return new Rect(ToWindow(r.TopLeft), ToWindow(r.BottomRight)); }
        void SetRect(FrameworkElement element, Rect r)
        {
            r = Physical(r);
            Canvas.SetLeft(element, r.Left); Canvas.SetTop(element, r.Top);
            element.Width = r.Width; element.Height = r.Height;
        }

        // Frosts whatever is behind the dock: a 1/4-size capture around the pane (with room for it to widen while
        // magnifying), blurred, and redone only when the picture changes.
        void RefreshGlass()
        {
            if (!IsVisible || hide > 0.99 || bgR <= bgL) return;
            double d = App.Dpi, x0 = Math.Max(0, Math.Floor(bgL - magSlot)), x1 = Math.Min(W, Math.Ceiling(bgR + magSlot)), y0 = Math.Max(0, Math.Floor(bgTop - 8));
            var area = Physical(new Rect(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, H - y0)));
            var shot = Glass.Behind((int)Math.Round((Left + area.Left) * d), (int)Math.Round((Top + area.Top) * d), (int)Math.Round(area.Width * d), (int)Math.Round(area.Height * d), 1, ref glassSum);
            // Nothing changed: check less and less often (down to once a second). A change brings back ~12 checks a second.
            if (shot == null) { glassTimer.Interval = TimeSpan.FromMilliseconds(Math.Min(1000, glassTimer.Interval.TotalMilliseconds * 1.5)); return; }
            glassTimer.Interval = TimeSpan.FromMilliseconds(80);
            wall.Source = shot;
            wallX = area.Left; wallY = area.Top; wall.Width = area.Width; wall.Height = area.Height;
            Canvas.SetLeft(wall, wallX); Canvas.SetTop(wall, wallY);
            StartLoop();   // re-clip to the pane at the new spot
        }

        public void OnFullScreen(bool fs)
        {
            if (closed) return;
            var target=MonitorBounds; Native.RECT actual;
            // Allow a pixel of slack: at 125%/150% scaling the DIP conversion rounds, and a false "moved" here
            // would re-register the appbar (reflowing every maximized window) and close open menus.
            var slack=target; slack.Inflate(1.5/App.Dpi,1.5/App.Dpi);
            if(Bar!=null&&!Bar.IsPlacing&&!repairingPlacement&&(monitor!=target||Native.GetWindowRect(new WindowInteropHelper(this).Handle,out actual)&&!slack.Contains(DisplayLayout.Dip(actual))))
            {
                // A display reconnect or Explorer can relocate the native window without changing our assigned device.
                repairingPlacement=true;
                try { Bar.Remove(); Place(); }
                finally { repairingPlacement=false; }
            }
            bool covered = Windows.FullscreenOn(Screen);
            if (covered && IsVisible)
            {
                preview.Close2();
                if (stack != null && stack.IsVisible) stack.Dismiss();
                if (openMenu != null) openMenu.IsOpen = false;
            }
            Visibility = covered ? Visibility.Hidden : Visibility.Visible;
            if (!covered) StartLoop();
            if (App.MenuBar != null && Settings.ShowMenuBar && Screen.Device == DisplayLayout.Selected.Device) App.MenuBar.Visibility = Visibility;
        }

        public void BringToFront()
        {
            Native.SetWindowPos(new WindowInteropHelper(this).Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        // ---------- items ----------

        DockItem AddFixed(string kind, string name)
        {
            return Add(new DockItem { Kind = kind, Name = name, P = 1 }, items.Count);
        }

        void AddPin(string pin, int index)
        {
            int bar = pin.IndexOf('|');
            if (bar < 0) return;
            string target = pin.Substring(bar + 1);
            var it = new DockItem { Name = pin.Substring(0, bar), Target = target, Pinned = true, P = 1 };
            it.IconPath = target.StartsWith("app:") ? "shell:AppsFolder\\" + target.Substring(4) : target;
            it.ComputeKeys();
            Add(it, index);
        }

        DockItem Add(DockItem it, int index)
        {
            it.Root = new Border { Background = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255)), RenderTransform = it.Scale, RenderTransformOrigin = new Point(0.5, 1) };
            if (it.IsSep)
            {
                var line = new Rectangle { Width = 1, VerticalAlignment = VerticalAlignment.Center };
                line.SetResourceReference(Shape.FillProperty, "DockBorder");
                it.Root.Child = line;
                it.Root.MouseRightButtonUp += (s, e) => { var cm = new ContextMenu(); DockOptions(cm); OpenMenu(cm, it.Root, false); e.Handled = true; };
            }
            else
            {
                it.Img = new Image();
                var face=new Grid(); face.Children.Add(it.Img);
                it.BadgeText=new TextBlock { Foreground=Brushes.White,FontSize=10,FontWeight=FontWeights.SemiBold,Margin=new Thickness(4,0,4,0) };
                it.Badge=new Border { Child=it.BadgeText,Background=Theme.B("#E33A46"),CornerRadius=new CornerRadius(7),HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Top,MinWidth=16,Visibility=Visibility.Collapsed,IsHitTestVisible=false };
                face.Children.Add(it.Badge); it.Root.Child = face;
                it.Dot = new Ellipse { Width = 4, Height = 4, Opacity = 0, IsHitTestVisible = false };
                it.Dot.SetResourceReference(Shape.FillProperty, "Fg");
                layer.Children.Add(it.Dot);
                it.Root.MouseLeftButtonDown += (s, e) => { press = it; pressAt = Logical(e.GetPosition(this)); it.Root.CaptureMouse(); it.Img.Opacity = 0.65; e.Handled = true; };
                it.Root.MouseMove += (s, e) =>
                {
                    if (press != it) return;
                    var p = Logical(e.GetPosition(this));
                    if (drag == null && (p - pressAt).Length > 6 && (it.Kind == "app" || it.Kind == "shelf")) BeginDrag(it);
                    if (drag == it) DragMove(p);
                };
                it.Root.MouseLeftButtonUp += (s, e) =>
                {
                    if (press != it) return;
                    press = null;
                    it.Root.ReleaseMouseCapture();
                    it.Img.Opacity = 1;
                    if (drag == it) EndDrag(Logical(e.GetPosition(this))); else Click(it);
                };
                it.Root.MouseRightButtonUp += (s, e) => { preview.Close2(); ShowMenu(it); e.Handled = true; };
                it.Root.MouseUp += (s, e) => { if (e.ChangedButton == MouseButton.Middle) { OpenNew(it); e.Handled = true; } };
                LoadIcon(it);
            }
            it.Root.MouseEnter += delegate { hovering = true; StartLoop(); };
            layer.Children.Add(it.Root);
            items.Insert(index, it);
            SizeRoot(it);
            StartLoop();
            return it;
        }

        void SizeRoot(DockItem it)
        {
            if (slot == 0) return;
            if (it.IsSep) { it.Root.Width = slot * 0.34; it.Root.Height = slot * 0.62; ((Rectangle)it.Root.Child).Height = slot * 0.62; }
            else it.Root.Width = it.Root.Height = slot;
        }

        void LoadIcon(DockItem it)
        {
            int version=++it.IconVersion;
            Action<ImageSource> set=image=> { if(version==it.IconVersion) it.Img.Source=image; };
            string custom=CustomIcons.Get(it.IconKey);
            if(custom!=null) { Icons.Get(custom,true,set); return; }
            switch (it.Kind)
            {
                case "finder": it.Img.Source = Art.Finder; break;
                case "launchpad": it.Img.Source = Art.Launchpad; break;
                case "stack": it.Img.Source = Art.Downloads; break;
                case "shelf": it.Img.Source = Art.Shelf; break;
                case "trash": trashFull = Launcher.TrashFull(); it.Img.Source = Art.Trash(trashFull); break;
                case "internal": it.Img.Source = it.RunKey == "opendock:settings" ? Art.Settings : Art.Computer; break;
                default: Icons.Get(it.IconPath, true, set); break;
            }
        }

        void ReloadIcons()
        {
            Icons.ClearCache();
            foreach (var it in items.Where(i => !i.IsSep)) LoadIcon(it);
        }

        void UpdateTrash()
        {
            bool full = Launcher.TrashFull();
            if (full == trashFull) return;
            trashFull = full;
            LoadIcon(trash);
        }

        public void Pin(AppEntry a)
        {
            if (items.Any(i => i.Pinned && i.Target == a.Target)) return;
            int at = PinIndex();
            AddPin(a.Name + "|" + a.Target, at);
            items[at].P = 0;
            SavePins();
            App.QueueRefresh();
        }

        int SepIndex() { return items.FindIndex(i => i.IsSep); }
        int PinIndex() { int i = items.FindIndex(x => x.Kind == "app" && !x.Pinned); return i < 0 ? SepIndex() : i; }
        int FirstMovable() { return items.FindIndex(i => i.Kind != "finder" && i.Kind != "launchpad" && i.Kind != "shelf"); }

        public void RefreshShelf()
        {
            if(closed) return;
            var shelf=items.FirstOrDefault(i=>i.Kind=="shelf");
            if(Workspace.Data.ShowShelf)
            {
                if(shelf==null) Add(new DockItem { Kind="shelf",Name="Shelf",P=1 },Math.Min(2,items.Count));
                return;
            }
            if(shelf==null) return;
            if(press==shelf) { shelf.Root.ReleaseMouseCapture(); press=null; }
            if(drag==shelf) { drag=null; ghost.Hide(); }
            if(hoverItem==shelf) hoverItem=null;
            if(lastHot==shelf) lastHot=null;
            if(labelItem==shelf) { labelItem=null; labelA=0; label.Opacity=0; }
            if(dropHot==shelf) SetDropHot(null);
            if(springItem==shelf) springItem=null;
            preview.Close2(); if(openMenu!=null) openMenu.IsOpen=false;
            layer.Children.Remove(shelf.Root); layer.Children.Remove(shelf.Dot); items.Remove(shelf); StartLoop();
        }

        void SavePins()
        {
            try
            {
                Workspace.SetPins(Device, items.Where(i => i.Kind == "app" && i.Pinned).Select(i => i.Name + "|" + i.Target).ToList());
                Settings.Save(); App.SyncPins(this);
            }
            catch { RefreshPins(); App.SyncPins(this); throw; }
        }

        public void RefreshPins()
        {
            if (closed) return;
            if (drag != null) { pinsNeedRefresh = true; return; }
            pinsNeedRefresh = false;
            foreach (var it in items.Where(i => i.Kind == "app").ToList())
            {
                layer.Children.Remove(it.Root); layer.Children.Remove(it.Dot); items.Remove(it);
            }
            foreach (var pin in Workspace.Pins(Device)) AddPin(pin, PinIndex());
            Sync(App.Wins);
        }

        static readonly string[] Defaults = { "Microsoft Edge", "Google Chrome", "Firefox", "Brave", "Spotify", "Discord", "Steam", "WhatsApp", "Visual Studio Code", "Terminal", "Notepad", "Calculator", "Photos", "Settings" };

        bool sharpIcons;

        void OnAppsLoaded()
        {
            // Store apps get their crisp package logos once the app list (with package folders) is known.
            if (!sharpIcons) { sharpIcons = true; ReloadIcons(); }
            if (Settings.FirstRun)
            {
                foreach (var name in Defaults)
                {
                    var a = Apps.All.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (a != null && items.Count(i => i.Pinned) < 9) AddPin(a.Name + "|" + a.Target, PinIndex());
                }
                Settings.FirstRun = false;
                SavePins();
            }
            foreach (var it in items) if (it.RunKey == null) it.ComputeKeys();
            App.QueueRefresh();
        }

        // ---------- running windows ----------

        public void RefreshBadges()
        {
            foreach(var it in items.Where(i=>!i.IsSep))
            {
                string text=it.Kind=="shelf" && Workspace.Data.Shelf.Count>0 ? Workspace.Data.Shelf.Count.ToString() : it.Kind=="stack" && Living.Downloads>0 ? "↓"+Living.Downloads : it.Kind=="launchpad" ? Living.TimerText : "";   // red badge = something waiting, not a window count
                if(Workspace.Restoring&&it.Kind=="finder") text="…";
                it.BadgeText.Text=text; it.Badge.Visibility=Workspace.Data.LivingIcons&&text.Length>0?Visibility.Visible:Visibility.Collapsed;
            }
        }

        public void Sync(List<WinInfo> wins)
        {
            if (closed) return;
            foreach (var it in items) it.Wins.Clear();
            var loose = new List<WinInfo>();
            foreach (var w in wins)
            {
                var it = w.InternalKind == "shelf" ? items.FirstOrDefault(i=>i.Kind=="shelf") : w.InternalKind != null ? items.FirstOrDefault(i => i.Kind == "internal" && i.RunKey == w.Key)
                    : items.FirstOrDefault(i => i.Kind == "app" && i.Pinned && i.Matches(w)) ?? items.FirstOrDefault(i => i.Kind == "app" && !i.Pinned && i.Matches(w));
                if (it != null) it.Wins.Add(w); else if(w.InternalKind!="shelf") loose.Add(w);
            }
            foreach (var g in loose.Where(w => w.Key != null && (Settings.ShowRunning || w.InternalKind != null)).GroupBy(w => w.Key, StringComparer.OrdinalIgnoreCase))
            {
                var it = NewRunning(g.First());
                it.Wins.AddRange(g);
                Add(it, SepIndex());
            }
            double now = clock.Elapsed.TotalSeconds;
            foreach (var it in items.Where(i => i.Kind == "app" || i.Kind == "internal"))
            {
                if (it.Wins.Count > 0) it.GoneAt = -1;
                else if (it.GoneAt < 0) it.GoneAt = now;
                if (!it.Pinned) it.PWant = it.Wins.Count > 0 || now - it.GoneAt < 0.6 ? 1 : 0;
                if (!it.Pinned && it.PWant == 1 && it.Wins.Count == 0) WaitThenSync();
                if (it.BounceT0 >= 0 && it.Wins.Count > 0 && !it.Attention) it.BounceStop = Math.Min(it.BounceStop, now);   // launched: stop bouncing
            }
            RefreshBadges(); StartLoop();
        }

        void WaitThenSync()
        {
            if (grace == null)
            {
                grace = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.65) };
                grace.Tick += delegate { grace.Stop(); Sync(App.Wins); };
            }
            if (!grace.IsEnabled) grace.Start();
        }

        DockItem NewRunning(WinInfo w)
        {
            if (w.InternalKind != null) return new DockItem { Kind = "internal", RunKey = w.Key,
                Name = w.InternalKind == "settings" ? "System Settings" : w.InternalKind == "about" ? "About This PC" : "OpenDock Windows" };
            var it = new DockItem { RunKey = w.Key };
            var e = w.IsFrameHost ? Apps.ById(w.Aumid) : (Apps.ByExe(w.Exe) ?? (w.Aumid != null ? Apps.ById(w.Aumid) : null));
            if (e != null) { it.Name = e.Name; it.Target = e.Target; it.IconPath = e.Icon; }
            else if (w.IsFrameHost && w.Aumid != null) { it.Name = w.Title; it.Target = "app:" + w.Aumid; it.IconPath = "shell:AppsFolder\\" + w.Aumid; }
            // Packaged desktop apps (Claude, ...) live in a versioned WindowsApps folder that each update deletes;
            // pin them by app ID so the pin survives updates.
            else if (w.Aumid != null && w.Exe != null && w.Exe.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) >= 0) { it.Name = Apps.FriendlyName(w.Exe); it.Target = "app:" + w.Aumid; it.IconPath = w.Exe; }
            else { it.Name = Apps.FriendlyName(w.Exe); it.Target = w.Exe; it.IconPath = w.Exe; }
            it.ComputeKeys();
            return it;
        }

        // ---------- clicks ----------

        // An app flashing its taskbar button (new message, finished download...): bounce three times, like macOS,
        // and turn its dot red until you switch to it.
        void OnFlash(IntPtr h)
        {
            var it = items.FirstOrDefault(i => i.Wins.Any(w => w.Hwnd == h));
            if (it == null || it.Wins.Any(w => w.Hwnd == Native.GetAncestor(Native.GetForegroundWindow(), 2))) return;
            it.Attention = true;
            it.Dot.Fill = Theme.B("#FF453A");
            if (it.BounceT0 < 0) { it.BounceT0 = clock.Elapsed.TotalSeconds; it.BounceStop = it.BounceT0 + 2 * 0.68 + 0.01; }
            StartLoop();
        }

        // Middle-click or Shift+click: a new window / instance, like the taskbar.
        void OpenNew(DockItem it)
        {
            preview.Close2();
            if (it.Kind == "finder") { App.NewFinder(); return; }
            if (it.Kind == "internal") { if (it.Wins.Count > 0) FocusApp(it); return; }
            if (it.Kind != "app") return;
            Launcher.Open(it.Target);
            it.BounceT0 = clock.Elapsed.TotalSeconds; it.BounceStop = it.BounceT0;   // one bounce
            StartLoop();
        }

        List<IntPtr> WindowsOf(DockItem it)
        {
            if (it.Kind == "finder") return App.Finders.Select(f => new WindowInteropHelper(f).Handle).ToList();
            return it.Kind == "app" || it.Kind == "internal" || it.Kind == "shelf" ? it.Wins.Select(w => w.Hwnd).ToList() : null;
        }

        // Hover a running app for a moment and its windows' live previews appear; moving to another running app
        // switches them instantly (taskbar behaviour).
        bool UpdatePreview(DockItem hot, double now, double iconBase)
        {
            var wins = hot == null || dropActive || menuOpen || drag != null ? null : WindowsOf(hot);
            var target = wins != null && wins.Count > 0 ? hot : null;
            if (target != hoverItem) { hoverItem = target; hoverSince = now; }
            if (target != null)
            {
                if (preview.Item != target && (preview.IsVisible || now - hoverSince > 0.55))
                    preview.ShowFor(target, wins, target.Img.Source, ScreenPoint(new Point(target.CX, iconBase - slot * target.S)), position, MonitorWorkArea);
                return preview.Item != target;   // keep the loop running until it's shown
            }
            if (preview.IsVisible && !preview.MouseInside) preview.MaybeHide();
            return false;
        }

        void Click(DockItem it)
        {
            preview.Close2();
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && (it.Kind == "app" || it.Kind == "finder")) { OpenNew(it); return; }
            switch (it.Kind)
            {
                case "finder":
                    if (App.Finders.Count == 0) App.NewFinder();
                    else foreach (var f in App.Finders.ToList()) { if (f.WindowState == WindowState.Minimized) f.WindowState = WindowState.Normal; f.Activate(); }
                    break;
                case "launchpad": App.ToggleLaunchpad(); break;
                case "shelf": ShelfWindow.Open(); break;
                case "stack": ToggleStack(it); break;
                case "trash": Launcher.Shell("shell:RecycleBinFolder"); break;
                case "internal": if (it.Wins.Count > 0) FocusApp(it); break;
                default:
                    if (it.Wins.Count == 0) Launch(it, null);
                    else FocusApp(it);
                    break;
            }
        }

        void Launch(DockItem it, string args)
        {
            if (Directory.Exists(it.Target)) { App.NewFinder(it.Target); return; }
            Launcher.Open(it.Target, args);
            if (Settings.Bounce && it.Wins.Count == 0)
            {
                it.BounceT0 = clock.Elapsed.TotalSeconds;
                it.BounceStop = it.BounceT0 + 10;
                StartLoop();
            }
        }

        static void FocusApp(DockItem it)
        {
            var fg = Native.GetForegroundWindow();
            if (it.Wins.Any(w => w.Hwnd == fg && !Native.IsIconic(w.Hwnd)))   // a minimized window can still be "foreground"; that should restore, not minimize
            {
                foreach (var w in it.Wins) Native.ShowWindow(w.Hwnd, Native.SW_MINIMIZE);
                return;
            }
            if (it.Wins.All(w => Native.IsIconic(w.Hwnd)))
                for (int i = it.Wins.Count - 1; i > 0; i--) Native.ShowWindow(it.Wins[i].Hwnd, Native.SW_SHOWNOACTIVATE);
            Native.Activate(it.Wins[0].Hwnd);
        }

        void ToggleStack(DockItem it)
        {
            if (stack != null && stack.IsVisible) { stack.Dismiss(); return; }
            stack = new StackWindow(Launcher.Downloads, "Downloads");
            stack.ShowAbove(ScreenPoint(new Point(it.CX, bgTop - 8)), position, MonitorWorkArea);
        }

        // ---------- menus ----------

        static MenuItem Item(string text, Action a, bool enabled = true)
        {
            var mi = new MenuItem { Header = text, IsEnabled = enabled };
            mi.Click += delegate { a(); };
            return mi;
        }

        void DockOptions(ContextMenu cm)
        {
            cm.Items.Add(Item(Settings.Magnify ? "Turn Magnification Off" : "Turn Magnification On", () => { Settings.Magnify = !Settings.Magnify; Settings.Apply(); }));
            cm.Items.Add(Item(Settings.AutoHide ? "Turn Hiding Off" : "Turn Hiding On", () => { Settings.AutoHide = !Settings.AutoHide; Settings.Apply(); }));
            cm.Items.Add(new Separator());
            cm.Items.Add(Item("Shelf…", ShelfWindow.Open));
            cm.Items.Add(Item("Desktop Scenes & Dock Studio…", ()=>App.OpenSettings("Workspace")));
            cm.Items.Add(Item("Task Manager", () => Launcher.Shell("taskmgr.exe")));
            cm.Items.Add(Item("Dock Settings…", () => App.OpenSettings("Desktop & Dock")));
        }

        void ShowMenu(DockItem it)
        {
            var cm = new ContextMenu();
            if ((it.Kind == "app" || it.Kind == "internal") && it.Wins.Count > 0)
            {
                foreach (var w in it.Wins.Take(12))
                {
                    var win = w;
                    cm.Items.Add(Item(win.Title.Length > 50 ? win.Title.Substring(0, 49) + "…" : win.Title, () => Native.Activate(win.Hwnd)));
                }
                cm.Items.Add(new Separator());
            }
            switch (it.Kind)
            {
                case "internal":
                    cm.Items.Add(Item("Hide", () => { foreach (var w in it.Wins) Native.ShowWindow(w.Hwnd, Native.SW_MINIMIZE); }));
                    cm.Items.Add(Item("Close Windows", () => { foreach (var w in it.Wins) Native.PostMessage(w.Hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }));
                    break;
                case "app":
                    if (it.Wins.Count == 0) cm.Items.Add(Item("Open", () => Launch(it, null)));
                    var keep = Item("Keep in Dock", () =>
                    {
                        it.Pinned = !it.Pinned;
                        if (!it.Pinned && it.Wins.Count == 0) it.PWant = 0;
                        SavePins(); StartLoop();
                    });
                    keep.IsChecked = it.Pinned;
                    cm.Items.Add(keep);
                    string path = it.FilePath;
                    if (path != null && File.Exists(path)) cm.Items.Add(Item("Show in Finder", () => App.ShowInFinder(path)));
                    if (it.Wins.Count > 0)
                    {
                        cm.Items.Add(new Separator());
                        cm.Items.Add(Item("Hide", () => { foreach (var w in it.Wins) Native.ShowWindow(w.Hwnd, Native.SW_MINIMIZE); }));
                        cm.Items.Add(Item("Quit", () => { foreach (var w in it.Wins) Native.PostMessage(w.Hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }));
                    }
                    break;
                case "finder":
                    foreach (var f in App.Finders.ToList()) { var fw = f; cm.Items.Add(Item(fw.Title, () => fw.Activate())); }
                    if (App.Finders.Count > 0) cm.Items.Add(new Separator());
                    cm.Items.Add(Item("New Finder Window", () => App.NewFinder()));
                    break;
                case "launchpad":
                    cm.Items.Add(Item("Open Launchpad", App.ToggleLaunchpad));
                    break;
                case "shelf":
                    cm.Items.Add(Item("Open Shelf",ShelfWindow.Open)); cm.Items.Add(Item("Collect Clipboard",ShelfWindow.AddClipboard));
                    cm.Items.Add(new Separator());
                    cm.Items.Add(Item("Remove from Dock",()=>Workspace.SetShelfShown(false))); break;
                case "stack":
                    cm.Items.Add(Item("Open", () => ToggleStack(it)));
                    cm.Items.Add(Item("Open in Finder", () => App.NewFinder(Launcher.Downloads)));
                    break;
                case "trash":
                    cm.Items.Add(Item("Open", () => Launcher.Shell("shell:RecycleBinFolder")));
                    cm.Items.Add(new Separator());
                    cm.Items.Add(Item("Empty Trash", () => { Native.SHEmptyRecycleBin(IntPtr.Zero, null, 0); UpdateTrash(); }, trashFull));
                    break;
            }
            OpenMenu(cm, it.Root, false);
        }

        void OpenMenu(ContextMenu cm, FrameworkElement target, bool atMouse)
        {
            cm.PlacementTarget = target;
            if (atMouse) cm.Placement = PlacementMode.MousePoint;
            else
            {
                cm.Placement = PlacementMode.Custom;
                cm.CustomPopupPlacementCallback = (popup, t, o) => new[] { new CustomPopupPlacement(
                    position == 1 ? new Point(t.Width + 6, (t.Height - popup.Height) / 2)
                    : position == 2 ? new Point(-popup.Width - 6, (t.Height - popup.Height) / 2)
                    : new Point((t.Width - popup.Width) / 2, -popup.Height + 10),
                    position == 0 ? PopupPrimaryAxis.Horizontal : PopupPrimaryAxis.Vertical) };
            }
            menuOpen = true;
            openMenu = cm;
            cm.Closed += delegate { if (openMenu == cm) { menuOpen = false; openMenu = null; } StartLoop(); };
            cm.IsOpen = true;
            StartLoop();
        }

        // ---------- drag to reorder / remove ----------

        void BeginDrag(DockItem it)
        {
            drag = it;
            ghost.Begin(it.Img.Source, slot);
            StartLoop();
        }

        void DragMove(Point p)
        {
            ghost.MoveTo(ScreenPoint(p));
            dragOut = p.Y < bgTop - slot * 0.9;
            ghost.ShowRemove(dragOut && (drag.Pinned || drag.Kind=="shelf"));
            drag.PWant = dragOut ? 0 : 1;
            if (dragOut || drag.Kind=="shelf") return;
            items.Remove(drag);
            int lo = FirstMovable(), hi = SepIndex(), at = lo;
            for (int i = lo; i < hi; i++) if (p.X > items[i].X) at = i + 1;
            items.Insert(at, drag);
        }

        void EndDrag(Point p)
        {
            var it = drag;
            drag = null;
            if(it.Kind=="shelf")
            {
                if(dragOut)
                {
                    try { Workspace.SetShelfShown(false); ghost.Poof(); }
                    catch { ghost.Hide(); it.PWant=1; dragOut=false; StartLoop(); throw; }
                }
                else { ghost.Hide(); it.PWant=1; }
                dragOut=false; if(pinsNeedRefresh) RefreshPins(); StartLoop(); return;
            }
            if (dragOut && it.Pinned)
            {
                ghost.Poof();
                it.Pinned = false;
                it.PWant = it.Wins.Count > 0 ? 1 : 0;
                if (it.Wins.Count > 0) { items.Remove(it); items.Insert(SepIndex(), it); }
            }
            else
            {
                ghost.Hide();
                it.PWant = 1;
                if (!dragOut) it.Pinned = true;
            }
            dragOut = false;
            SavePins();
            if (pinsNeedRefresh) RefreshPins();
            StartLoop();
        }

        // ---------- files dropped from Explorer / Finder ----------

        DockItem HitItem(Point p)
        {
            return items.FirstOrDefault(i => !i.IsSep && i.P > 0.5 && Math.Abs(p.X - i.CX) <= slot * i.S / 2 && p.Y >= bgTop - slot * (i.S - 1) - 6);
        }

        void SetDropHot(DockItem it)
        {
            if (dropHot != null && dropHot.Img != null) dropHot.Img.Opacity = 1;
            dropHot = it;
            if (it != null && it.Img != null) it.Img.Opacity = 0.6;
        }

        DockItem springItem;
        DateTime springSince;
        bool springDone;

        // Spring-loading (macOS) / taskbar drag-hover (Windows): hold anything you're dragging over a running app's
        // icon and its window comes to the front, so you can carry on and drop into it.
        void SpringLoad(DockItem it)
        {
            if (it != springItem) { springItem = it; springSince = DateTime.Now; springDone = false; return; }
            if (it == null || springDone || (DateTime.Now - springSince).TotalSeconds < 0.6) return;
            springDone = true;
            if (it.Kind == "finder")
            {
                foreach (var f in App.Finders.ToList()) { if (f.WindowState == WindowState.Minimized) f.WindowState = WindowState.Normal; f.Activate(); }
                return;
            }
            if (it.Kind != "app" || it.Wins.Count == 0) return;
            foreach (var w in it.Wins.Where(w => Native.IsIconic(w.Hwnd))) Native.ShowWindow(w.Hwnd, Native.SW_RESTORE);
            Native.Activate(it.Wins[0].Hwnd);
            it.Img.BeginAnimation(OpacityProperty, new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(300)) { FillBehavior = FillBehavior.Stop });   // blink, like macOS
        }

        void OnDragOver(object s, DragEventArgs e)
        {
            App.Dock = this;
            e.Handled = true;
            var at = HitItem(Logical(e.GetPosition(this)));
            SpringLoad(at != null && (at.Kind == "app" || at.Kind == "finder") ? at : null);
            dropActive = true; hovering = true; StartLoop();
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.None; return; }
            var it = at;
            SetDropHot(it != null && (it.Kind == "app" || it.Kind == "trash" || it.Kind == "finder") ? it : null);
            e.Effects = it == null ? DragDropEffects.Link : it.Kind == "trash" ? DragDropEffects.Move : DragDropEffects.Copy;
        }

        void OnDrop(object s, DragEventArgs e)
        {
            dropActive = false;
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            var p = Logical(e.GetPosition(this));
            var it = HitItem(p);
            SetDropHot(null);
            if (files == null || files.Length == 0) return;
            if (it == null)
            {
                foreach (var f in files)
                {
                    string name = Directory.Exists(f) ? new DirectoryInfo(f).Name : System.IO.Path.GetFileNameWithoutExtension(f);
                    int lo = FirstMovable(), hi = SepIndex(), at = lo;
                    for (int i = lo; i < hi; i++) if (p.X > items[i].X) at = i + 1;
                    AddPin(name + "|" + f, at);
                    items[at].P = 0;
                }
                SavePins();
                App.QueueRefresh();
            }
            else if (it.Kind == "trash") { Launcher.Recycle(files); UpdateTrash(); }
            else if (it.Kind == "shelf") { foreach(var file in files) Shelf.Add("file",file); ShelfWindow.Open(); }
            else if (it.Kind == "finder") foreach (var f in files) App.ShowInFinder(f);
            // Shift+drop on an app with several windows picks a window: the files are copied (clipboard + Shelf) and
            // that window comes forward, ready to paste. A plain drop opens the files with the app, as always.
            else if (it.Kind == "app" && it.Wins.Count>1 && (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                var menu=new ContextMenu();
                foreach(var value in it.Wins)
                {
                    var target=value;
                    menu.Items.Add(Item("Copy files and switch to: "+(target.Title.Length>60?target.Title.Substring(0,60)+"…":target.Title),()=>
                    {
                        foreach(var file in files) Shelf.Add("file",file);
                        var paths=new System.Collections.Specialized.StringCollection(); paths.AddRange(files);
                        try { Clipboard.SetFileDropList(paths); } catch(Exception ex) { App.Log(ex); }
                        Native.Activate(target.Hwnd);
                    }));
                }
                OpenMenu(menu,it.Root,false);
            }
            else if (it.Kind == "app") Launch(it, Launcher.Quote(files));
        }

        // ---------- animation loop (runs only while something moves) ----------

        void StartLoop()
        {
            if (closed || looping) return;
            looping = true;
            last = clock.Elapsed.TotalSeconds;
            CompositionTarget.Rendering += Frame;
        }

        bool Approach(ref double v, double want, double rate)
        {
            v += (want - v) * (1 - Math.Exp(-dt * rate));
            if (Math.Abs(want - v) < 0.002) { v = want; return false; }
            return true;
        }

        // Icons grow in / shrink out on an ease-in-out curve (gentle start and finish, no snap).
        static bool Tween(DockItem it, double now)
        {
            if (it.P == it.PWant) { it.PTarget = it.PWant; return false; }
            if (it.PTarget != it.PWant) { it.PFrom = it.P; it.PT0 = now; it.PTarget = it.PWant; }
            double t = Math.Min(1, (now - it.PT0) / (it.PWant < it.PFrom ? 0.55 : 0.45));
            double e = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
            it.P = t >= 1 ? it.PWant : it.PFrom + (it.PWant - it.PFrom) * e;
            return true;
        }

        static double Bump(double u, double r) { return u >= r ? 0 : 0.5 * (1 + Math.Cos(Math.PI * u / r)); }

        // Integral of Bump: how far an icon at distance d gets pushed sideways, so the icon under the cursor stays put.
        static double Shift(double d, double r)
        {
            double u = Math.Min(Math.Abs(d), r);
            return Math.Sign(d) * 0.5 * (u + r / Math.PI * Math.Sin(Math.PI * u / r));
        }

        double BaseW(DockItem i) { return (i.IsSep ? slot * 0.34 : slot) * i.P; }

        Point CursorPos()
        {
            Native.POINT p; Native.GetCursorPos(out p);
            return Logical(new Point(p.X / App.Dpi - Left, p.Y / App.Dpi - Top));
        }

        void Frame(object sender, EventArgs e)
        {
            if (slot == 0) return;
            double now = clock.Elapsed.TotalSeconds;
            dt = Math.Min(0.05, Math.Max(0.001, now - last));
            last = now;
            Point m = CursorPos();
            bool busy = false;

            double A = (magSlot / slot - 1) * mag, R = slot * 2.6;
            if (hovering && drag == null && !menuOpen && !dropActive)
            {
                double top = bgTop - slot * A - 6;
                if (m.X < bgL - 2 || m.X > bgR + 2 || m.Y < top || m.Y > H + 1) { hovering = false; hoverLeftAt = now; }
            }
            bool engaged = hovering || drag != null || menuOpen || dropActive || (stack != null && stack.IsVisible);
            busy |= Approach(ref mag, (hovering || dropActive) && drag == null && !menuOpen && Settings.Magnify ? 1 : 0, 16);
            busy |= Approach(ref hide, Settings.AutoHide && !engaged && now - hoverLeftAt > 0.35 ? 1 : 0, 11);
            edge.Visibility = Settings.AutoHide && hide > 0.5 ? Visibility.Visible : Visibility.Collapsed;

            for (int i = items.Count - 1; i >= 0; i--)
            {
                var it = items[i];
                busy |= Tween(it, now);
                if (it.P == 0 && it.PWant == 0 && it != drag)
                {
                    layer.Children.Remove(it.Root);
                    if (it.Dot != null) layer.Children.Remove(it.Dot);
                    items.RemoveAt(i);
                }
            }

            // Fit even a crowded vertical dock, including the extra room needed for magnification.
            double requested = Math.Max(36, Math.Min(96, Settings.IconSize));
            double requestedMag = Settings.Magnify ? Math.Max(Math.Min(160, Settings.MagnifySize), requested) : requested;
            double units = items.Sum(i => (i.IsSep ? 0.34 : 1) * i.P);
            double gaps = Math.Max(0, items.Sum(i => i.P) - 1);
            double fit = DisplayLayout.FitScale(W, requested * units + Settings.DockSpacing * gaps, (requestedMag - requested) * 2.6);
            slot = requested * fit; magSlot = requestedMag * fit;
            // Each gap grows and shrinks with its icon's presence.
            double sp = Settings.DockSpacing * fit, total = items.Sum(i => BaseW(i) + sp * i.P) - (items.Count > 0 ? sp * items[items.Count - 1].P : 0);
            double x = (W - total) / 2, k = 1 - Math.Exp(-dt * 18);
            foreach (var it in items)
            {
                double c = x + BaseW(it) / 2;
                if (double.IsNaN(it.X) || Math.Abs(c - it.X) < 0.2) it.X = c;
                else { it.X += (c - it.X) * k; busy = true; }
                x += BaseW(it) + sp * it.P;
            }

            A = (magSlot / slot - 1) * mag;
            double mx = m.X, hideY = hide * (bgH + Gap + 10);
            double bottom = H - Gap + hideY, iconBase = bottom - 6, pad = Math.Round(slot * 0.09);
            double L = items.Min(i => i.X - BaseW(i) / 2), Rr = items.Max(i => i.X + BaseW(i) / 2);
            bgL = L + A * Shift(L - mx, R) - pad;
            bgR = Rr + A * Shift(Rr - mx, R) + pad;
            bgTop = bottom - bgH;
            double radius = bgH * 0.29;
            SetRect(bg, new Rect(bgL, bgTop, Math.Max(0, bgR - bgL), bgH));
            bg.CornerRadius = new CornerRadius(radius);
            sheen.CornerRadius = new CornerRadius(Math.Max(0, radius - 1));
            SetRect(outline, new Rect(bgL - 1, bgTop - 1, Math.Max(0, bgR - bgL) + 2, bgH + 2));
            outline.CornerRadius = new CornerRadius(radius + 1);
            var paneRect = Physical(new Rect(bgL, bgTop, Math.Max(0, bgR - bgL), bgH));
            paneRect.Offset(-wallX, -wallY);
            wallClip.Rect = paneRect;
            wallClip.RadiusX = wallClip.RadiusY = radius;

            foreach (var it in items)
            {
                it.S = 1 + A * Bump(Math.Abs(it.X - mx), R);
                it.CX = it.X + A * Shift(it.X - mx, R);
                if (it.IsSep)
                {
                    it.Root.Opacity = it.P;
                    double sepW = slot * 0.34, sepH = slot * 0.62;
                    SetRect(it.Root, new Rect(it.CX - sepW / 2, bgTop + (bgH - sepH) / 2, sepW, sepH));
                    var line = (Rectangle)it.Root.Child;
                    line.Width = position == 0 ? 1 : sepH; line.Height = position == 0 ? sepH : 1;
                    continue;
                }
                double bounce = 0;
                if (it.BounceT0 >= 0)
                {
                    const double period = 0.68;
                    double t = now - it.BounceT0;
                    int cycles = (int)Math.Ceiling(Math.Max(0.01, it.BounceStop - it.BounceT0) / period);
                    if (t >= cycles * period) it.BounceT0 = -1;
                    else { double ph = (t % period) / period; bounce = slot * 0.55 * 4 * ph * (1 - ph); busy = true; }
                }
                it.Scale.ScaleX = it.Scale.ScaleY = it.S * it.P;
                it.Root.Opacity = it == drag ? 0 : 1;
                SetRect(it.Root, new Rect(it.CX - slot / 2, iconBase - slot - bounce, slot, slot));
                busy |= Approach(ref it.DotA, Settings.Indicators && it.Running ? 1 : 0, 6);
                it.Dot.Opacity = it.DotA * it.P * 0.85;
                SetRect(it.Dot, new Rect(it.CX - 2, bottom - 6.5, 4, 4));
            }

            DockItem hot = null;
            if (hovering && drag == null && !menuOpen && m.Y > bgTop - slot * A - 10)
                hot = items.FirstOrDefault(i => !i.IsSep && i.P > 0.5 && Math.Abs(i.CX - mx) <= slot * i.S / 2);
            if (dropActive) hot = dropHot;
            lastHot = hot;
            busy |= UpdatePreview(hot, now, iconBase);
            if (preview.IsVisible) hot = null;   // the previews replace the name bubble
            if (hot != null && hot != labelItem) { labelItem = hot; labelText.Text = hot.Name; }
            busy |= Approach(ref labelA, hot != null ? 1 : 0, hot != null ? 30 : 18);
            label.Opacity = labelA;
            if (labelItem != null && labelA > 0)
            {
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double lw = label.DesiredSize.Width, lh = label.DesiredSize.Height;
                var anchor = ToWindow(new Point(labelItem.CX, iconBase - slot * labelItem.S));
                var at = DisplayLayout.Beside(anchor, new Size(lw, lh), position, new Rect(0, 0, Width, Height));
                Canvas.SetLeft(label, at.X); Canvas.SetTop(label, at.Y);
            }

            if (!busy && !hovering && drag == null && !dropActive && !menuOpen)
            {
                CompositionTarget.Rendering -= Frame;
                looping = false;
            }
        }
    }

    // The icon that follows the cursor while dragging, plus the "poof" when it's dropped off the dock.
    class GhostWindow : Window
    {
        readonly Image img = new Image();
        readonly Border tag = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 3, 10, 4), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 24, 0, 0), Opacity = 0 };
        readonly Canvas puff = new Canvas();
        const double Size = 240;

        public GhostWindow()
        {
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
            ShowInTaskbar = false; Topmost = true; ShowActivated = false; Width = Height = Size; IsHitTestVisible = false;
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            tag.SetResourceReference(Border.BackgroundProperty, "Bubble");
            var t = new TextBlock { Text = "Remove", FontSize = 13, FontFamily = Theme.Font };
            t.SetResourceReference(TextBlock.ForegroundProperty, "BubbleFg");
            tag.Child = t;
            var g = new Grid();
            g.Children.Add(img); g.Children.Add(tag); g.Children.Add(puff);
            Content = g;
            SourceInitialized += delegate
            {
                var h = new WindowInteropHelper(this).Handle;
                Native.MakeToolWindow(h, true);
                Native.SetWindowLong(h, Native.GWL_EXSTYLE, Native.GetWindowLong(h, Native.GWL_EXSTYLE) | Native.WS_EX_TRANSPARENT);
            };
        }

        public void Begin(ImageSource src, double size)
        {
            img.Source = src; img.Width = img.Height = size; img.Opacity = 1; tag.Opacity = 0;
            puff.Children.Clear();
            Show();
        }

        public void MoveTo(Point screen) { Left = screen.X - Size / 2; Top = screen.Y - Size / 2; }

        public void ShowRemove(bool show) { tag.Opacity = show ? 1 : 0; }

        public void Poof()
        {
            tag.Opacity = 0;
            img.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)));
            var rnd = new Random();
            for (int i = 0; i < 9; i++)
            {
                double a = i * Math.PI * 2 / 9 + rnd.NextDouble() * 0.5, d = 26 + rnd.NextDouble() * 22, r = 20 + rnd.NextDouble() * 14;
                var e = new Ellipse
                {
                    Width = r * 2, Height = r * 2,
                    Fill = new RadialGradientBrush(new GradientStopCollection
                    {
                        new GradientStop(Color.FromArgb(240, 252, 252, 253), 0),
                        new GradientStop(Color.FromArgb(200, 214, 217, 224), 0.55),
                        new GradientStop(Color.FromArgb(0, 190, 194, 204), 1)
                    }),
                    RenderTransform = new TransformGroup { Children = { new ScaleTransform(0.3, 0.3, r, r), new TranslateTransform() } }
                };
                Canvas.SetLeft(e, Size / 2 - r); Canvas.SetTop(e, Size / 2 - r);
                puff.Children.Add(e);
                var tg = (TransformGroup)e.RenderTransform;
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                var dur = TimeSpan.FromMilliseconds(420 + rnd.Next(120));
                ((ScaleTransform)tg.Children[0]).BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.5, dur) { EasingFunction = ease });
                ((ScaleTransform)tg.Children[0]).BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.5, dur) { EasingFunction = ease });
                ((TranslateTransform)tg.Children[1]).BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(Math.Cos(a) * d, dur) { EasingFunction = ease });
                ((TranslateTransform)tg.Children[1]).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(Math.Sin(a) * d - 10, dur) { EasingFunction = ease });
                e.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, dur) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });
            }
            var done = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            done.Tick += delegate { done.Stop(); Hide(); img.BeginAnimation(OpacityProperty, null); };
            done.Start();
        }
    }

    // A folder fanned out as a grid above its dock icon (macOS "Stacks").
    class StackWindow : Window
    {
        readonly Border panel;
        readonly ScaleTransform zoom = new ScaleTransform(0.86, 0.86);
        bool closing;

        public StackWindow(string folder, string title)
        {
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
            ShowInTaskbar = false; Topmost = true; SizeToContent = SizeToContent.WidthAndHeight;
            FontFamily = Theme.Font;
            var grid = new WrapPanel { Width = 4 * 104 };
            IEnumerable<FileSystemInfo> entries = new FileSystemInfo[0];
            try { entries = new DirectoryInfo(folder).EnumerateFileSystemInfos().Where(f => (f.Attributes & FileAttributes.Hidden) == 0).OrderByDescending(f => f.LastWriteTime).Take(16).ToList(); } catch { }
            foreach (var f in entries)
            {
                var path = f.FullName;
                var icon = new Image { Width = 60, Height = 60 };
                RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
                Icons.Get(path, false, s => icon.Source = s, 128, false);
                var name = new TextBlock { Text = f.Name, FontSize = 11.5, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 30, Margin = new Thickness(0, 4, 0, 0) };
                name.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
                var cell = new StackPanel { Width = 96, Margin = new Thickness(4, 6, 4, 6), Background = Brushes.Transparent, Cursor = Cursors.Hand };
                cell.Children.Add(icon); cell.Children.Add(name);
                cell.MouseLeftButtonUp += delegate { Launcher.Open(path); Dismiss(); };
                grid.Children.Add(cell);
            }
            var header = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 0, 0, 8) };
            header.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            var open = new Button { Content = "Open in Finder  ›", Style = (Style)Application.Current.Resources["Flat"], Padding = new Thickness(10, 5, 10, 5), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
            open.Click += delegate { App.NewFinder(folder); Dismiss(); };
            var body = new StackPanel();
            body.Children.Add(header);
            if (grid.Children.Count == 0) body.Children.Add(new TextBlock { Text = "No items", Margin = new Thickness(6, 20, 6, 20), Foreground = Theme.Get("FgDim") });
            body.Children.Add(grid);
            body.Children.Add(open);
            var tint = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(12), BorderThickness = new Thickness(1), Child = body };
            tint.SetResourceReference(Border.BackgroundProperty, "Glass");
            tint.SetResourceReference(Border.BorderBrushProperty, "MenuBorder");
            panel = new Border
            {
                CornerRadius = new CornerRadius(14), Margin = new Thickness(24, 16, 24, 30), Child = tint,
                Effect = (Effect)Application.Current.Resources["MenuShadow"], RenderTransform = zoom, RenderTransformOrigin = new Point(0.5, 1), Opacity = 0
            };
            Content = panel;
            Deactivated += delegate { Dismiss(); };
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Dismiss(); };
            SourceInitialized += delegate { Native.MakeToolWindow(new WindowInteropHelper(this).Handle, false); };
        }

        public void ShowAbove(Point anchor, int position, Rect area)
        {
            Show();
            UpdateLayout();
            var at = DisplayLayout.Beside(anchor, new Size(ActualWidth, ActualHeight), position, area);
            Left = at.X; Top = at.Y;
            // frosted glass: blur what's behind the panel (the window is still fully transparent here)
            double w = panel.ActualWidth, h = panel.ActualHeight, dpi = App.Dpi;
            var shot = Icons.CaptureScreen((int)((Left + panel.Margin.Left) * dpi), (int)((Top + panel.Margin.Top) * dpi), (int)(w * dpi), (int)(h * dpi));
            panel.Background = new ImageBrush(Icons.Blur(shot, (int)(w / 3), (int)(h / 3), 8));
            Activate();
            var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 };
            var d = TimeSpan.FromMilliseconds(260);
            zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, d) { EasingFunction = ease });
            zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, d) { EasingFunction = ease });
            panel.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
        }

        public void Dismiss()
        {
            if (closing) return;
            closing = true;
            var d = TimeSpan.FromMilliseconds(150);
            zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.9, d));
            zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.9, d));
            var fade = new DoubleAnimation(0, d);
            fade.Completed += delegate { Close(); };
            panel.BeginAnimation(OpacityProperty, fade);
        }
    }
}
