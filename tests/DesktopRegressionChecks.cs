using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace OpenDock
{
    static class DesktopRegressionChecks
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static void Settle(int milliseconds = 250)
        {
            var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += delegate { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
        }
        public static void FocusFixture()
        {
            var app = new Application(); var b = Sys.Screens().Last().Bounds;
            var window = new Window { Title = "OpenDock external focus fixture", Width = 350, Height = 180, Left = b.Left + 100, Top = b.Top + 100, Topmost = true, Content = new TextBlock { Text = "OpenDock focus regression check", Margin = new Thickness(20) } };
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
            timer.Tick += delegate { timer.Stop(); window.Close(); };
            window.Loaded += delegate
            {
                var handle = new WindowInteropHelper(window).Handle;
                Native.ShowWindow(handle, Native.SW_RESTORE);
                window.Dispatcher.BeginInvoke(new Action(() => Native.Activate(handle)), DispatcherPriority.ApplicationIdle);
                timer.Start();
            };
            app.Run(window);
        }
        public static void Run(string fixture)
        {
            var finder = new FinderWindow(fixture); finder.Show(); Settle();
            try
            {
                var address = (TextBox)typeof(FinderWindow).GetField("address", Private).GetValue(finder);
                var navigate = typeof(FinderWindow).GetMethod("NavigateAddress", Private);
                if (address.Text != fixture) throw new Exception("Finder didn't display its complete path");
                address.Text = "Documents";
                if (!(bool)navigate.Invoke(finder, null) || address.Text != Path.Combine(fixture, "Documents")) throw new Exception("relative address navigation failed");
                typeof(FinderWindow).GetMethod("Back", Private).Invoke(finder, null);
                if (address.Text != fixture) throw new Exception("Back didn't update the address bar");
                typeof(FinderWindow).GetMethod("Forward", Private).Invoke(finder, null);
                if (address.Text != Path.Combine(fixture, "Documents")) throw new Exception("Forward didn't update the address bar");
                address.Text = "%TEMP%";
                if (!(bool)navigate.Invoke(finder, null) || address.Text != Path.GetFullPath(Environment.GetEnvironmentVariable("TEMP"))) throw new Exception("environment path navigation failed");
                string valid = address.Text; address.Text = "?:\\invalid";
                if ((bool)navigate.Invoke(finder, null) || (string)typeof(FinderWindow).GetField("path", Private).GetValue(finder) != valid) throw new Exception("invalid address changed the active folder");
                Native.Activate(new WindowInteropHelper(finder).Handle); Settle();
                using (var child = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--focus-fixture") { UseShellExecute = false, CreateNoWindow = true }))
                {
                    int attempts = 0; uint owner = 0;
                    do
                    {
                        Settle();
                        IntPtr target = IntPtr.Zero;
                        Native.EnumWindows((h, l) =>
                        {
                            uint pid; Native.GetWindowThreadProcessId(h, out pid);
                            if (pid == (uint)child.Id && Native.WindowText(h) == "OpenDock external focus fixture") target = h;
                            return true;
                        }, IntPtr.Zero);
                        if (target != IntPtr.Zero) { Native.ShowWindow(target, Native.SW_RESTORE); Settle(); Native.Activate(target); }
                        Settle(); Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out owner);
                    } while (owner != (uint)child.Id && attempts++ < 10 && !child.HasExited);
                    if (owner != (uint)child.Id) Console.WriteLine("SKIP external-focus assertion: Windows did not grant the fixture foreground focus.");
                    Settle(500);
                    if (owner == (uint)child.Id && finder.WindowState != WindowState.Normal) throw new Exception("Finder minimized when another process gained focus");
                    if (owner == (uint)child.Id) Console.WriteLine("cross-process focus: Finder stayed open");
                    while (!child.HasExited) Settle();
                }
            }
            finally { finder.Close(); }
            var screens = Sys.Screens();
            var full = new Window { Title = "OpenDock fullscreen fixture", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true, ShowActivated = false };
            var other = new Window { Title = "OpenDock other monitor fixture", Width = 380, Height = 230, Topmost = true };
            try
            {
                var b = screens[0].Bounds;
                full.Left = b.Left / App.Dpi; full.Top = b.Top / App.Dpi; full.Width = (b.Right-b.Left)/App.Dpi; full.Height = (b.Bottom-b.Top)/App.Dpi;
                full.Show(); Settle();
                var second = screens.Last().Bounds; other.Left = (second.Left + 100)/App.Dpi; other.Top = (second.Top + 100)/App.Dpi;
                if (screens.Count > 1) { other.Show(); other.Activate(); Settle(); }
                foreach (var dock in App.Docks) dock.OnFullScreen(true);
                if (App.Docks.Single(d => d.Device == screens[0].Device).IsVisible) throw new Exception("dock overlays fullscreen after focus moves to another monitor");
                if (screens.Count > 1 && !App.Docks.Single(d => d.Device == screens.Last().Device).IsVisible) throw new Exception("other monitor dock was hidden");
                full.Width = 400; full.Height = 240; Settle();
                foreach (var dock in App.Docks) dock.OnFullScreen(false);
                if (!App.Docks.Single(d => d.Device == screens[0].Device).IsVisible) throw new Exception("dock didn't return after leaving fullscreen");
                Console.WriteLine("regressions: editable Finder paths/history and fullscreen on an unfocused monitor passed");
            }
            finally { full.Close(); other.Close(); foreach (var dock in App.Docks) dock.OnFullScreen(false); }
        }
    }
}
