using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace OpenDock
{
    static class InternalWindowChecks
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static List<DockItem> Items(DockWindow dock) { return (List<DockItem>)typeof(DockWindow).GetField("items", Private).GetValue(dock); }
        static void Refresh() { typeof(App).GetMethod("RefreshWindows", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null); }
        static void Settle(int milliseconds = 200)
        {
            var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += delegate { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
        }
        static void Capture(BitmapSource image, string name)
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var file = File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "qa", name + ".png"))) encoder.Save(file);
        }
        public static void Run()
        {
            bool showRunning = Settings.ShowRunning; var previous = App.Wins;
            var first = new SettingsWindow("General") { ShowActivated = false };
            var second = new SettingsWindow("Appearance") { ShowActivated = false };
            var about = (AboutWindow)Activator.CreateInstance(typeof(AboutWindow), true); about.ShowActivated = false;
            var dialog = new MacWindow(false) { Title = "Owned dialog fixture", Width = 180, Height = 100, ShowActivated = false };
            try
            {
                Settings.ShowRunning = false;
                first.Show(); second.Show(); about.Show(); dialog.Owner = first; dialog.Show(); Settle(); Refresh();
                var tracked = App.Wins.Where(w => w.InternalKind != null).ToList();
                if (tracked.Count != 3 || tracked.Count(w => w.InternalKind == "settings") != 2 || tracked.Count(w => w.InternalKind == "about") != 1) throw new Exception("missing internal window or included an owned dialog/overlay");
                foreach (var dock in App.Docks)
                {
                    var settings = Items(dock).Single(i => i.Kind == "internal" && i.RunKey == "opendock:settings");
                    var pc = Items(dock).Single(i => i.Kind == "internal" && i.RunKey == "opendock:about");
                    if (settings.Wins.Count != 2 || settings.Img.Source != Art.Settings || !settings.Running || pc.Img.Source != Art.Computer || !pc.Running) throw new Exception("internal window grouping, icon or running indicator differs across docks");
                    var handles = (List<IntPtr>)typeof(DockWindow).GetMethod("WindowsOf", Private).Invoke(dock, new object[] { settings });
                    if (handles.Count != 2) throw new Exception("internal windows missing from hover previews");
                    var preview = (PreviewWindow)typeof(DockWindow).GetField("preview", Private).GetValue(dock);
                    preview.ShowFor(settings, handles, Art.Settings, new Point(dock.Left + dock.ActualWidth / 2, dock.Top + 25), 0, dock.MonitorWorkArea);
                    var row = (System.Windows.Controls.StackPanel)typeof(PreviewWindow).GetField("row", Private).GetValue(preview);
                    foreach (System.Windows.Controls.Border card in row.Children)
                    {
                        var host = (System.Windows.Controls.Grid)((System.Windows.Controls.StackPanel)card.Child).Children[1];
                        if (host.Children.Count != 1 || ((System.Windows.Controls.Image)host.Children[0]).Source != Art.Settings) throw new Exception("capture-protected Settings preview is blank instead of showing its icon");
                    }
                    preview.Close2();
                    dock.Sync(App.Wins);
                    if (Items(dock).Count(i => i.RunKey == settings.RunKey) != 1) throw new Exception("internal window refresh created a duplicate tile");
                }
                Capture(Art.Settings, "settings-dock-icon"); Capture(Art.Computer, "about-dock-icon");
                dialog.Close();
                var clickedDock = App.Docks.Last();
                var tile = Items(clickedDock).Single(i => i.RunKey == "opendock:settings");
                Native.Activate(new WindowInteropHelper(first).Handle); Settle();
                typeof(DockWindow).GetMethod("Click", Private).Invoke(clickedDock, new object[] { tile }); Settle(); Refresh();
                if (first.WindowState != WindowState.Minimized || second.WindowState != WindowState.Minimized || !Windows.Enumerate().Any(w => w.InternalKind == "settings")) throw new Exception("minimized settings lost its dock tile");
                typeof(DockWindow).GetMethod("Click", Private).Invoke(clickedDock, new object[] { tile }); Settle();
                if (first.WindowState == WindowState.Minimized && second.WindowState == WindowState.Minimized) throw new Exception("dock click didn't restore settings");
                dialog.Close(); first.Close(); second.Close(); Refresh(); Settle(850); Refresh();
                foreach (var dock in App.Docks)
                {
                    if (Items(dock).Any(i => i.RunKey == "opendock:settings" && i.PWant != 0)) throw new Exception("closed settings retained an active dock tile");
                    if (!Items(dock).Any(i => i.RunKey == "opendock:about" && i.Running)) throw new Exception("closing Settings removed About's separate tile");
                }
                Console.WriteLine("OpenDock windows: grouped Settings/About icons on every display, minimized restore, preview handles and closed-window removal verified");
            }
            finally
            {
                dialog.Close(); first.Close(); second.Close(); about.Close(); Settings.ShowRunning = showRunning;
                App.Wins = previous; foreach (var dock in App.Docks) dock.Sync(App.Wins);
            }
        }
    }
}
