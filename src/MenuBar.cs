using System;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace OpenDock
{
    class MenuBarWindow : Window
    {
        public AppBar Bar;
        public static double H { get { return Math.Max(24,Math.Min(48,Settings.MenuBarHeight)); } }
        static IntPtr commandTarget;
        readonly Menu menu = new Menu { Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Stretch };
        readonly MenuItem appMenu = new MenuItem();
        readonly TextBlock appName = new TextBlock { FontWeight = FontWeights.Bold };
        readonly TextBlock clock = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        readonly StackPanel right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 10, 0) };
        readonly DropShadowEffect glow = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = 0.35 };
        readonly Grid battery = new Grid { Width = 27, Height = 13, VerticalAlignment = VerticalAlignment.Center };
        readonly Rectangle batteryFill = new Rectangle { RadiusX = 1.5, RadiusY = 1.5, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2.2, 2.2, 0, 2.2) };
        readonly Path bolt = new Path { Data = Geometry.Parse("M7,0 L2,7 L5.5,7 L4,13 L10,5 L6.5,5 Z"), Width = 8, Height = 10, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 3, 0) };
        readonly TextBlock net = new TextBlock { FontFamily = Theme.Glyphs, FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
        Border batteryButton, bgApps, spotlightItem, focusItem;
        readonly TextBlock focusGlyph = new TextBlock { FontFamily = Theme.Glyphs, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };

        void ShowFocus()
        {
            focusItem.Visibility = FocusMode.Active ? Visibility.Visible : Visibility.Collapsed;
            focusGlyph.Text = FocusMode.Glyph;
            focusItem.ToolTip = FocusMode.Name;
        }
        MenuItem searchItem;
        Brush fg;
        string current = "Finder", displayDevice;

        public MenuBarWindow()
        {
            Title = "OpenDock Menu Bar";
            WindowStyle = WindowStyle.None; AllowsTransparency = true; ShowInTaskbar = false; Topmost = true;
            ResizeMode = ResizeMode.NoResize; ShowActivated = false; Height = H;
            Settings.Changed += OnSettings;
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // invisible but clickable
            FontFamily = Theme.Font; FontSize = 13.5;

            // Windows 11's flat four-square mark, kept vector-sharp at any display scale.
            var logo = new Path
            {
                Width = 15, Height = 15, Margin = new Thickness(4, 0, 4, 0), Stretch = Stretch.Uniform,
                Fill = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)), SnapsToDevicePixels = true,
                Data = Geometry.Parse("M0,0H7V7H0Z M8,0H15V7H8Z M0,8H7V15H0Z M8,8H15V15H8Z")
            };
            var logoMenu = Bar0(logo);
            logoMenu.Items.Add(Cmd("About This PC", null, AboutWindow.ShowIt));
            logoMenu.Items.Add(new Separator());
            logoMenu.Items.Add(Cmd("System Settings…", null, () => App.OpenSettings("General")));
            logoMenu.Items.Add(Cmd("Shelf…", null, ShelfWindow.Open));
            logoMenu.Items.Add(Cmd("Desktop Scenes & Dock Studio…", null, ()=>App.OpenSettings("Workspace")));
            logoMenu.Items.Add(Cmd("App Store…", null, () => Launcher.Shell("ms-windows-store:")));
            logoMenu.Items.Add(new Separator());
            logoMenu.Items.Add(Cmd("Force Quit…", "Ctrl+Shift+Esc", () => Launcher.Shell("taskmgr.exe")));
            logoMenu.Items.Add(new Separator());
            logoMenu.Items.Add(Cmd("Sleep", null, () => Native.SetSuspendState(false, false, false)));
            logoMenu.Items.Add(Cmd("Restart…", null, () => { if (Confirm("restart")) Process.Start("shutdown", "/r /t 0"); }));
            logoMenu.Items.Add(Cmd("Shut Down…", null, () => { if (Confirm("shut down")) Process.Start("shutdown", "/s /t 0"); }));
            logoMenu.Items.Add(new Separator());
            logoMenu.Items.Add(Cmd("Lock Screen", "Win+L", () => Native.LockWorkStation()));
            logoMenu.Items.Add(Cmd("Log Out " + Environment.UserName + "…", null, () => { if (Confirm("log out")) Native.ExitWindowsEx(0, 0); }));
            logoMenu.Items.Add(new Separator());
            logoMenu.Items.Add(Cmd("Quit OpenDock", null, App.Quit));

            appMenu.Header = appName;
            appMenu.Style = (Style)Application.Current.Resources["BarItem"];
            appMenu.SubmenuOpened += delegate { BuildAppMenu(); };
            appMenu.Items.Add(new MenuItem()); // placeholder so the submenu can open
            menu.Items.Add(appMenu);

            var file = Bar0("File");
            file.Items.Add(Keys("New Window", "Ctrl+N", 0x11, 0x4E));
            file.Items.Add(Keys("New Tab", "Ctrl+T", 0x11, 0x54));
            file.Items.Add(Keys("Open…", "Ctrl+O", 0x11, 0x4F));
            file.Items.Add(new Separator());
            file.Items.Add(Keys("Save", "Ctrl+S", 0x11, 0x53));
            file.Items.Add(Keys("Save As…", "Ctrl+Shift+S", 0x11, 0x10, 0x53));
            file.Items.Add(new Separator());
            file.Items.Add(Keys("Print…", "Ctrl+P", 0x11, 0x50));
            file.Items.Add(Keys("Close", "Ctrl+W", 0x11, 0x57));

            var edit = Bar0("Edit");
            edit.Items.Add(Keys("Undo", "Ctrl+Z", 0x11, 0x5A));
            edit.Items.Add(Keys("Redo", "Ctrl+Y", 0x11, 0x59));
            edit.Items.Add(new Separator());
            edit.Items.Add(Keys("Cut", "Ctrl+X", 0x11, 0x58));
            edit.Items.Add(Keys("Copy", "Ctrl+C", 0x11, 0x43));
            edit.Items.Add(Keys("Paste", "Ctrl+V", 0x11, 0x56));
            edit.Items.Add(Keys("Select All", "Ctrl+A", 0x11, 0x41));
            edit.Items.Add(new Separator());
            edit.Items.Add(Keys("Find…", "Ctrl+F", 0x11, 0x46));
            edit.Items.Add(new Separator());
            edit.Items.Add(Keys("Emoji & Symbols", "Win+.", 0x5B, 0xBE));

            var view = Bar0("View");
            view.Items.Add(Keys("Zoom In", "Ctrl++", 0x11, 0xBB));
            view.Items.Add(Keys("Zoom Out", "Ctrl+−", 0x11, 0xBD));
            view.Items.Add(Keys("Actual Size", "Ctrl+0", 0x11, 0x30));
            view.Items.Add(new Separator());
            view.Items.Add(Keys("Enter Full Screen", "F11", 0x7A));

            var window = Bar0("Window");
            window.Items.Add(Cmd("Minimize", null, () => Target(h => Native.ShowWindow(h, Native.SW_MINIMIZE))));
            window.Items.Add(Cmd("Zoom", null, () => Target(h => Native.ShowWindow(h, Native.IsZoomed(h) ? Native.SW_RESTORE : Native.SW_MAXIMIZE))));
            window.Items.Add(Cmd("Fill", "Ctrl+Alt+Enter", () => Tiling.Move(TargetHandle(), Tiling.Spot.Fill)));
            window.Items.Add(Cmd("Center", null, () => Tiling.Move(TargetHandle(), Tiling.Spot.Center)));
            // macOS 15's Move & Resize: halves, quarters, and arranging the two front windows together.
            var tile = new MenuItem { Header = "Move & Resize" };
            foreach (var spot in Tiling.MoveSpots)
            {
                var s = spot;
                if (s == Tiling.Spot.TopLeft) tile.Items.Add(new Separator());
                tile.Items.Add(Cmd(Tiling.Name(s), s == Tiling.Spot.Left ? "Ctrl+Alt+←" : s == Tiling.Spot.Right ? "Ctrl+Alt+→" : null, () => Tiling.Move(TargetHandle(), s)));
            }
            tile.Items.Add(new Separator());
            tile.Items.Add(Cmd("Left & Right", null, () => Tiling.Arrange(Tiling.Spot.Left, Tiling.Spot.Right)));
            tile.Items.Add(Cmd("Right & Left", null, () => Tiling.Arrange(Tiling.Spot.Right, Tiling.Spot.Left)));
            tile.Items.Add(Cmd("Top & Bottom", null, () => Tiling.Arrange(Tiling.Spot.Top, Tiling.Spot.Bottom)));
            tile.Items.Add(Cmd("Quarters", null, Tiling.Quarters));
            tile.Items.Add(new Separator());
            tile.Items.Add(Cmd("Return to Previous Size", "Ctrl+Alt+Backspace", () => Tiling.Move(TargetHandle(), Tiling.Spot.Restore)));
            window.Items.Add(tile);
            window.Items.Add(new Separator());
            window.Items.Add(Cmd("Mission Control", "Ctrl+Alt+↑", () => MissionControl.Toggle(IntPtr.Zero)));
            window.Items.Add(Cmd("Show All Windows", "Ctrl+Alt+↓", () => MissionControl.Toggle(TargetHandle())));
            window.Items.Add(Cmd("Close Window", "Alt+F4", () => Target(h => Native.PostMessage(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero))));

            var help = Bar0("Help");
            searchItem = Cmd("Search", "Alt+Space", App.ToggleSpotlight);
            help.Items.Add(searchItem);
            help.Items.Add(Keys("App Help", "F1", 0x70));

            // status area
            bgApps = Status(Glyph(""), ShowBackgroundApps, "Background Apps");
            right.Children.Add(bgApps);
            Border sound = null;
            sound = Status(Glyph(""), () => SoundPanel.Toggle(sound), "Sound");
            right.Children.Add(sound);
            batteryButton = Status(Battery(), () => App.OpenSettings("Battery"), "Battery");
            right.Children.Add(batteryButton);
            Border network = null;
            network = Status(net, () => WifiPanel.Toggle(network), "Wi-Fi");
            right.Children.Add(network);
            spotlightItem = Status(Glyph(""), App.ToggleSpotlight, "Spotlight");
            right.Children.Add(spotlightItem);
            // While a Focus is on, its symbol sits next to Control Center, as on macOS.
            focusItem = Status(focusGlyph, () => { var cm = FocusMode.Menu(); cm.PlacementTarget = focusItem; cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; cm.HorizontalOffset = -12; cm.IsOpen = true; }, "Focus");
            right.Children.Add(focusItem);
            ShowFocus();
            FocusMode.Changed += ShowFocus;
            right.Children.Add(Status(ControlCenter(), ControlCenterWindow.Toggle, "Control Center"));
            Border date = null;
            date = Status(clock, () => ClockPanel.Toggle(date), null);
            right.Children.Add(date);

            var root = new Grid { Effect = glow };
            root.Children.Add(menu);
            root.Children.Add(right);
            Content = root;

            SourceInitialized += delegate
            {
                Native.MakeToolWindow(new WindowInteropHelper(this).Handle, true);
                Bar = new AppBar(this, Native.ABE_TOP);
                Bar.Placed = rc => { var r = DisplayLayout.Dip(rc); Left = r.Left; Top = r.Top; Width = r.Width; };
                Bar.FullScreen = fs => { foreach (var dock in App.Docks) dock.OnFullScreen(fs); };
                Place();
            };
            App.ForegroundChanged += OnForeground;
            Wallpaper.Changed += Recolor;
            Recolor();
            SetName("Finder");

            var minute = new DispatcherTimer();
            minute.Tick += delegate { Tick(); minute.Interval = TimeSpan.FromSeconds(60 - DateTime.Now.Second + 0.05); };
            minute.Interval = TimeSpan.FromMilliseconds(10);
            minute.Start();
            var slow = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            slow.Tick += delegate { UpdateStatus(); };
            slow.Start();
            UpdateStatus();
            ShowSpotlight();
            Closed+=delegate { minute.Stop(); slow.Stop(); Settings.Changed-=OnSettings; App.ForegroundChanged-=OnForeground; Wallpaper.Changed-=Recolor; if(Bar!=null) Bar.Remove(); };
        }
        void OnSettings() { Height=H; Place(); Island.Place(); ShowSpotlight(); }

        void ShowSpotlight()
        {
            var v = Settings.Spotlight ? Visibility.Visible : Visibility.Collapsed;
            spotlightItem.Visibility = v; searchItem.Visibility = v;
        }

        public void Place()
        {
            if (Bar == null) return;
            var selected = DisplayLayout.Selected;
            if (displayDevice != selected.Device) { Bar.Remove(); displayDevice = selected.Device; }
            var bounds = DisplayLayout.Dip(selected.Bounds);
            Left = bounds.Left; Top = bounds.Top; Width = bounds.Width;
            if (Settings.ShowMenuBar) Bar.Reserve((int)Math.Round(H * App.Dpi)); else Bar.Remove();
        }

        public void SetShown(bool shown)
        {
            if (shown) Show(); else Hide();
            Place();
        }

        // White text over dark wallpapers, black over light ones.
        void Recolor()
        {
            bool light = Wallpaper.TopIsLight;
            fg = light ? Theme.B("#E6000000") : Brushes.White;
            glow.Color = light ? Colors.White : Colors.Black;
            Foreground = fg;
            menu.Foreground = fg;
            bolt.Fill = fg;
        }

        void Tick()
        {
            var now = DateTime.Now;
            clock.Text = now.ToString("ddd MMM d") + "  " + now.ToShortTimeString();
        }

        void UpdateStatus()
        {
            Native.SYSTEM_POWER_STATUS ps;
            bool hasBattery = Native.GetSystemPowerStatus(out ps) && ps.BatteryFlag != 128 && ps.BatteryFlag != 255 && ps.BatteryLifePercent <= 100;
            batteryButton.Visibility = hasBattery ? Visibility.Visible : Visibility.Collapsed;
            if (hasBattery)
            {
                batteryFill.Width = Math.Max(1.5, 19.6 * ps.BatteryLifePercent / 100.0);
                batteryFill.Fill = ps.BatteryLifePercent <= 15 && ps.ACLineStatus != 1 ? Theme.B("#FF453A") : fg;
                bolt.Visibility = ps.ACLineStatus == 1 ? Visibility.Visible : Visibility.Collapsed;
                batteryFill.Opacity = ps.ACLineStatus == 1 ? 0.45 : 1;
                batteryButton.ToolTip = ps.BatteryLifePercent + "%" + (ps.ACLineStatus == 1 ? " — charging" : "");
            }
            bool wifi = false, any = false;
            try
            {
                foreach (var n in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel))
                {
                    any = true;
                    if (n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) wifi = true;
                }
            }
            catch { }
            net.Text = wifi ? "" : any ? "" : "";
        }

        // ---------- active app ----------

        void OnForeground(IntPtr h)
        {
            h = Native.GetAncestor(h, 2);
            if (h == IntPtr.Zero) return;
            uint pid; Native.GetWindowThreadProcessId(h, out pid);
            string cls = Native.ClassName(h);
            if (pid == (uint)Process.GetCurrentProcess().Id)
            {
                var w = HwndSource.FromHwnd(h);
                if (w != null && w.RootVisual is MacWindow) { commandTarget=h; SetName(w.RootVisual is FinderWindow?"Finder":((Window)w.RootVisual).Title); }
                return;
            }
            if (cls == "Progman" || cls == "WorkerW") { commandTarget=h; SetName("Finder"); return; }
            if (cls == "Shell_TrayWnd" || cls.StartsWith("Windows.UI.Core") || cls == "XamlExplorerHostIslandWindow") return;
            commandTarget=h;
            string exe = Windows.ExeOf(h);
            if (exe == null) return;
            if (exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
            {
                var a = Apps.ById(Native.WindowAumid(h) ?? "");
                SetName(a != null ? a.Name : Native.WindowText(h));
            }
            else SetName(Apps.FriendlyName(exe));
        }

        void SetName(string name)
        {
            current = string.IsNullOrWhiteSpace(name) ? "Finder" : name;
            appName.Text = current.Length > 30 ? current.Substring(0, 29) + "…" : current;
        }

        void BuildAppMenu()
        {
            appMenu.Items.Clear();
            string exe = TargetExe();
            IntPtr target=TargetHandle(); uint pid; Native.GetWindowThreadProcessId(target,out pid); bool own=pid==(uint)Process.GetCurrentProcess().Id;
            Func<WinInfo, bool> mine = w => own?w.Hwnd==target:exe != null && string.Equals(w.Exe, exe, StringComparison.OrdinalIgnoreCase);
            var related=own?new[]{target}:App.Wins.Where(mine).Select(w=>w.Hwnd).ToArray();
            appMenu.Items.Add(Cmd("About " + current, null, () => { if (exe != null) App.ShowInFinder(exe); }, exe != null));
            appMenu.Items.Add(new Separator());
            appMenu.Items.Add(Cmd("Settings…", null, App.OpenSettings));
            appMenu.Items.Add(new Separator());
            appMenu.Items.Add(Cmd("Hide " + current, null, () => { foreach (var h in related) Native.ShowWindow(h, Native.SW_MINIMIZE); }, exe != null));
            appMenu.Items.Add(Cmd("Hide Others", null, () => { foreach (var w in App.Wins.Where(w => !mine(w))) Native.ShowWindow(w.Hwnd, Native.SW_MINIMIZE); }));
            appMenu.Items.Add(Cmd("Show All", null, () => { foreach (var w in App.Wins.Where(w => Native.IsIconic(w.Hwnd))) Native.ShowWindow(w.Hwnd, Native.SW_SHOWNOACTIVATE); }));
            appMenu.Items.Add(new Separator());
            appMenu.Items.Add(Cmd("Quit " + current, "Alt+F4", () => { foreach (var h in related) Native.PostMessage(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }, exe != null));
        }
        void ShowBackgroundApps()
        {
            var cm = new ContextMenu { PlacementTarget = bgApps, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, HorizontalOffset = -12 };
            cm.Items.Add(new MenuItem { Header = "Background Apps", IsEnabled = false });
            cm.Items.Add(new Separator());
            var apps = Tray.Running();
            if (apps.Count == 0) cm.Items.Add(new MenuItem { Header = "No background apps running", IsEnabled = false });
            foreach (var a in apps)
            {
                var app = a;
                // Each app opens a submenu (hover or click) with OpenDock's own options for it.
                var mi = new MenuItem { Header = a.Name, Icon = new Image { Source = a.Icon, Width = 16, Height = 16 } };
                mi.Items.Add(Cmd("Open", null, app.Open));
                if (app.Exe != null && app.Exe.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) < 0) mi.Items.Add(Cmd("Show in Finder", null, () => App.ShowInFinder(app.Exe)));
                mi.Items.Add(new Separator());
                mi.Items.Add(Cmd("Close", null, app.Quit));
                mi.Items.Add(Cmd("Force Close…", null, () =>
                {
                    if (MessageBox.Show("Force close " + app.Name + "? Unsaved work in it will be lost.", "OpenDock", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) app.Kill();
                }));
                cm.Items.Add(mi);
            }
            cm.Items.Add(new Separator());
            cm.Items.Add(Cmd("Windows Tray Icons…", null, Taskbar.OpenTrayFlyout));
            cm.IsOpen = true;
        }

        static string TargetExe()
        {
            var h = TargetHandle();
            return h != IntPtr.Zero && Native.IsWindow(h) ? Windows.ExeOf(h) : null;
        }

        static void Target(Action<IntPtr> a)
        {
            var h = TargetHandle();
            if (h != IntPtr.Zero && Native.IsWindow(h)) a(h);
        }

        // Menu commands are delivered as the matching keyboard shortcut to the app you were using.
        static void SendTo(byte[] vks)
        {
            var h = TargetHandle();
            if (h == IntPtr.Zero || !Native.IsWindow(h)) return;
            Native.Activate(h);
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            t.Tick += delegate { t.Stop(); if(Native.GetAncestor(Native.GetForegroundWindow(),2)==h) Native.SendKeys(vks); };
            t.Start();
        }
        internal static IntPtr TargetHandle() { return Native.IsWindow(commandTarget)?commandTarget:App.LastForeground; }

        static void Win(char key) { Native.SendKeys(0x5B, (byte)key); }

        static bool Confirm(string what)
        {
            return MessageBox.Show("Are you sure you want to " + what + " now?", "OpenDock", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        }

        // ---------- builders ----------

        MenuItem Bar0(object header)
        {
            var mi = new MenuItem { Header = header, Style = (Style)Application.Current.Resources["BarItem"] };
            menu.Items.Add(mi);
            return mi;
        }

        static MenuItem Cmd(string text, string gesture, Action a, bool enabled = true)
        {
            var mi = new MenuItem { Header = text, InputGestureText = gesture ?? "", IsEnabled = enabled };
            mi.Click += delegate { a(); };
            return mi;
        }

        static MenuItem Keys(string text, string gesture, params byte[] vks) { return Cmd(text, gesture, () => SendTo(vks)); }

        static TextBlock Glyph(string g) { return new TextBlock { Text = g, FontFamily = Theme.Glyphs, FontSize = 15, VerticalAlignment = VerticalAlignment.Center }; }

        Border Status(UIElement content, Action click, string tip)
        {
            var b = new Border { Child = content, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(1, 3, 1, 3), CornerRadius = new CornerRadius(5), Background = Brushes.Transparent, ToolTip = tip };
            b.MouseEnter += delegate { b.Background = Theme.B("#26FFFFFF"); };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; };
            b.MouseLeftButtonUp += delegate { click(); };
            return b;
        }

        FrameworkElement Battery()
        {
            var shell = new Border { CornerRadius = new CornerRadius(3.5), BorderThickness = new Thickness(1.1), Width = 24, HorizontalAlignment = HorizontalAlignment.Left, Opacity = 0.55 };
            shell.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("Foreground") { Source = this });
            var nub = new Rectangle { Width = 1.8, Height = 4.5, RadiusX = 1, RadiusY = 1, HorizontalAlignment = HorizontalAlignment.Right, Opacity = 0.55 };
            nub.SetBinding(Shape.FillProperty, new System.Windows.Data.Binding("Foreground") { Source = this });
            battery.Children.Add(shell); battery.Children.Add(nub); battery.Children.Add(batteryFill); battery.Children.Add(bolt);
            return battery;
        }

        FrameworkElement ControlCenter()
        {
            var c = new Canvas { Width = 17, Height = 13, VerticalAlignment = VerticalAlignment.Center };
            var b = new System.Windows.Data.Binding("Foreground") { Source = this };
            Action<double, bool> pill = (y, knobLeft) =>
            {
                var p = new Border { Width = 17, Height = 6, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1.2) };
                p.SetBinding(Border.BorderBrushProperty, b);
                Canvas.SetTop(p, y); c.Children.Add(p);
                var k = new Ellipse { Width = 6, Height = 6 };
                k.SetBinding(Shape.FillProperty, b);
                Canvas.SetTop(k, y); Canvas.SetLeft(k, knobLeft ? 0 : 11); c.Children.Add(k);
            };
            pill(0, false); pill(7, true);
            return c;
        }
    }
}
