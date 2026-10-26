using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace OpenDock
{
    static class Settings
    {
        public static int DockPosition = 0;      // 0 = bottom, 1 = left, 2 = right
        public static string DisplayDevice = ""; // empty = follow primary, otherwise Windows' display device name
        public static bool LiveGlass = false;    // capture exclusion is opt-in; wallpaper frost is capture-safe
        public static bool FinderListView = false;
        public static int IconSize = 60;          // dock slot size (DIP)
        public static int MagnifySize = 100;
        public static bool Magnify = true;
        public static bool AutoHide = false;
        public static bool HideTaskbar = true;
        public static bool ShowMenuBar = true;
        public static int MenuBarHeight = 30;     // menu bar height (DIP)
        public static int DockSpacing = 0;        // extra space between dock icons (DIP)
        public static int ColorProfile = 0;       // index into ColorProfiles.Presets, or -1 = Custom
        public static int CustomSaturation = 100, CustomContrast = 100, CustomBrightness = 0, CustomWarmth = 0;   // percent
        public static bool Bounce = true;
        public static bool Indicators = true;
        public static bool IconTiles = true;
        public static bool ShowRunning = true;
        public static int Appearance = 2;         // 0 = follow Windows, 1 = light, 2 = dark
        public static bool FirstRun = true;
        public static int TaskbarStateToRestore = -1; // survives a crash so the next exit can still restore it
        public static List<string> Pins = new List<string>(); // "Name|Target"
        public static List<string> Hidden = new List<string>(); // AppsFolder ids hidden from Launchpad
        public static List<string> Wallpapers = new List<string>(); // the wallpaper library (image paths), newest first
        public static bool WallpapersSeeded = false;            // the library was filled with the Windows wallpapers once

        public static event Action Changed;
        static readonly string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenDock");
        public static string WallpaperDir { get { return Path.Combine(dir, "Wallpapers"); } }   // copies of photos you add
        static readonly string file = Path.Combine(dir, "settings.txt");
        public static string LogFile { get { return Path.Combine(dir, "error.log"); } }

        static IEnumerable<FieldInfo> Fields()
        {
            return typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(int) || f.FieldType == typeof(bool) || f.FieldType == typeof(string));
        }

        public static void Load()
        {
            if (!File.Exists(file)) return;
            foreach (var line in File.ReadAllLines(file))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line.Substring(0, eq), v = line.Substring(eq + 1);
                if (k == "pin") { Pins.Add(v); continue; }
                if (k == "hide") { Hidden.Add(v); continue; }
                if (k == "wall") { Wallpapers.Add(v); continue; }
                var f = Fields().FirstOrDefault(x => x.Name == k);
                try { if (f != null) f.SetValue(null, Convert.ChangeType(v, f.FieldType)); } catch { }
            }
        }

        public static void Save()
        {
            Directory.CreateDirectory(dir);
            var lines = Fields().Select(f => f.Name + "=" + f.GetValue(null)).ToList();
            lines.AddRange(Pins.Select(p => "pin=" + p));
            lines.AddRange(Hidden.Select(h => "hide=" + h));
            lines.AddRange(Wallpapers.Select(w => "wall=" + w));
            File.WriteAllLines(file, lines);
        }

        public static void Apply() { Save(); if (Changed != null) Changed(); }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public static bool LaunchAtLogin
        {
            get { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("OpenDock") != null; }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue("OpenDock", "\"" + Assembly.GetExecutingAssembly().Location + "\"");
                    else k.DeleteValue("OpenDock", false);
                }
                LogonTask(value);
            }
        }

        // Windows holds back "Run" entries and starts them in batches minutes after sign-in when there are many
        // startup apps. A per-user logon task starts OpenDock right away (no admin needed when it's defined for
        // the current user only). The Run entry stays as a fallback; the second copy just exits (single instance).
        public static void LogonTask(bool on)
        {
            try
            {
                string args;
                if (on)
                {
                    string user = SecurityElement.Escape(System.Security.Principal.WindowsIdentity.GetCurrent().Name);
                    string xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">"
                        + "<RegistrationInfo><Description>Starts OpenDock as soon as you sign in.</Description></RegistrationInfo>"
                        + "<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + user + "</UserId></LogonTrigger></Triggers>"
                        + "<Principals><Principal id=\"Author\"><UserId>" + user + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>"
                        + "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>"
                        + "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>4</Priority></Settings>"
                        + "<Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(Assembly.GetExecutingAssembly().Location) + "</Command></Exec></Actions></Task>";
                    string file = Path.Combine(Path.GetTempPath(), "OpenDock-logon-task.xml");
                    File.WriteAllText(file, xml, Encoding.Unicode);
                    args = "/create /tn OpenDock /f /xml \"" + file + "\"";
                }
                else args = "/delete /tn OpenDock /f";
                var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("schtasks.exe", args) { CreateNoWindow = true, UseShellExecute = false });
                p.WaitForExit(10000);
            }
            catch (Exception ex) { App.Log(ex); }
        }
    }

    // Reserves screen space like the Windows taskbar does, so maximized windows stop at the menu bar / dock.
    class AppBar
    {
        readonly IntPtr hwnd; readonly uint msg; uint edge; bool registered; int thickness; bool placing;
        public Action<bool> FullScreen;
        public Action<Native.RECT> Placed;

        public AppBar(Window w, uint edge)
        {
            hwnd = new WindowInteropHelper(w).Handle;
            this.edge = edge;
            msg = Native.RegisterWindowMessage("OpenDockAppBar");
            HwndSource.FromHwnd(hwnd).AddHook(Hook);
        }

        Native.APPBARDATA Data() { return new Native.APPBARDATA { cbSize = Marshal.SizeOf(typeof(Native.APPBARDATA)), hWnd = hwnd, uCallbackMessage = msg, uEdge = edge }; }

        public void Reserve(int px)
        {
            if (placing) return;
            placing = true;
            try
            {
                thickness = px;
                var d = Data();
                if (!registered) { Native.SHAppBarMessage(Native.ABM_NEW, ref d); registered = true; }
                d.rc = DisplayLayout.Selected.Bounds;
                Native.SHAppBarMessage(Native.ABM_QUERYPOS, ref d);
                if (edge == Native.ABE_TOP) d.rc.Bottom = d.rc.Top + px;
                else if (edge == Native.ABE_BOTTOM) d.rc.Top = d.rc.Bottom - px;
                else if (edge == Native.ABE_LEFT) d.rc.Right = d.rc.Left + px;
                else d.rc.Left = d.rc.Right - px;
                Native.SHAppBarMessage(Native.ABM_SETPOS, ref d);
                if (Placed != null) Placed(d.rc);
            }
            finally { placing = false; }
        }

        public void SetEdge(uint value) { if (edge != value) { Remove(); edge = value; } }

        public void Remove()
        {
            if (!registered) return;
            var d = Data();
            Native.SHAppBarMessage(Native.ABM_REMOVE, ref d);
            registered = false;
        }

        public void Reregister() { if (registered) { registered = false; Reserve(thickness); } }

        IntPtr Hook(IntPtr h, int m, IntPtr w, IntPtr l, ref bool handled)
        {
            if (m == msg)
            {
                if ((int)w == Native.ABN_POSCHANGED && registered) Reserve(thickness);
                else if ((int)w == Native.ABN_FULLSCREENAPP && FullScreen != null) FullScreen(l != IntPtr.Zero);
            }
            return IntPtr.Zero;
        }
    }

    static class Taskbar
    {
        static bool hidden;

        static IEnumerable<IntPtr> Bars()
        {
            var main = Native.FindWindow("Shell_TrayWnd", null);
            if (main != IntPtr.Zero) yield return main;
            IntPtr h = IntPtr.Zero;
            while ((h = Native.FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero) yield return h;
        }

        static void SetState(int state)
        {
            var d = new Native.APPBARDATA { cbSize = Marshal.SizeOf(typeof(Native.APPBARDATA)), hWnd = Native.FindWindow("Shell_TrayWnd", null), lParam = (IntPtr)state };
            Native.SHAppBarMessage(Native.ABM_SETSTATE, ref d);
        }

        // Auto-hide releases the taskbar's reserved space; hiding the window removes the leftover sliver.
        public static void Hide()
        {
            var tb = Native.FindWindow("Shell_TrayWnd", null);
            if (tb == IntPtr.Zero) return;
            if (Settings.TaskbarStateToRestore < 0)
            {
                var d = new Native.APPBARDATA { cbSize = Marshal.SizeOf(typeof(Native.APPBARDATA)), hWnd = tb };
                Settings.TaskbarStateToRestore = (int)Native.SHAppBarMessage(Native.ABM_GETSTATE, ref d);
                Settings.Save();
            }
            SetState(1 /* ABS_AUTOHIDE */);
            foreach (var b in Bars()) Native.ShowWindow(b, Native.SW_HIDE);
            hidden = true;
        }

        // Explorer occasionally re-shows the taskbar (after the auto-hide switch, Start menu, etc.).
        static DateTime pausedUntil;

        // The real Windows tray: Win+B focuses the notification area, Enter opens the hidden-icons flyout.
        // Clicking icons there does exactly what the apps intend (their own menus), even with the taskbar hidden.
        public static void OpenTrayFlyout()
        {
            pausedUntil = DateTime.Now.AddSeconds(20);
            foreach (var b in Bars()) Native.ShowWindow(b, Native.SW_SHOWNOACTIVATE);
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            int step = 0;
            t.Tick += delegate
            {
                if (step++ == 0) Native.SendKeys(0x5B, 0x42);   // Win+B
                else { t.Stop(); Native.SendKeys(0x0D); }       // Enter
            };
            t.Start();
        }

        public static void Enforce()
        {
            if (!hidden || DateTime.Now < pausedUntil) return;
            foreach (var b in Bars()) if (Native.IsWindowVisible(b)) Native.ShowWindow(b, Native.SW_HIDE);
        }

        // On quit: the taskbar comes back as auto-hide only, so it stays out of sight at the next sign-in
        // (before OpenDock starts) yet is still reachable at the screen edge while OpenDock isn't running.
        public static void Release()
        {
            if (!hidden) return;
            foreach (var b in Bars()) Native.ShowWindow(b, Native.SW_SHOW);
            hidden = false;
        }

        // Turning the setting off puts the taskbar back exactly as it was.
        public static void Restore()
        {
            if (!hidden && Settings.TaskbarStateToRestore < 0) return;
            foreach (var b in Bars()) Native.ShowWindow(b, Native.SW_SHOW);
            if (Settings.TaskbarStateToRestore >= 0) SetState(Settings.TaskbarStateToRestore);
            Settings.TaskbarStateToRestore = -1;
            Settings.Save();
            hidden = false;
        }
    }

    static class App
    {
        public static double Dpi = 1;
        public static DockWindow Dock;
        public static MenuBarWindow MenuBar;
        public static LaunchpadWindow Launchpad;
        public static SpotlightWindow Spotlight;
        public static readonly List<FinderWindow> Finders = new List<FinderWindow>();
        public static IntPtr LastForeground;                 // last app window the user was in (not ours)
        public static event Action<IntPtr> ForegroundChanged;
        public static event Action<IntPtr> WindowFlashed;     // an app asked for attention (what the taskbar shows as a flashing button)
        public static event Action<List<WinInfo>> WindowsChanged;
        public static List<WinInfo> Wins = new List<WinInfo>();

        static HwndSource msgWindow;
        static readonly List<Native.WinEventProc> hooks = new List<Native.WinEventProc>(); // keep delegates alive
        static DispatcherTimer refresh;
        static bool cleaned;

        [STAThread]
        static void Main(string[] args)
        {
            bool first;
            using (var mutex = new Mutex(true, "OpenDock.SingleInstance", out first))
            {
                if (!first && args.Contains("--restart"))
                {
                    try { first = mutex.WaitOne(15000); } catch (AbandonedMutexException) { first = true; } // previous copy is on its way out
                }
                if (!first || args.Contains("--quit"))
                {
                    // "OpenDock.exe --quit" asks the running copy to exit cleanly (restores the taskbar).
                    var host = Native.FindWindow(null, "OpenDockHost");
                    if (host != IntPtr.Zero && args.Contains("--quit")) Native.PostMessage(host, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    return;
                }
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var started = DateTime.Now;
                app.DispatcherUnhandledException += (s, e) =>
                {
                    Log(e.Exception);
                    e.Handled = true;
                    // If the graphics driver resets, WPF's render thread dies and every window freezes. Start fresh.
                    var com = e.Exception as COMException;
                    if (com != null && com.ErrorCode == unchecked((int)0x88980406) && (DateTime.Now - started).TotalSeconds > 30 && !cleaned)
                    {
                        Cleanup();
                        System.Diagnostics.Process.Start(Assembly.GetExecutingAssembly().Location, "--restart");
                        Environment.Exit(0);
                    }
                };
                AppDomain.CurrentDomain.UnhandledException += (s, e) => { Log(e.ExceptionObject); Cleanup(); };
                SystemEvents.SessionEnding += delegate { Cleanup(); };
                using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero)) Dpi = g.DpiX / 96.0;

                Settings.Load();
                Theme.Init(app);
                Glass.Init();
                Outside.Init();
                Icons.Start();
                Apps.Refresh(true);
                Wallpaper.Load();
                SystemEvents.UserPreferenceChanged += (s, e) => { if (e.Category == UserPreferenceCategory.Desktop) Wallpaper.Load(); };
                SystemEvents.DisplaySettingsChanged += delegate
                {
                    app.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        Wallpaper.Load();
                        if (MenuBar != null) MenuBar.Place();
                        if (Dock != null) Dock.Place();
                        if (Launchpad != null && Launchpad.IsOpen) Launchpad.Close2(null);
                        if (Spotlight != null && Spotlight.IsVisible) Spotlight.Close2();
                    }));
                };

                app.Startup += delegate { Start(); };
                app.Run();
                Cleanup();
            }
        }

        static int pinged, answered;

        // If the UI thread stops answering (stuck waiting on another app, a driver hiccup...), start a fresh copy
        // instead of leaving a frozen dock and menu bar. Each ping is checked on its own, so sleep/hibernate
        // (where the clock jumps) can't trigger it.
        static void StartWatchdog(Dispatcher ui)
        {
            var t = new Thread(() =>
            {
                Thread.Sleep(30000);   // let startup finish
                int misses = 0;
                while (true)
                {
                    int id = Interlocked.Increment(ref pinged);
                    ui.BeginInvoke(new Action(() => Volatile.Write(ref answered, id)));
                    Thread.Sleep(2000);
                    misses = Volatile.Read(ref answered) == id ? 0 : misses + 1;
                    if (misses < 6) continue;
                    Log("UI unresponsive for ~12s; restarting");
                    System.Diagnostics.Process.Start(Assembly.GetExecutingAssembly().Location, "--restart");
                    System.Diagnostics.Process.GetCurrentProcess().Kill();   // the stuck thread can't run a clean exit
                }
            }) { IsBackground = true, Name = "OpenDock watchdog" };
            t.Start();
        }

        static void Start()
        {
            StartWatchdog(Dispatcher.CurrentDispatcher);
            ColorProfiles.Apply();
            if (Settings.LaunchAtLogin) System.Threading.Tasks.Task.Run(() => Settings.LogonTask(true));   // upgrade older "Run"-only installs; keeps the path current
            // Hidden top-level window: receives hotkeys and the TaskbarCreated broadcast (Explorer restarts).
            msgWindow = new HwndSource(new HwndSourceParameters("OpenDockHost") { WindowStyle = unchecked((int)0x80000000), Width = 0, Height = 0 });
            uint taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
            uint shellHook = Native.RegisterWindowMessage("SHELLHOOK");
            msgWindow.AddHook((IntPtr h, int m, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (m == Native.WM_HOTKEY && (int)w == 1) ToggleSpotlight();
                if (m == shellHook && (int)w.ToInt64() == 0x8006 /* HSHELL_FLASH */ && WindowFlashed != null) WindowFlashed(l);
                if (m == Native.WM_CLOSE) { handled = true; Quit(); }
                if (m == taskbarCreated) Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
                {
                    if (Settings.HideTaskbar) Taskbar.Hide();
                    if (MenuBar != null) MenuBar.Bar.Reregister();
                    if (Dock != null) Dock.Bar.Reregister();
                }), DispatcherPriority.Background);
                return IntPtr.Zero;
            });
            Native.RegisterShellHookWindow(msgWindow.Handle);
            Native.RegisterHotKey(msgWindow.Handle, 1, 0x1 /* MOD_ALT */ | 0x4000 /* NOREPEAT */, 0x20 /* Space */);

            if (Settings.HideTaskbar || Settings.TaskbarStateToRestore >= 0) { if (Settings.HideTaskbar) Taskbar.Hide(); else Taskbar.Restore(); }
            MenuBar = new MenuBarWindow();
            if (Settings.ShowMenuBar) MenuBar.Show();
            Dock = new DockWindow();
            Dock.Show();
            Launchpad = new LaunchpadWindow();
            Spotlight = new SpotlightWindow();

            Hook(0x0003, 0x0003, (hk, ev, h, obj, child, t, time) =>  // EVENT_SYSTEM_FOREGROUND
            {
                uint pid; Native.GetWindowThreadProcessId(h, out pid);
                if (pid != (uint)System.Diagnostics.Process.GetCurrentProcess().Id && Native.GetAncestor(h, 2) == h) LastForeground = h;
                if (ForegroundChanged != null) ForegroundChanged(h);
                QueueRefresh();
            }, 0);  // include our own windows so the menu bar can show "Finder"
            Native.WinEventProc onWindow = (hk, ev, h, obj, child, t, time) => { if (obj == 0 && child == 0) QueueRefresh(); };
            Hook(0x8001, 0x8003, onWindow);  // destroy / show / hide
            Hook(0x8017, 0x8018, onWindow);  // cloaked / uncloaked (Store apps)
            Hook(0x800C, 0x800C, onWindow);  // title change

            refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            refresh.Tick += delegate { refresh.Stop(); RefreshWindows(); };
            // Window events above keep the list current; the full re-scan is only a safety net.
            int ticks = 0;
            var slow = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            slow.Tick += delegate { if (++ticks % 5 == 0) RefreshWindows(); Taskbar.Enforce(); };
            slow.Start();
            RefreshWindows();
            LastForeground = Native.GetForegroundWindow();
        }

        static void Hook(uint min, uint max, Native.WinEventProc cb, uint flags = 2 /* OUTOFCONTEXT | SKIPOWNPROCESS */)
        {
            hooks.Add(cb);
            Native.SetWinEventHook(min, max, IntPtr.Zero, cb, 0, 0, flags);
        }

        public static void QueueRefresh() { if (refresh != null && !refresh.IsEnabled) refresh.Start(); }

        static void RefreshWindows()
        {
            Wins = Windows.Enumerate();
            if (WindowsChanged != null) WindowsChanged(Wins);
        }

        public static void ToggleSpotlight() { if (Spotlight.IsVisible) Spotlight.Close2(); else Spotlight.Open(); }
        public static void ToggleLaunchpad() { if (Launchpad.IsOpen) Launchpad.Close2(null); else Launchpad.Open(); }

        public static void NewFinder(string path = null)
        {
            var f = new FinderWindow(path ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            f.Show();
            f.Activate();
        }

        public static void ShowInFinder(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            var f = new FinderWindow(Directory.Exists(path) ? path : Path.GetDirectoryName(path), path);
            f.Show();
            f.Activate();
        }

        static SettingsWindow settingsWindow;
        public static void OpenSettings() { OpenSettings(null); }

        public static void OpenSettings(string pane)
        {
            if (settingsWindow == null || !settingsWindow.IsLoaded) settingsWindow = new SettingsWindow(pane);
            else if (pane != null) settingsWindow.Show(pane, true);
            if (settingsWindow.WindowState == WindowState.Minimized) settingsWindow.WindowState = WindowState.Normal;
            settingsWindow.Show();
            settingsWindow.Activate();
        }

        public static void Quit()
        {
            Cleanup();
            Application.Current.Shutdown();
        }

        static void Cleanup()
        {
            if (cleaned) return;
            cleaned = true;
            try
            {
                ColorProfiles.Stop();
                if (Dock != null) Dock.Bar.Remove();
                if (MenuBar != null) MenuBar.Bar.Remove();
                if (Settings.HideTaskbar) Taskbar.Release(); else Taskbar.Restore();
            }
            catch (Exception ex) { Log(ex); }
        }

        public static void Log(object ex)
        {
            try { File.AppendAllText(Settings.LogFile, DateTime.Now + " " + ex + Environment.NewLine); } catch { }
        }
    }
}
