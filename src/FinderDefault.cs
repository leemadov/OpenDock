using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace OpenDock
{
    // Finder as the PC's file manager (Settings > Desktop & Dock, off until you turn it on): opening a folder or a
    // drive anywhere -- double-clicking one on the desktop, "Open folder" in an app, a shortcut -- and Win+E open
    // GlassDock's Finder instead of File Explorer.
    //
    // It's your account's own setting (HKCU\Software\Classes, nothing machine-wide): a "GlassDock" verb on folders and
    // drives, made their default. Windows' special places (This PC, Control Panel, the Recycle Bin) aren't folders and
    // still open in Explorer, and Explorer itself is untouched -- "Show in Explorer" still works. Turning it off removes
    // exactly what was added.
    static class FinderDefault
    {
        const string Verb = "GlassDock";
        static readonly string[] Kinds = { "Directory", "Drive" };

        public static string Exe { get { return Assembly.GetExecutingAssembly().Location; } }

        public static bool IsOn
        {
            get
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Directory\shell"))
                        return k != null && Verb.Equals(k.GetValue("") as string, StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            }
        }

        public static void Set(bool on)
        {
            foreach (var kind in Kinds)
            {
                string shell = @"Software\Classes\" + kind + @"\shell";
                if (on)
                {
                    using (var verb = Registry.CurrentUser.CreateSubKey(shell + @"\" + Verb))
                    {
                        verb.SetValue("", "Open in Finder");
                        verb.SetValue("Icon", "\"" + Exe + "\"");
                    }
                    using (var cmd = Registry.CurrentUser.CreateSubKey(shell + @"\" + Verb + @"\command"))
                        cmd.SetValue("", "\"" + Exe + "\" --open \"%1\"");
                    using (var k = Registry.CurrentUser.CreateSubKey(shell)) k.SetValue("", Verb);
                }
                else
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(shell, true))
                    {
                        if (k == null) continue;
                        if (Verb.Equals(k.GetValue("") as string, StringComparison.OrdinalIgnoreCase)) k.DeleteValue("", false);
                        try { k.DeleteSubKeyTree(Verb, false); } catch { }
                    }
                }
            }
            Settings.FinderDefault = on;
            Settings.Save();
            Hook(on);
            SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);
        }

        // At startup: the verb points at this GlassDock.exe (it may have moved), and Win+E is watched if it's on.
        public static void Start()
        {
            if (IsOn && !Settings.FinderDefault) { Settings.FinderDefault = true; Settings.Save(); }   // the registry is what counts
            if (!Settings.FinderDefault) return;
            try
            {
                using (var cmd = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Directory\shell\" + Verb + @"\command"))
                {
                    string want = "\"" + Exe + "\" --open \"%1\"";
                    if (cmd == null || !want.Equals(cmd.GetValue("") as string, StringComparison.OrdinalIgnoreCase)) Set(true);
                    else Hook(true);
                }
            }
            catch (Exception ex) { App.Log(ex); }
        }

        // ---------- Win+E ----------
        delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("shell32.dll")] static extern void SHChangeNotify(int e, uint flags, IntPtr a, IntPtr b);
        static IntPtr hook;
        static HookProc proc;

        static void Hook(bool on)
        {
            if (on && hook == IntPtr.Zero)
            {
                proc = (code, w, l) =>
                {
                    try
                    {
                        if (code >= 0 && Marshal.ReadInt32(l) == 0x45 /* E */ && ((GetAsyncKeyState(0x5B) | GetAsyncKeyState(0x5C)) & 0x8000) != 0
                            && (Marshal.ReadInt32(l, 8) & 0x10 /* injected */) == 0 && !LockScreen.Active)
                        {
                            if (w == (IntPtr)0x100 || w == (IntPtr)0x104)
                            {
                                // a harmless key in between, so letting go of the Windows key doesn't open Start
                                keybd_event(0xFF, 0, 0, UIntPtr.Zero); keybd_event(0xFF, 0, 2, UIntPtr.Zero);
                                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => App.NewFinder()));
                            }
                            return (IntPtr)1;   // Explorer never sees Win+E
                        }
                    }
                    catch { }
                    return CallNextHookEx(hook, code, w, l);
                };
                hook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, proc, GetModuleHandle(null), 0);
            }
            else if (!on && hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        }

        // ---------- "GlassDock.exe --open <folder>" ----------
        [StructLayout(LayoutKind.Sequential)] struct COPYDATASTRUCT { public IntPtr dwData; public int cbData; public IntPtr lpData; }
        [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, uint m, IntPtr w, ref COPYDATASTRUCT l, uint flags, uint timeout, out IntPtr result);
        public const int OpenTag = 0x47444F50;   // "GDOP"

        // The folder after --open in a command line, or null.
        public static string Requested(string[] args)
        {
            int i = Array.IndexOf(args, "--open");
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        // A second GlassDock.exe hands the folder to the running one. If there's no running one to take it, or it doesn't
        // answer within 2 s (busy, restarting, hung), the folder opens in File Explorer instead -- a double-click on a
        // folder must never just do nothing.
        public static void Forward(IntPtr host, string path)
        {
            path = Clean(path);
            if (host == IntPtr.Zero || !Send(host, path)) Explorer(path);
        }

        static bool Send(IntPtr host, string path)
        {
            var bytes = System.Text.Encoding.Unicode.GetBytes(path + "\0");
            var mem = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, mem, bytes.Length);
                var cds = new COPYDATASTRUCT { dwData = (IntPtr)OpenTag, cbData = bytes.Length, lpData = mem };
                IntPtr reply;
                return SendMessageTimeout(host, 0x004A /* WM_COPYDATA */, IntPtr.Zero, ref cds, 2 /* ABORTIFHUNG */, 2000, out reply) != IntPtr.Zero && reply != IntPtr.Zero;
            }
            catch { return false; }
            finally { Marshal.FreeHGlobal(mem); }
        }

        // Explorer itself, past the "GlassDock" verb (/e, explores; it doesn't use the folder's default verb)
        static void Explorer(string path)
        {
            try { System.Diagnostics.Process.Start("explorer.exe", Directory(path) ? "/e,\"" + path + "\"" : "\"" + path + "\""); } catch { }
        }

        static bool Directory(string p) { try { return System.IO.Directory.Exists(p); } catch { return false; } }

        // A folder as Windows hands it over: "C:\" arrives as C:" (the backslash escapes the quote), so the quote goes
        // and a bare drive gets its backslash back.
        static string Clean(string path)
        {
            if (path == null) return null;
            path = path.Trim().Trim('"');
            if (path.Length == 2 && path[1] == ':') path += "\\";
            return path;
        }

        // The running one receives it.
        public static string Received(IntPtr l)
        {
            var cds = (COPYDATASTRUCT)Marshal.PtrToStructure(l, typeof(COPYDATASTRUCT));
            if (cds.dwData != (IntPtr)OpenTag || cds.cbData <= 0 || cds.cbData > 65536) return null;
            return Marshal.PtrToStringUni(cds.lpData).TrimEnd('\0');
        }

        // Opens a folder (or a drive) in Finder; anything else goes to Explorer as before.
        public static void Open(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) { App.NewFinder(); return; }
                path = Clean(path);
                if (Directory(path)) App.NewFinder(path);
                else Explorer(path);
            }
            catch (Exception ex) { App.Log(ex); }
        }
    }
}
