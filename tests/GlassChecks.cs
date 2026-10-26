using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace OpenDock
{
    static class GlassChecks
    {
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sx, int sy, uint operation);
        [DllImport("user32.dll")] static extern bool GetWindowDisplayAffinity(IntPtr window, out uint affinity);
        [DllImport("user32.dll")] static extern IntPtr GetThreadDpiAwarenessContext();
        [DllImport("user32.dll")] static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);

        static void Settle()
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            timer.Tick += delegate { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }

        static System.Drawing.Color Pixel(int x, int y)
        {
            var dc = Native.GetDC(IntPtr.Zero); var memory = Native.CreateCompatibleDC(dc); var bitmap = Native.CreateCompatibleBitmap(dc, 1, 1);
            var previous = Native.SelectObject(memory, bitmap);
            try
            {
                if (!BitBlt(memory, 0, 0, 1, 1, dc, x, y, 0x40CC0020)) throw new Exception("Couldn't capture the glass fixture.");
                uint pixel = Native.GetPixel(memory, 0, 0);
                return System.Drawing.Color.FromArgb((int)(pixel & 255), (int)((pixel >> 8) & 255), (int)((pixel >> 16) & 255));
            }
            finally { Native.SelectObject(memory, previous); Native.DeleteObject(bitmap); Native.DeleteDC(memory); Native.ReleaseDC(IntPtr.Zero, dc); }
        }

        public static void Run()
        {
            if (!AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), new IntPtr(-4))) throw new Exception("UI process is not per-monitor-v2 DPI aware");
            var bounds = DisplayLayout.Pixels(DisplayLayout.Selected.Bounds);
            double x = bounds.Left + 320, y = bounds.Top + 240;
            var background = new Window { Title = "OpenDock glass test background", WindowStyle = WindowStyle.None, Left = x, Top = y, Width = 440, Height = 260, Background = System.Windows.Media.Brushes.Red, Topmost = true, ShowActivated = false };
            var overlay = new Window { Title = "OpenDock glass test icons", WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent, Left = x + 50, Top = y + 50, Width = 300, Height = 150, Topmost = true, ShowActivated = false, ShowInTaskbar = false,
                Content = new Border { Width = 40, Height = 40, Background = System.Windows.Media.Brushes.Lime, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            background.AllowsTransparency = true;
            var pane = new GlassPaneWindow();
            var color = new Border { Background = System.Windows.Media.Brushes.Red };
            background.Content = color;
            try
            {
                background.Show();
                Native.SetWindowPos(new WindowInteropHelper(background).Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                overlay.Show();
                Native.SetWindowPos(new WindowInteropHelper(overlay).Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                var region = DisplayLayout.WindowPixels(overlay);
                if (!pane.Prepare(region)) { Console.WriteLine("native compositor blur unavailable; wallpaper fallback retained"); return; }
                pane.Place(new WindowInteropHelper(overlay).Handle, region, 20, true);
                Settle();
                int px = (int)region.Left + 35, py = (int)region.Top + 35;
                var red = Pixel(px, py);
                var icon = Pixel((int)(region.Left + region.Width / 2), (int)(region.Top + region.Height / 2));
                background.Background = System.Windows.Media.Brushes.Blue;
                color.Background = System.Windows.Media.Brushes.Blue;
                Settle();
                var blue = Pixel(px, py);
                using (var shot = new Bitmap(440, 260))
                {
                    using (var graphics = Graphics.FromImage(shot)) graphics.CopyFromScreen((int)x, (int)y, 0, 0, shot.Size);
                    shot.Save(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "glass-diagnostic.png"));
                }
                if (red.R < red.B + 40 || blue.B < blue.R + 40) throw new Exception("native glass didn't follow the real window behind it: " + red + " / " + blue);
                if (icon.G < icon.R + 50 || icon.G < icon.B + 50) throw new Exception("glass pane covered the dock's icon layer");
                uint affinity;
                if (!GetWindowDisplayAffinity(pane.Handle, out affinity) || affinity != 0) throw new Exception("native glass is excluded from capture");
                pane.Place(new WindowInteropHelper(overlay).Handle, region, 20, false);
                if (pane.IsVisible) throw new Exception("hidden dock retained a glass pane");
                pane.Place(new WindowInteropHelper(overlay).Handle, region, 20, true);
                Settle();
                icon = Pixel((int)(region.Left + region.Width / 2), (int)(region.Top + region.Height / 2));
                if (icon.G < icon.R + 50 || icon.G < icon.B + 50) throw new Exception("reshown glass moved above its dock");
                var stripes = new Grid();
                stripes.ColumnDefinitions.Add(new ColumnDefinition()); stripes.ColumnDefinitions.Add(new ColumnDefinition());
                stripes.Children.Add(new Border { Background = System.Windows.Media.Brushes.Red });
                var right = new Border { Background = System.Windows.Media.Brushes.Blue }; Grid.SetColumn(right, 1); stripes.Children.Add(right);
                color.Child = stripes; Settle();
                var mixed = Pixel((int)x + 218, (int)y + 85);
                if (mixed.R < 25 || mixed.B < 25) throw new Exception("native pane is transparent without blurring: " + mixed);
                pane.Hide(); overlay.Hide();
                bool live = Settings.LiveGlass;
                Settings.LiveGlass = true;
                var dialog = new MacWindow { Title = "OpenDock window glass test", Width = 300, Height = 150, Left = x + 50, Top = y + 50, Topmost = true, ShowActivated = false };
                try
                {
                    color.Child = null; color.Background = System.Windows.Media.Brushes.Red;
                    dialog.Show(); Settle();
                    var windowRed = Pixel(px, py + 65);
                    color.Background = System.Windows.Media.Brushes.Blue; Settle();
                    var windowBlue = Pixel(px, py + 65);
                    Console.WriteLine("window glass: bounds=" + DisplayLayout.WindowPixels(dialog) + ", background=" + dialog.Background);
                    using (var shot = new Bitmap(440, 260))
                    {
                        using (var graphics = Graphics.FromImage(shot)) graphics.CopyFromScreen((int)x, (int)y, 0, 0, shot.Size);
                        shot.Save(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "window-glass-diagnostic.png"));
                    }
                    if (windowRed.R < windowRed.B + 20 || windowBlue.B < windowBlue.R + 20) throw new Exception("Finder/Settings glass didn't follow the real background: " + windowRed + " / " + windowBlue);
                }
                finally { dialog.Close(); Settings.LiveGlass = live; }
                Console.WriteLine("native glass: real red/blue backdrop, visible in capture, icon layer and hide/show verified");
            }
            finally { pane.Close(); overlay.Close(); background.Close(); }
        }
    }
}
