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
        public const double H = 30;
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
        Border batteryButton, bgApps;
        Brush fg;
        string current = "Finder";

        public MenuBarWindow()
        {
            Title = "OpenDock Menu Bar";
            WindowStyle = WindowStyle.None; AllowsTransparency = true; ShowInTaskbar = false; Topmost = true;
            ResizeMode = ResizeMode.NoResize; ShowActivated = false; Height = H;
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // invisible but clickable
            FontFamily = Theme.Font; FontSize = 13.5;

            var logo = new Ellipse { Width = 15, Height = 15, StrokeThickness = 2.2, Margin = new Thickness(4, 0, 4, 0) };
            logo.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground") { Source = this });
            var logoMenu = Bar0(logo);
            logoMenu.Items.Add(Cmd("About This PC", null, AboutWindow.ShowIt));
            logoMenu.Items.Add(new Separator());
            logoMenu.Items.Add(Cmd("System Settings…", null, () => App.OpenSettings("General")));
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
            window.Items.Add(new Separator());
            window.Items.Add(Keys("Move to Left Half", "Win+←", 0x5B, 0x25));
            window.Items.Add(Keys("Move to Right Half", "Win+→", 0x5B, 0x27));
            window.Items.Add(new Separator());
            window.Items.Add(Keys("Mission Control", "Win+Tab", 0x5B, 0x09));
            window.Items.Add(Cmd("Close Window", "Alt+F4", () => Target(h => Native.PostMessage(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero))));

            var help = Bar0("Help");
            help.Items.Add(Cmd("Search", "Alt+Space", App.ToggleSpotlight));
            help.Items.Add(Keys("App Help", "F1", 0x70));

            // status area
            bgApps = Status(Glyph(""), ShowBackgroundApps, "Background Apps");
            right.Children.Add(bgApps);
            right.Children.Add(Status(Glyph(""), () => Win('A'), "Sound"));
            batteryButton = Status(Battery(), () => Win('A'), "Battery");
            right.Children.Add(batteryButton);
            right.Children.Add(Status(net, () => Launcher.Shell("ms-availablenetworks:"), "Network"));
            right.Children.Add(Status(Glyph(""), App.ToggleSpotlight, "Spotlight"));
            right.Children.Add(Status(ControlCenter(), () => Win('A'), "Control Center"));
            right.Children.Add(Status(clock, () => Win('N'), null));

            var root = new Grid { Effect = glow };
            root.Children.Add(menu);
            root.Children.Add(right);
            Content = root;

            SourceInitialized += delegate
            {
                Native.MakeToolWindow(new WindowInteropHelper(this).Handle, true);
                Bar = new AppBar(this, Native.ABE_TOP);
                Bar.FullScreen = fs => { if (App.Dock != null) App.Dock.OnFullScreen(fs); };
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
        }

        public void Place()
        {
            if (Bar == null) return;
            Left = 0; Top = 0; Width = SystemParameters.PrimaryScreenWidth;
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
                if (w != null && (w.RootVisual is FinderWindow || w.RootVisual is SettingsWindow)) SetName("Finder");
                return;
            }
            if (cls == "Progman" || cls == "WorkerW") { SetName("Finder"); return; }
            if (cls == "Shell_TrayWnd" || cls.StartsWith("Windows.UI.Core") || cls == "XamlExplorerHostIslandWindow") return;
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
            Func<WinInfo, bool> mine = w => exe != null && string.Equals(w.Exe, exe, StringComparison.OrdinalIgnoreCase);
            appMenu.Items.Add(Cmd("About " + current, null, () => { if (exe != null) App.ShowInFinder(exe); }, exe != null));
            appMenu.Items.Add(new Separator());
            appMenu.Items.Add(Cmd("Settings…", null, App.OpenSettings));
            appMenu.Items.Add(new Separator());
            appMenu.Items.Add(Cmd("Hide " + current, null, () => { foreach (var w in App.Wins.Where(mine)) Native.ShowWindow(w.Hwnd, Native.SW_MINIMIZE); }, exe != null));
            appMenu.Items.Add(Cmd("Hide Others", null, () => { foreach (var w in App.Wins.Where(w => !mine(w))) Native.ShowWindow(w.Hwnd, Native.SW_MINIMIZE); }));
            appMenu.Items.Add(Cmd("Show All", null, () => { foreach (var w in App.Wins.Where(w => Native.IsIconic(w.Hwnd))) Native.ShowWindow(w.Hwnd, Native.SW_SHOWNOACTIVATE); }));
            appMenu.Items.Add(new Separator());
            appMenu.Items.Add(Cmd("Quit " + current, "Alt+F4", () => { foreach (var w in App.Wins.Where(mine)) Native.PostMessage(w.Hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }, exe != null));
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
                // Opens the app's own tray menu, the same one the Windows taskbar shows on right-click.
                var mi = Cmd(a.Name, null, app.ShowTrayMenu);
                mi.Icon = new Image { Source = a.Icon, Width = 16, Height = 16 };
                cm.Items.Add(mi);
            }
            cm.Items.Add(new Separator());
            cm.Items.Add(Cmd("Windows Tray Icons…", null, Taskbar.OpenTrayFlyout));
            cm.IsOpen = true;
        }

        static string TargetExe()
        {
            var h = App.LastForeground;
            return h != IntPtr.Zero && Native.IsWindow(h) ? Windows.ExeOf(h) : null;
        }

        static void Target(Action<IntPtr> a)
        {
            var h = App.LastForeground;
            if (h != IntPtr.Zero && Native.IsWindow(h)) a(h);
        }

        // Menu commands are delivered as the matching keyboard shortcut to the app you were using.
        static void SendTo(byte[] vks)
        {
            var h = App.LastForeground;
            if (h == IntPtr.Zero || !Native.IsWindow(h)) return;
            Native.Activate(h);
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            t.Tick += delegate { t.Stop(); Native.SendKeys(vks); };
            t.Start();
        }

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
