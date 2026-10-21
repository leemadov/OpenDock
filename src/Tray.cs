using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace OpenDock
{
    class TrayApp
    {
        public string Name, Exe, Aumid, Target;   // Target: the app's Start-menu entry, when there is one
        public uint Pid;
        public ImageSource Icon;

        // The app's own tray menu, the one right-clicking its icon on the Windows taskbar shows. Windows 11 draws the
        // tray itself, so: open the tray's hidden-icons flyout, find the app's icon (UI Automation names it after
        // its tooltip), focus it and press the menu key, which makes Windows ask the app for its menu. If the icon
        // isn't found the flyout stays open, so you can right-click it yourself.
        public void ShowTrayMenu()
        {
            Taskbar.OpenTrayFlyout();
            string name = Name;
            new Thread(() =>
            {
                try
                {
                    var cond = new PropertyCondition(AutomationElement.AutomationIdProperty, "NotifyItemIcon");
                    for (int attempt = 0; attempt < 4; attempt++)
                    {
                        Thread.Sleep(attempt == 0 ? 700 : 400);   // the flyout opens ~250ms after Win+B
                        foreach (AutomationElement e in AutomationElement.RootElement.FindAll(TreeScope.Descendants, cond))
                        {
                            string label = (e.Current.Name ?? "").Trim().Split('\n')[0].Trim();
                            if (label.Length == 0 || !(label.StartsWith(name, StringComparison.OrdinalIgnoreCase) || name.StartsWith(label, StringComparison.OrdinalIgnoreCase))) continue;
                            e.SetFocus();
                            Thread.Sleep(120);
                            Native.SendKeys(0x5D);   // VK_APPS, the keyboard's context-menu key
                            return;
                        }
                    }
                }
                catch (Exception ex) { App.Log(ex); }
            }) { IsBackground = true }.Start();
        }

        // Bring up the app's window; single-instance tray apps (Discord, Steam, Spotify...) show themselves when relaunched.
        public void Open()
        {
            var w = App.Wins.FirstOrDefault(x => string.Equals(x.Exe, Exe, StringComparison.OrdinalIgnoreCase));
            if (w != null) { Native.Activate(w.Hwnd); return; }
            var hidden = HiddenMainWindow();
            if (hidden != IntPtr.Zero)
            {
                Native.ShowWindow(hidden, Native.SW_SHOW);   // async; activate once the app has shown it
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                t.Tick += delegate { t.Stop(); Native.Activate(hidden); App.QueueRefresh(); };
                t.Start();
            }
            else Launcher.Open(Target ?? Exe);
        }

        // "Minimize to tray" usually just hides the app's main window. Find it: titled, has a caption or frame,
        // not a tool window, reasonably large -- the biggest such hidden window the process owns.
        IntPtr HiddenMainWindow()
        {
            IntPtr best = IntPtr.Zero; long bestArea = 0;
            Native.EnumWindows((h, l) =>
            {
                uint pid; Native.GetWindowThreadProcessId(h, out pid);
                if (pid != Pid || Native.IsWindowVisible(h) || Native.GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero || Native.GetWindowTextLength(h) == 0) return true;
                int style = Native.GetWindowLong(h, Native.GWL_STYLE), ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
                if ((style & 0x00C00000) == 0 && (style & 0x00040000) == 0) return true;   // WS_CAPTION / WS_THICKFRAME
                if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return true;
                Native.RECT r; Native.GetWindowRect(h, out r);
                long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                if (r.Right - r.Left >= 300 && r.Bottom - r.Top >= 200 && area > bestArea) { best = h; bestArea = area; }
                return true;
            }, IntPtr.Zero);
            return best;
        }

        // Politely: ask every window the app owns (including its hidden tray window) to close.
        public void Quit()
        {
            Native.EnumWindows((h, l) =>
            {
                uint pid; Native.GetWindowThreadProcessId(h, out pid);
                if (pid == Pid) Native.PostMessage(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);
        }

        public void Kill()
        {
            foreach (var p in Process.GetProcesses())
                using (p)
                {
                    try { if (string.Equals(Native.ProcessPath((uint)p.Id), Exe, StringComparison.OrdinalIgnoreCase)) p.Kill(); } catch { }
                }
        }
    }

    // Windows records every app that puts an icon in the notification area (exe path + a snapshot of the icon)
    // under HKCU\Control Panel\NotifyIconSettings. The ones whose process is alive are the background apps.
    static class Tray
    {
        public static List<TrayApp> Running()
        {
            var icons = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var tips = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings"))
                if (root != null)
                    foreach (var name in root.GetSubKeyNames())
                        using (var k = root.OpenSubKey(name))
                        {
                            var path = k == null ? null : k.GetValue("ExecutablePath") as string;
                            if (string.IsNullOrEmpty(path)) continue;
                            path = Apps.ExpandKnownFolder(path) ?? path;
                            if (!icons.ContainsKey(path)) icons[path] = k.GetValue("IconSnapshot") as byte[];
                            var tip = k.GetValue("InitialTooltip") as string;
                            if (!string.IsNullOrWhiteSpace(tip) && !tips.ContainsKey(path)) tips[path] = tip.Trim();
                        }

            var list = new List<TrayApp>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            uint me = (uint)Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcesses())
                using (p)
                {
                    uint pid = (uint)p.Id;
                    if (pid == me) continue;
                    string exe = Native.ProcessPath(pid);
                    if (exe == null || !icons.ContainsKey(exe) || !seen.Add(exe) || exe.EndsWith(@"\explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    string aumid = Native.ProcessAumid(pid);
                    var entry = (aumid != null ? Apps.ById(aumid) : null) ?? EntryFor(exe);
                    list.Add(new TrayApp { Exe = exe, Pid = pid, Aumid = aumid, Name = NameFor(exe, entry, tips), Icon = Png(icons[exe]),
                        Target = entry != null ? entry.Target : aumid != null ? "app:" + aumid : null });
                }
            return list.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        static readonly string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + Path.DirectorySeparatorChar;

        // Tray processes are often an app's inner engine (SignalRGB runs app-2.5.74\Signal-x64\SignalRgb.exe while its
        // Start-menu entry is SignalRgbLauncher.exe one folder up). Use the Start entry whose folder contains the process.
        static AppEntry EntryFor(string exe)
        {
            AppEntry best = null; int bestLen = 0;
            foreach (var a in Apps.All)
            {
                if (a.Exe == null) continue;
                string dir = Path.GetDirectoryName(a.Exe) + Path.DirectorySeparatorChar;
                if (dir.StartsWith(windir, StringComparison.OrdinalIgnoreCase) || !exe.StartsWith(dir, StringComparison.OrdinalIgnoreCase) || dir.Length <= bestLen) continue;
                best = a; bestLen = dir.Length;
            }
            return best;
        }

        static readonly string[] hosts = { "python.exe", "pythonw.exe", "javaw.exe", "java.exe", "node.exe", "electron.exe", "rundll32.exe" };

        static string NameFor(string exe, AppEntry entry, Dictionary<string, string> tips)
        {
            if (entry != null) return entry.Name;
            string tip;
            // scripts running inside a generic host are better known by their tray tooltip ("OpenClicky", not "Python 3.10")
            if (hosts.Contains(Path.GetFileName(exe).ToLowerInvariant()) && tips.TryGetValue(exe, out tip)) return tip;
            string name = Apps.FriendlyName(exe);
            return name.EndsWith(" notification icon", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 18) : name;
        }

        static ImageSource Png(byte[] data)
        {
            if (data == null || data.Length == 0) return null;
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.StreamSource = new MemoryStream(data);
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch { return null; }
        }
    }
}
