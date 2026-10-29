using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace OpenDock
{
    // Opt-in integration checks on a real desktop. No taskbar hiding, saved preferences,
    // startup entries, global hotkeys, colour profiles or window-event hooks are installed.
    static class DesktopChecks
    {
        [DllImport("user32.dll")] static extern bool GetWindowDisplayAffinity(IntPtr window, out uint affinity);
        static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

        static void View(FinderWindow finder, bool list)
        {
            typeof(FinderWindow).GetMethod("SetView", Private).Invoke(finder, new object[] { list, false });
        }

        static void Capture(Window window, string name)
        {
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "qa"); Directory.CreateDirectory(dir);
            using (var file = File.Create(Path.Combine(dir, name + ".png"))) png.Save(file);
        }

        static void Capturable(Window window)
        {
            uint affinity;
            if (!GetWindowDisplayAffinity(new WindowInteropHelper(window).Handle, out affinity) || affinity != 0)
                throw new Exception(window.Title + " is excluded from screenshots");
        }

        public static void Run(bool preview)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            using (var graphics = System.Drawing.Graphics.FromHwnd(IntPtr.Zero)) App.Dpi = graphics.DpiX / 96;
            Settings.HideTaskbar = false; Settings.FirstRun = false; Settings.AutoHide = true;
            Settings.LiveGlass = false; Settings.DisplayDevice = "";
            // Force a crowded dock, including the short vertical dimension of a laptop screen.
            for (int i = 0; i < 18; i++) Settings.Pins.Add("App " + i + "|" + Assembly.GetExecutingAssembly().Location);
            Theme.Init(app); Glass.Init(); Icons.Start(); Wallpaper.Load();
            string fixture = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "qa", "fixture");
            Directory.CreateDirectory(Path.Combine(fixture, "Documents"));
            File.WriteAllText(Path.Combine(fixture, "Read me.txt"), "Finder list and icon view sample.");
            File.WriteAllText(Path.Combine(fixture, "Report.csv"), "Name,Value\r\nSample,42\r\n");
            var dock = App.Dock = new DockWindow();
            var menu = App.MenuBar = new MenuBarWindow();
            FinderWindow finder = null;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            Exception failure = null;
            int step = 0;
            Action cleanup = delegate
            {
                timer.Stop();
                foreach (var monitorDock in App.Docks.ToList()) monitorDock.Close();
                App.Docks.Clear();
                if (dock.Bar != null) dock.Bar.Remove();
                if (menu.Bar != null) menu.Bar.Remove();
                app.Shutdown();
            };
            app.DispatcherUnhandledException += (s, e) => { failure = e.Exception; e.Handled = true; cleanup(); };
            app.Startup += delegate { menu.Show(); dock.Show(); finder = new FinderWindow(fixture); finder.Show(); timer.Start(); };
            timer.Tick += delegate
            {
                try
                {
                    if (step < 6)
                    {
                        Settings.DockPosition = step % 3; Settings.AutoHide = step < 3;
                        dock.Place();
                        dock.UpdateLayout();
                        Capturable(dock); Capturable(finder);
                        if (!DisplayLayout.Bounds.Contains(new Rect(dock.Left, dock.Top, dock.Width, dock.Height))) throw new Exception("dock escaped selected display");
                        Console.WriteLine("desktop placement: " + Settings.DockPosition + ", auto-hide=" + Settings.AutoHide);
                    }
                    else if (step == 6)
                    {
                        Settings.AutoHide = true; dock.Place();
                        Settings.DisplayDevice = "disconnected-display"; dock.Place();
                        if (!DisplayLayout.Selected.Primary) throw new Exception("missing display did not fall back to primary");
                        Settings.DisplayDevice = "";
                        View(finder, true);
                        var list = (ListView)typeof(FinderWindow).GetField("files", Private).GetValue(finder);
                        Capture(finder, "finder-before-assert");
                        if (!(list.View is GridView) || list.Items.Count != 3) throw new Exception("Finder fixture: view=" + list.View + ", entries=" + list.Items.Count + ", path=" + typeof(FinderWindow).GetField("path", Private).GetValue(finder));
                        finder.UpdateLayout();
                        var container = (ListViewItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                        if (container == null || VisualTreeHelper.GetChildrenCount(container) == 0 || !(VisualTreeHelper.GetChild(container, 0) is Border)) throw new Exception("Finder list row template wasn't applied");
                        list.SelectedIndex = 1;
                        var selected = list.SelectedItem;
                        View(finder, false); View(finder, true);
                        if (list.SelectedItem != selected) throw new Exception("view switch lost selection");
                        Capture(finder, "finder-list-dark");
                    }
                    else if (step == 7)
                    {
                        Settings.FinderListView = true;
                        var savedList = new FinderWindow(fixture);
                        var savedFiles = (ListView)typeof(FinderWindow).GetField("files", Private).GetValue(savedList);
                        if (!(savedFiles.View is GridView) || ScrollViewer.GetHorizontalScrollBarVisibility(savedFiles) != ScrollBarVisibility.Auto) throw new Exception("saved Finder list view lost horizontal scrolling");
                        savedList.Close(); Settings.FinderListView = false;
                        Settings.Appearance = 1; Theme.Refresh();
                        Capture(finder, "finder-list-light");
                        View(finder, false);
                        Capture(finder, "finder-icons-light");
                        var settings = new SettingsWindow("Desktop & Dock");
                        settings.Show(); settings.UpdateLayout(); Capture(settings, "dock-settings-light"); settings.Close();
                        Settings.Appearance = 2; Theme.Refresh();
                    }
                    else if (step == 8)
                    {
                        var list = (ListView)typeof(FinderWindow).GetField("files", Private).GetValue(finder);
                        list.SelectedItem = list.Items.Cast<Entry>().First(e => e.Name == "Report.csv");
                        var closeDialog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                        closeDialog.Tick += delegate
                        {
                            closeDialog.Stop();
                            var dialog = app.Windows.Cast<Window>().First(w => w.Title == "Rename");
                            Capture(dialog, "rename-dialog");
                            var body = (StackPanel)dialog.Content;
                            ((TextBox)body.Children[1]).Text = "Renamed report.csv";
                            ((Button)((StackPanel)body.Children[2]).Children[1]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        };
                        closeDialog.Start();
                        typeof(FinderWindow).GetMethod("RenameSelection", Private).Invoke(finder, null);
                        string renamed = Path.Combine(fixture, "Renamed report.csv");
                        if (!File.Exists(renamed) || File.ReadAllText(renamed) != "Name,Value\r\nSample,42\r\n") throw new Exception("rename dialog didn't preserve file contents");
                        File.Move(renamed, Path.Combine(fixture, "Report.csv"));
                        var changed = (Action)typeof(Settings).GetField("Changed", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                        Settings.LiveGlass = true; changed();
                        Settings.LiveGlass = false; changed(); Capturable(dock); Capturable(finder);
                        Settings.AutoHide = false; Settings.DockPosition = 0; dock.Place();
                    }
                    else if (step == 9) { Capture(dock, "dock-bottom"); Settings.DockPosition = 1; dock.Place(); }
                    else if (step == 10) { Capture(dock, "dock-left"); Settings.DockPosition = 2; dock.Place(); }
                    else if (step == 11)
                    {
                        Capture(dock, "dock-right");
                        timer.Stop(); dock.Close(); App.Dock = null;
                        MultiDisplayChecks.Run(fixture); dock = App.Dock;
                        InternalWindowChecks.Run();
                        DesktopRegressionChecks.Run(fixture);
                        WorkspaceChecks.Desktop(fixture);
                        AuditChecks.Desktop(fixture);
                        FeatureChecks.Handoff();
                        Console.WriteLine("desktop checks passed; snapshots in bin/qa");
                        timer.Stop();
                        if (!preview) cleanup();
                        else
                        {
                            var panel = new StackPanel { Margin = new Thickness(16) };
                            for (int i = 0; i < 3; i++)
                            {
                                int position = i;
                                var button = new Button { Content = new[] { "Bottom", "Left", "Right" }[i], Margin = new Thickness(4) };
                                button.Click += delegate { Settings.DockPosition = position; foreach (var monitorDock in App.Docks) monitorDock.Place(); };
                                panel.Children.Add(button);
                            }
                            var toggle = new Button { Content = "Toggle Finder view", Margin = new Thickness(4) };
                            bool list = false;
                            toggle.Click += delegate { list = !list; View(finder, list); finder.Activate(); };
                            panel.Children.Add(toggle);
                            var controller = new Window { Title = "OpenDock checks", Width = 300, Height = 250, Content = panel, Topmost = true };
                            controller.Closed += delegate { cleanup(); }; controller.Show();
                            Console.WriteLine("preview running; close OpenDock checks to exit");
                        }
                    }
                    step++;
                }
                catch (Exception ex) { failure = ex; cleanup(); }
            };
            app.Run();
            if (failure != null) throw new Exception("Desktop checks failed", failure);
        }
    }
}
