using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace OpenDock
{
    // Mac Setup: one page that sets the rest of Windows up around GlassDock, so the whole PC feels like a Mac --
    // every change a switch, all of them undoable. Before the first change, Windows' own values are written to
    // setup-restore.txt; "Restore Windows Defaults" puts exactly those back. Only GlassDock's own artwork is used
    // (no Apple fonts, icons, sounds or wallpapers).
    static class MacSetup
    {
        public class Step
        {
            public string Key, Title, Detail;
            public bool Default;
            public Func<string> Read;          // Windows' (or GlassDock's) current value, to restore later
            public Action<string> Write;       // put a saved value back
            public Action Apply;               // make the change
        }

        static string RestoreFile { get { return Path.Combine(Settings.DataDir, "setup-restore.txt"); } }
        public static bool CanRestore { get { return File.Exists(RestoreFile); } }

        const string Personalize = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        static int RegInt(string key, string name, int fallback) { try { var v = Registry.GetValue(key, name, fallback); return v is int ? (int)v : fallback; } catch { return fallback; } }
        static void RegSet(string key, string name, int value) { Registry.SetValue(key, name, value, RegistryValueKind.DWord); }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr result);
        static void Broadcast() { Task.Run(() => { IntPtr r; SendMessageTimeout((IntPtr)0xFFFF, 0x1A /* WM_SETTINGCHANGE */, IntPtr.Zero, "ImmersiveColorSet", 2, 500, out r); }); }

        // GlassDock switches saved and restored as "Name=True,Other=False"
        static Func<string> ReadFlags(params string[] names)
        {
            return () => string.Join(",", names.Select(n => n + "=" + typeof(Settings).GetField(n).GetValue(null)));
        }
        static void WriteFlags(string saved)
        {
            foreach (var pair in (saved ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('='); if (kv.Length != 2) continue;
                var f = typeof(Settings).GetField(kv[0]);
                if (f == null) continue;
                try { f.SetValue(null, Convert.ChangeType(kv[1], f.FieldType)); } catch { }
            }
        }
        static void SetFlags(bool on, params string[] names) { foreach (var n in names) typeof(Settings).GetField(n).SetValue(null, on); }

        // ---------- desktop icons: Explorer's own "Show desktop icons" command ----------
        public delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        static IntPtr DesktopView()
        {
            IntPtr view = IntPtr.Zero;
            EnumWindows((h, l) =>
            {
                var v = FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (v != IntPtr.Zero) { view = v; return false; }
                return true;
            }, IntPtr.Zero);
            return view;
        }
        static bool IconsShown()
        {
            var view = DesktopView();
            var list = view == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(view, IntPtr.Zero, "SysListView32", null);
            return list != IntPtr.Zero && IsWindowVisible(list);
        }
        static void ShowIcons(bool show)
        {
            if (IconsShown() == show) return;
            var view = DesktopView();
            if (view != IntPtr.Zero) SendMessage(view, 0x0111 /* WM_COMMAND */, (IntPtr)0x7402 /* toggle desktop icons */, IntPtr.Zero);
        }

        static bool WindowsWallpaper()
        {
            string w = Sys.WallpaperPath();
            return string.IsNullOrEmpty(w) || w.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase);
        }

        // ---------- the Windows accent colour: GlassDock's blue (#0A84FF), on title bars and system controls ----------
        const string AccentKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";
        const string DwmKey = @"Software\Microsoft\Windows\DWM";
        static readonly byte[] AccentRgb = { 0x0A, 0x84, 0xFF };

        static int Abgr(byte r, byte g, byte b) { return unchecked((int)(0xFF000000u | ((uint)b << 16) | ((uint)g << 8) | r)); }
        static int Argb(byte a, byte r, byte g, byte b) { return unchecked((int)(((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b)); }

        // 8 shades of the accent, lightest to darkest, with the base at index 5 -- the palette Windows wants.
        static byte[] AccentPalette()
        {
            double[] toward = { 0.75, 0.55, 0.35, 0.18, 0.09, 0, -0.22, -0.45 };   // + toward white, - toward black
            var bytes = new byte[32];
            for (int i = 0; i < 8; i++)
            {
                double t = toward[i];
                Func<byte, byte> f = c => (byte)(t >= 0 ? c + (255 - c) * t : c * (1 + t));
                bytes[i * 4] = f(AccentRgb[0]); bytes[i * 4 + 1] = f(AccentRgb[1]); bytes[i * 4 + 2] = f(AccentRgb[2]); bytes[i * 4 + 3] = 0;
            }
            return bytes;
        }

        static string DwmOrAccent(string name) { return name == "AccentColor" || name == "ColorizationColor" || name == "ColorPrevalence" ? DwmKey : AccentKey; }
        static readonly string[] AccentValues = { "AccentColorMenu", "StartColorMenu", "AccentPalette", "AccentColor", "ColorizationColor", "ColorPrevalence" };

        static string ReadAccent()
        {
            var parts = new List<string>();
            foreach (var name in AccentValues)
            {
                using (var k = Registry.CurrentUser.OpenSubKey(DwmOrAccent(name)))
                {
                    object v = k == null ? null : k.GetValue(name);
                    string encoded = v is int ? unchecked((uint)(int)v).ToString()
                        : v is byte[] ? "b:" + BitConverter.ToString((byte[])v).Replace("-", "")
                        : "absent";
                    parts.Add(name + "=" + encoded);
                }
            }
            return string.Join(";", parts);
        }

        static void WriteAccent(string saved)
        {
            foreach (var pair in (saved ?? "").Split(';'))
            {
                int eq = pair.IndexOf('='); if (eq < 0) continue;
                string name = pair.Substring(0, eq), val = pair.Substring(eq + 1);
                using (var k = Registry.CurrentUser.CreateSubKey(DwmOrAccent(name)))
                {
                    if (k == null) continue;
                    if (val == "absent") { try { k.DeleteValue(name, false); } catch { } }
                    else if (val.StartsWith("b:"))
                    {
                        string hex = val.Substring(2); var bytes = new byte[hex.Length / 2];
                        for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
                        k.SetValue(name, bytes, RegistryValueKind.Binary);
                    }
                    else { uint u; if (uint.TryParse(val, out u)) k.SetValue(name, unchecked((int)u), RegistryValueKind.DWord); }
                }
            }
            Broadcast();
        }

        static void ApplyAccent()
        {
            byte r = AccentRgb[0], g = AccentRgb[1], b = AccentRgb[2];
            using (var k = Registry.CurrentUser.CreateSubKey(AccentKey))
            {
                k.SetValue("AccentPalette", AccentPalette(), RegistryValueKind.Binary);
                k.SetValue("AccentColorMenu", Abgr(r, g, b), RegistryValueKind.DWord);
                k.SetValue("StartColorMenu", Abgr(r, g, b), RegistryValueKind.DWord);
            }
            using (var k = Registry.CurrentUser.CreateSubKey(DwmKey))
            {
                k.SetValue("AccentColor", Abgr(r, g, b), RegistryValueKind.DWord);
                k.SetValue("ColorizationColor", Argb(0xC4, r, g, b), RegistryValueKind.DWord);
            }
            Broadcast();
        }

        public static List<Step> Steps()
        {
            return new List<Step>
            {
                new Step { Key = "dock", Title = "Dock and menu bar", Detail = "GlassDock's Dock and menu bar take over; Windows' taskbar is hidden.", Default = true,
                    Read = ReadFlags("ShowMenuBar", "HideTaskbar"), Write = WriteFlags, Apply = () => SetFlags(true, "ShowMenuBar", "HideTaskbar") },
                new Step { Key = "windows", Title = "Mac window controls and animations", Detail = "Traffic lights on every window; the Genie effect, and windows that zoom open and fade closed.", Default = true,
                    Read = ReadFlags("TrafficLights", "OpenAnimation", "CloseAnimation", "MinimizeAnimation", "AppGenie"), Write = WriteFlags,
                    Apply = () => { SetFlags(true, "TrafficLights", "OpenAnimation", "CloseAnimation", "MinimizeAnimation", "AppGenie"); Settings.MinimizeEffect = 0; } },
                new Step { Key = "keys", Title = "Mac keyboard shortcuts", Detail = "Spotlight (Alt+Space), the app switcher (Alt+Tab), Alt+` between an app's windows, screenshots (Ctrl+Shift+3/4/5) and window tiling.", Default = true,
                    Read = ReadFlags("Spotlight", "AppSwitcher", "CycleWindowsKey", "ScreenshotKeys", "TilingKeys"), Write = WriteFlags,
                    Apply = () => SetFlags(true, "Spotlight", "AppSwitcher", "CycleWindowsKey", "ScreenshotKeys", "TilingKeys") },
                new Step { Key = "transparency", Title = "Transparency", Detail = "Windows' transparency effects on, so menus and panels are see-through glass.", Default = true,
                    Read = () => RegInt(Personalize, "EnableTransparency", 1).ToString(), Write = v => { RegSet(Personalize, "EnableTransparency", v == "0" ? 0 : 1); Broadcast(); },
                    Apply = () => { RegSet(Personalize, "EnableTransparency", 1); Broadcast(); } },
                new Step { Key = "desktop", Title = "A clean desktop", Detail = "Desktop icons hidden, like a Mac's. Your files stay where they are, in the Desktop folder.", Default = true,
                    Read = () => IconsShown() ? "shown" : "hidden", Write = v => ShowIcons(v == "shown"), Apply = () => ShowIcons(false) },
                new Step { Key = "accent", Title = "Mac-blue accent colour", Detail = "GlassDock's blue becomes the Windows accent, on title bars, checkboxes, selections and any app that follows the system colour.", Default = true,
                    Read = ReadAccent, Write = WriteAccent, Apply = ApplyAccent },
                new Step { Key = "wallpaper", Title = "A GlassDock wallpaper", Detail = "Drift, an original GlassDock render, in light or dark to match.", Default = WindowsWallpaper(),
                    Read = Sys.WallpaperPath, Write = v => { if (!string.IsNullOrEmpty(v) && File.Exists(v)) Sys.SetWallpaper(v); },
                    Apply = () => { bool dark = Theme.Dark; Task.Run(() => { try { Sys.SetWallpaper(WallpaperArt.Make("Drift", dark)); } catch (Exception ex) { App.Log(ex); } }); } },
            };
        }

        // Light or dark everywhere: Windows' apps and system colours, and GlassDock following Windows.
        public static void SetDark(bool dark)
        {
            RegSet(Personalize, "AppsUseLightTheme", dark ? 0 : 1);
            RegSet(Personalize, "SystemUsesLightTheme", dark ? 0 : 1);
            Settings.Appearance = 0; Theme.Refresh(); Broadcast();
        }

        // Applies the chosen steps; the first time, Windows' values are saved so they can be restored.
        public static void Apply(IEnumerable<Step> chosen, bool? dark)
        {
            var steps = Steps();
            if (!CanRestore)
            {
                var lines = steps.Select(s => { string v; try { v = s.Read(); } catch { v = null; } return s.Key + "\t" + (v ?? ""); }).ToList();
                lines.Add("appearance\t" + RegInt(Personalize, "AppsUseLightTheme", 1) + "," + RegInt(Personalize, "SystemUsesLightTheme", 1) + "," + Settings.Appearance);
                Directory.CreateDirectory(Settings.DataDir);
                File.WriteAllLines(RestoreFile, lines, new UTF8Encoding(false));
            }
            else
            {
                // a step this file has no "before" for (added in a later GlassDock): its Windows value is saved now,
                // before it changes, so Restore can put it back too
                var have = new HashSet<string>(File.ReadAllLines(RestoreFile).Select(l => l.Split('\t')[0]));
                var extra = steps.Where(s => !have.Contains(s.Key)).Select(s => { string v; try { v = s.Read(); } catch { v = null; } return s.Key + "\t" + (v ?? ""); }).ToList();
                if (extra.Count > 0) File.AppendAllLines(RestoreFile, extra, new UTF8Encoding(false));
            }
            foreach (var s in chosen) { try { s.Apply(); } catch (Exception ex) { App.Log(ex); } }
            if (dark.HasValue) { try { SetDark(dark.Value); } catch (Exception ex) { App.Log(ex); } }
            Settings.SetupDone = true;
            Settings.Apply();
        }

        // Puts back everything as it was before Mac Setup first ran.
        public static void Restore()
        {
            if (!CanRestore) return;
            var saved = File.ReadAllLines(RestoreFile).Select(l => l.Split(new[] { '\t' }, 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
            foreach (var s in Steps())
            {
                string v;
                if (saved.TryGetValue(s.Key, out v)) { try { s.Write(v); } catch (Exception ex) { App.Log(ex); } }
            }
            string a;
            if (saved.TryGetValue("appearance", out a))
            {
                var p = a.Split(',');
                int apps, sys, mine;
                if (p.Length == 3 && int.TryParse(p[0], out apps) && int.TryParse(p[1], out sys) && int.TryParse(p[2], out mine))
                {
                    RegSet(Personalize, "AppsUseLightTheme", apps); RegSet(Personalize, "SystemUsesLightTheme", sys);
                    Settings.Appearance = mine; Theme.Refresh(); Broadcast();
                }
            }
            File.Move(RestoreFile, RestoreFile + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".used");
            Settings.Apply();
        }

        // The first time GlassDock runs, a notification offers Mac Setup (once).
        public static void Offer()
        {
            if (Settings.SetupDone || Settings.SetupOffered) return;
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            t.Tick += delegate
            {
                t.Stop();
                Settings.SetupOffered = true; Settings.Save();
                Notify.Post(new Note { App = "GlassDock", Title = "Make Windows feel like a Mac", Body = "Set up the Dock, shortcuts, desktop and appearance in one step. Click to choose.", Glyph = "", Open = () => App.OpenSettings("Mac Setup") });
            };
            t.Start();
        }
    }
}
