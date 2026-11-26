using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace OpenDock
{
    // GlassDock's glass hides its own windows from screen capture so it can photograph what's behind them without
    // photographing itself. That also hid the Dock, the traffic lights and the panels from everyone else's capture:
    // screenshots, Remote Desktop, screen sharing. This brings them back whenever someone else is capturing --
    // the moment a screenshot shortcut is pressed, while the Snipping Tool is open, during a Remote Desktop session,
    // or always (Settings) for other remote-control and sharing apps. Meanwhile the glass keeps its last picture.
    static class CaptureGuard
    {
        delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint thread);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);

        static IntPtr hook;
        static HookProc proc;   // kept alive: Windows calls it
        static DispatcherTimer poll;
        static DateTime keyUntil = DateTime.MinValue;   // a screenshot shortcut was just pressed: shown until then
        static readonly string[] Snippers = { "ScreenClippingHost", "SnippingTool", "ScreenSketch" };

        public static void Start()
        {
            proc = (code, w, l) =>
            {
                try
                {
                    // key down only (WM_KEYDOWN / WM_SYSKEYDOWN)
                    if (code >= 0 && (w == (IntPtr)0x100 || w == (IntPtr)0x104))
                    {
                        int vk = Marshal.ReadInt32(l);
                        bool win = Down(0x5B) || Down(0x5C), shift = Down(0x10);
                        // Print Screen (alone, with Alt or Win), Win+Shift+S (Snipping Tool), Win+Shift+R (screen recording)
                        if (vk == 0x2C || (win && shift && (vk == 0x53 || vk == 0x52))) { keyUntil = DateTime.UtcNow.AddSeconds(8); Glass.Lift(true); }
                    }
                }
                catch { }
                return CallNextHookEx(hook, code, w, l);
            };
            hook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, proc, GetModuleHandle(null), 0);
            // (the shortcuts are caught as they're pressed; this only notices a Snipping Tool window, a remote
            // session or a restarted window manager, so every 4 s is plenty)
            poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            poll.Tick += delegate { Update(); WatchDwm(); };
            poll.Start();
            Settings.Changed += Update;
            Update();
        }

        static bool Down(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }

        // Windows' window manager (dwm.exe, which draws every window, the wallpaper and all the glass) restarts after
        // it crashes -- typically when the PC runs out of memory under a heavy task. GlassDock's windows lose their
        // drawing surfaces with it and stay broken, so GlassDock starts itself fresh once the new one has settled.
        static int dwmPid;
        static DateTime dwmChanged = DateTime.MinValue;
        static void WatchDwm()
        {
            try
            {
                var p = Process.GetProcessesByName("dwm").FirstOrDefault();
                int pid = p != null ? p.Id : 0;
                if (p != null) p.Dispose();
                if (pid == 0) return;
                if (dwmPid == 0) { dwmPid = pid; return; }
                if (pid != dwmPid) { dwmPid = pid; dwmChanged = DateTime.UtcNow; return; }
                if (dwmChanged != DateTime.MinValue && (DateTime.UtcNow - dwmChanged).TotalSeconds > 4)
                {
                    dwmChanged = DateTime.MinValue;
                    App.Log(new Exception("dwm.exe restarted: starting GlassDock fresh"));
                    App.Restart();
                }
            }
            catch (Exception ex) { App.Log(ex); }
        }

        // Shown in captures while any reason holds; hidden again (live glass) once none does.
        static void Update()
        {
            try
            {
                bool always = Settings.ShowInCaptures;
                bool remote = GetSystemMetrics(0x1000 /* SM_REMOTESESSION */) != 0;
                bool key = DateTime.UtcNow < keyUntil, snipping = Snipping();
                Glass.Lift(always || remote || key || snipping);
            }
            catch (Exception ex) { App.Log(ex); }
        }

        static bool Snipping()
        {
            bool open = false;
            foreach (var p in Process.GetProcesses())
            {
                try { if (!open && Snippers.Contains(p.ProcessName) && p.MainWindowHandle != IntPtr.Zero) open = true; } catch { }
                p.Dispose();
            }
            return open;
        }
    }
}
