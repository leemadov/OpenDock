using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace OpenDock
{
    static class MultiDisplayChecks
    {
        static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static List<DockItem> Items(DockWindow dock) { return (List<DockItem>)typeof(DockWindow).GetField("items", Private).GetValue(dock); }

        static void Settle()
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            timer.Tick += delegate { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }

        static void CheckDocks(int expected)
        {
            if (App.Docks.Count != expected || App.Docks.Select(d => d.Device).Distinct().Count() != expected) throw new Exception("missing or duplicate monitor dock");
            foreach (var dock in App.Docks) { dock.Place(); dock.UpdateLayout(); }
            // WPF and Explorer post native position/work-area updates asynchronously.
            Settle();
            foreach (var dock in App.Docks)
            {
                if (!dock.IsVisible || !dock.MonitorBounds.Contains(new Rect(dock.Left, dock.Top, dock.ActualWidth, dock.ActualHeight))) throw new Exception("dock outside its own monitor: " + dock.Device);
                if (!Settings.AutoHide)
                {
                    var bounds = dock.MonitorBounds;
                    double reserve = Settings.IconSize + 12 + 6 + 4;
                    Func<bool> reserved = () =>
                    {
                        var work = dock.MonitorWorkArea;
                        return Settings.DockPosition == 0 ? work.Bottom <= bounds.Bottom - reserve + 1
                             : Settings.DockPosition == 1 ? work.Left >= bounds.Left + reserve - 1
                             : work.Right <= bounds.Right - reserve + 1;
                    };
                    int attempts = 0;
                    while (!reserved() && attempts++ < 20) Settle();
                    if (!reserved()) throw new Exception("monitor work area not reserved: " + dock.Device + ", edge=" + Settings.DockPosition + ", work=" + dock.MonitorWorkArea + ", bounds=" + bounds);
                }
                Native.RECT rc;
                if (!Native.GetWindowRect(new WindowInteropHelper(dock).Handle, out rc) || !dock.MonitorBounds.Contains(DisplayLayout.Dip(rc))) throw new Exception("native dock rectangle escaped monitor: " + dock.Device);
            }
        }

        public static void Run(string fixture)
        {
            Settings.DockOnAllDisplays = true; Settings.DisplayDevice = ""; Settings.AutoHide = false;
            var screens = Sys.Screens();
            App.ReconcileDocks();
            var pins = Settings.Pins.ToList();
            var wins = App.Wins;
            try
            {
                for (int position = 0; position < 3; position++)
                {
                    Settings.DockPosition = position;
                    foreach (bool autoHide in new[] { false, true })
                    {
                        Settings.AutoHide = autoHide; CheckDocks(screens.Count);
                        Console.WriteLine("multi-display: " + screens.Count + " docks, edge=" + position + ", auto-hide=" + autoHide);
                    }
                }
                Settings.AutoHide = false; Settings.DockPosition = 0; CheckDocks(screens.Count);
                // Exercise shared pin insertion, removal and order without persisting preferences.
                Settings.Pins.Add("Shared fixture|" + Path.Combine(fixture, "Report.csv"));
                Settings.Pins.Reverse(); App.SyncPins(null);
                foreach (var dock in App.Docks)
                    if (!Items(dock).Where(i => i.Pinned).Select(i => i.Name + "|" + i.Target).SequenceEqual(Settings.Pins)) throw new Exception("pins differ across displays");
                Settings.Pins = pins.ToList(); App.SyncPins(null);
                App.Wins = new List<WinInfo> { new WinInfo { Hwnd = new IntPtr(123), Exe = Path.Combine(fixture, "fixture.exe"), Title = "Fixture app" } };
                foreach (var dock in App.Docks) { dock.Sync(App.Wins); if (!Items(dock).Any(i => i.Wins.Count == 1)) throw new Exception("running app missing on display"); }
                App.Wins = wins; App.SyncPins(null);

                var secondary = App.Docks.Last(); App.Dock = secondary;
                if (DisplayLayout.ActiveBounds != secondary.MonitorBounds) throw new Exception("active dock lost its monitor");
                var finder = new FinderWindow(fixture); finder.Show(); finder.UpdateLayout();
                if (!secondary.MonitorBounds.Contains(new Rect(finder.Left, finder.Top, finder.ActualWidth, finder.ActualHeight))) throw new Exception("Finder opened on a different monitor");
                var anchor = new Point(secondary.Left + secondary.ActualWidth / 2, secondary.Top + 25);
                var preview = (PreviewWindow)typeof(DockWindow).GetField("preview", Private).GetValue(secondary);
                preview.ShowFor(Items(secondary).First(i => i.Kind == "finder"), new List<IntPtr> { new WindowInteropHelper(finder).Handle }, Art.Finder, anchor, 0, secondary.MonitorWorkArea);
                if (!secondary.MonitorBounds.Contains(new Rect(preview.Left, preview.Top, preview.ActualWidth, preview.ActualHeight))) throw new Exception("previews opened on a different monitor");
                preview.Close2();
                var stack = new StackWindow(fixture, "Fixture downloads"); stack.ShowAbove(anchor, 0, secondary.MonitorWorkArea);
                if (!secondary.MonitorBounds.Contains(new Rect(stack.Left, stack.Top, stack.ActualWidth, stack.ActualHeight))) throw new Exception("Downloads opened on a different monitor");
                stack.Close(); finder.Close();

                // Remove/recreate docks through the same reconciliation used by settings and display changes.
                for (int cycle = 0; cycle < 3; cycle++)
                {
                    var previous = App.Docks.ToList();
                    Settings.DockOnAllDisplays = false; Settings.DisplayDevice = screens.Last().Device; App.ReconcileDocks(); CheckDocks(1);
                    if (App.Dock.Device != screens.Last().Device) throw new Exception("single-display selection ignored");
                    foreach (var removed in previous.Where(d => !App.Docks.Contains(d)))
                    {
                        if (((DispatcherTimer)typeof(DockWindow).GetField("trashTimer", Private).GetValue(removed)).IsEnabled || (bool)typeof(DockWindow).GetField("looping", Private).GetValue(removed)) throw new Exception("removed dock still has active timers or rendering");
                    }
                    Settings.DisplayDevice = "disconnected-display"; App.ReconcileDocks(); CheckDocks(1);
                    if (!App.Dock.Screen.Primary) throw new Exception("disconnected monitor didn't fall back to primary");
                    Settings.DisplayDevice = ""; Settings.DockOnAllDisplays = true; App.ReconcileDocks(); CheckDocks(screens.Count);
                    App.MenuBar.Place(); App.MenuBar.UpdateLayout();
                    if (Math.Abs(App.MenuBar.ActualWidth - DisplayLayout.Bounds.Width) > 1) throw new Exception("menu bar size changed during monitor registration");
                    var handles = App.Docks.Select(d => new WindowInteropHelper(d).Handle).ToArray();
                    App.ReconcileDocks();
                    if (!handles.SequenceEqual(App.Docks.Select(d => new WindowInteropHelper(d).Handle))) throw new Exception("unchanged layout recreated dock windows");
                }
                var handlers = (Action<List<WinInfo>>)typeof(App).GetField("WindowsChanged", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                if (handlers.GetInvocationList().Count(d => d.Target is DockWindow) != screens.Count) throw new Exception("closed docks leaked event subscriptions");

                // Render settled geometry on every physical display, including secondary-screen bounds.
                int index = 0;
                foreach (var dock in App.Docks)
                {
                    typeof(DockWindow).GetMethod("Frame", Private).Invoke(dock, new object[] { null, EventArgs.Empty }); dock.UpdateLayout();
                    var bitmap = new RenderTargetBitmap((int)dock.ActualWidth, (int)dock.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(dock);
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var file = File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "qa", "monitor-" + ++index + ".png"))) png.Save(file);
                    Console.WriteLine("verified dock " + dock.Device + ": " + dock.Left + "," + dock.Top + " " + dock.ActualWidth + "x" + dock.ActualHeight);
                }
            }
            finally { Settings.Pins = pins; App.Wins = wins; }
        }

        // Read-only post-installation verification of the actual app process, independent of test windows.
        public static int Inspect(int pid)
        {
            var windows = new List<Native.RECT>();
            Native.EnumWindows((h, data) =>
            {
                uint owner; Native.GetWindowThreadProcessId(h, out owner);
                Native.RECT rc;
                if (owner == pid && Native.WindowText(h) == "OpenDock" && Native.IsWindowVisible(h) && Native.GetWindowRect(h, out rc)) windows.Add(rc);
                return true;
            }, IntPtr.Zero);
            int missing = 0;
            foreach (var screen in Sys.Screens())
            {
                var bounds = screen.Bounds;
                bool found = windows.Any(r => r.Left >= bounds.Left && r.Right <= bounds.Right && r.Top >= bounds.Top && r.Bottom <= bounds.Bottom);
                Console.WriteLine(screen.Device + ": visible dock=" + found + ", bounds=" + bounds.Left + "," + bounds.Top + ".." + bounds.Right + "," + bounds.Bottom);
                if (!found) missing++;
            }
            Console.WriteLine("visible dock windows: " + windows.Count);
            return missing == 0 ? 0 : 1;
        }
    }
}
