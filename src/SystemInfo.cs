using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace OpenDock
{
    // Backends for System Settings. Each one reads or changes a Windows setting directly (Win32, WMI, Core Audio,
    // the registry, netsh, powercfg), so the panes never have to send you to the Windows Settings app.
    // Most calls are slow-ish (WMI, child processes), so panes run them off the UI thread.
    static class Sys
    {
        // ---------- plumbing ----------

        public static string Run(string exe, string args, int timeout=10000)
        {
            try
            {
                using(var p = Process.Start(new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage)
                }))
                {
                    var output=p.StandardOutput.ReadToEndAsync();
                    if(!p.WaitForExit(timeout)) { try { p.Kill(); } catch { } return ""; }
                    return output.Wait(1000)?output.Result:"";
                }
            }
            catch { return ""; }
        }

        static List<Dictionary<string, string>> Wmi(string query, string scope = @"root\cimv2")
        {
            var list = new List<Dictionary<string, string>>();
            try
            {
                using (var s = new ManagementObjectSearcher(scope, query))
                    foreach (ManagementObject o in s.Get())
                    {
                        var d = new Dictionary<string, string>();
                        foreach (var p in o.Properties) d[p.Name] = p.Value == null ? "" : p.Value.ToString();
                        list.Add(d);
                    }
            }
            catch { }
            return list;
        }

        static IEnumerable<string> Lines(string s) { return s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries); }

        // "  Name    : value" from netsh / powercfg output
        static string Field(string text, string name)
        {
            foreach (var l in Lines(text))
            {
                int c = l.IndexOf(':');
                if (c > 0 && l.Substring(0, c).Trim() == name) return l.Substring(c + 1).Trim();
            }
            return "";
        }

        static int RegInt(string key, string name, int fallback)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(key)) { var v = k == null ? null : k.GetValue(name); return v is int ? (int)v : fallback; }
        }

        static void RegSet(string key, string name, object value, RegistryValueKind kind = RegistryValueKind.DWord)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(key)) k.SetValue(name, value, kind);
        }

        public static string Size(long bytes)
        {
            double g = bytes / 1e9;   // decimal units, like macOS
            return g >= 1000 ? (g / 1000).ToString("0.##") + " TB" : g.ToString(g >= 100 ? "0" : "0.#") + " GB";
        }

        // ---------- About ----------

        // Title/Subtitle play "MacBook Pro" / "16-inch, 2021"; the Short values fit About This PC's rows.
        public class Info { public string Name, Model, Chip, Memory, Graphics, Serial, Windows, Disk, Title, Subtitle, ChipShort, WindowsShort, Volume; public bool Laptop; }

        static string Maker(string m)
        {
            string[][] names = { new[] { "ASUSTeK", "ASUS" }, new[] { "Micro-Star", "MSI" }, new[] { "Gigabyte", "Gigabyte" }, new[] { "Dell", "Dell" }, new[] { "LENOVO", "Lenovo" },
                                 new[] { "Hewlett", "HP" }, new[] { "HP", "HP" }, new[] { "ASRock", "ASRock" }, new[] { "Acer", "Acer" }, new[] { "Microsoft", "Microsoft" }, new[] { "Razer", "Razer" } };
            foreach (var n in names) if (m.IndexOf(n[0], StringComparison.OrdinalIgnoreCase) >= 0) return n[1];
            return m.Replace(" Inc.", "").Replace(" Corporation", "").Replace(" CO., LTD.", "").Trim();
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
        [DllImport("kernel32.dll")] static extern bool GetPhysicallyInstalledSystemMemory(out ulong kb);

        static bool Junk(string s) { return string.IsNullOrWhiteSpace(s) || s.IndexOf("System", StringComparison.OrdinalIgnoreCase) >= 0 || s.IndexOf("Default", StringComparison.OrdinalIgnoreCase) >= 0 || s.Contains("O.E.M") || s.Contains("To be filled"); }

        public static Info About()
        {
            var i = new Info { Name = Environment.MachineName };
            using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                i.Chip = k == null ? "" : (k.GetValue("ProcessorNameString") ?? "").ToString().Trim();
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            ulong kb;   // installed RAM (what's on the box), not what's left after hardware reservations
            if (GetPhysicallyInstalledSystemMemory(out kb)) i.Memory = kb / 1048576 + " GB";
            else if (GlobalMemoryStatusEx(ref m)) i.Memory = Math.Round(m.ullTotalPhys / 1073741824.0) + " GB";
            // Custom-built PCs report placeholders for the system model and serial; the motherboard has the real ones.
            var cs = Wmi("SELECT Manufacturer, Model FROM Win32_ComputerSystem").FirstOrDefault();
            var board = Wmi("SELECT Manufacturer, Product, SerialNumber FROM Win32_BaseBoard").FirstOrDefault();
            bool oem = cs != null && !Junk(cs["Model"]);
            i.Model = oem ? (cs["Manufacturer"] + " " + cs["Model"]).Trim() : board != null ? (board["Manufacturer"] + " " + board["Product"]).Trim() : "";
            i.Laptop = Wmi("SELECT Name FROM Win32_Battery").Count > 0;
            // A brand-name PC has a real model ("XPS 15 9530"); a self-built one is named after its motherboard.
            i.Title = oem ? cs["Model"] : i.Laptop ? "Laptop" : "Desktop PC";
            i.Subtitle = oem ? Maker(cs["Manufacturer"]) : board != null ? (Maker(board["Manufacturer"]) + " " + board["Product"]).Trim() : "";
            i.ChipShort = System.Text.RegularExpressions.Regex.Replace(i.Chip.Replace("(R)", "").Replace("(TM)", "").Replace(" CPU", ""), @"\s+\d+-Core Processor|\s+Processor|\s*@.*$", "");
            i.ChipShort = System.Text.RegularExpressions.Regex.Replace(i.ChipShort, @"\s{2,}", " ").Trim();
            var bios = Wmi("SELECT SerialNumber FROM Win32_BIOS").FirstOrDefault();
            i.Serial = bios != null && !Junk(bios["SerialNumber"]) ? bios["SerialNumber"] : board != null && !Junk(board["SerialNumber"]) ? board["SerialNumber"] : "Not available";
            i.Graphics = string.Join(", ", Wmi("SELECT Name FROM Win32_VideoController").Select(v => v["Name"]).Where(n => n.Length > 0 && n.IndexOf("Basic", StringComparison.OrdinalIgnoreCase) < 0 && n.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) < 0).Distinct());
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                string product = (k.GetValue("ProductName") ?? "Windows").ToString(), ver = (k.GetValue("DisplayVersion") ?? "").ToString(), build = (k.GetValue("CurrentBuild") ?? "").ToString();
                int b;
                if (int.TryParse(build, out b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");   // the registry still says 10
                i.Windows = (product + " " + ver).Trim() + " (build " + build + ")";
                i.WindowsShort = (product.Replace("Windows ", "") + " " + ver).Trim();
            }
            try
            {
                var c = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory));
                i.Disk = Size(c.AvailableFreeSpace) + " available of " + Size(c.TotalSize);
                i.Volume = c.VolumeLabel.Length > 0 ? c.VolumeLabel : "Local Disk";
            }
            catch { i.Disk = ""; }
            return i;
        }

        public static List<string[]> StartupItems()
        {
            var list = new List<string[]>();
            const string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
                using (var k = hive.OpenSubKey(run))
                    if (k != null) foreach (var n in k.GetValueNames()) list.Add(new[] { n, hive == Registry.CurrentUser ? "This user" : "All users" });
            foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Startup), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup) })
                try { foreach (var f in Directory.GetFiles(dir, "*.lnk")) list.Add(new[] { Path.GetFileNameWithoutExtension(f), "Startup folder" }); } catch { }
            return list.OrderBy(x => x[0]).ToList();
        }

        // ---------- Wi-Fi (netsh) ----------

        public class WifiNet { public string Ssid; public int Signal; public bool Known, Secure; }
        public class WifiState { public bool HasAdapter, Connected; public string Ssid, Signal, Error; public List<WifiNet> Nets = new List<WifiNet>(); }

        public static WifiState Wifi()
        {
            var w = new WifiState();
            string inf = Run("netsh", "wlan show interfaces");
            w.HasAdapter = Field(inf, "Name").Length > 0;
            if (!w.HasAdapter) return w;
            w.Connected = Field(inf, "State").StartsWith("connected", StringComparison.OrdinalIgnoreCase);
            w.Ssid = Field(inf, "SSID"); w.Signal = Field(inf, "Signal");
            var known = new HashSet<string>(Lines(Run("netsh", "wlan show profiles")).Where(l => l.Contains("All User Profile")).Select(l => l.Substring(l.IndexOf(':') + 1).Trim()));
            string nets = Run("netsh", "wlan show networks mode=bssid");
            WifiNet cur = null;
            foreach (var l in Lines(nets))
            {
                var t = l.Trim();
                int c = t.IndexOf(':');
                if (c < 0) continue;
                string key = t.Substring(0, c).Trim(), val = t.Substring(c + 1).Trim();
                if (key.StartsWith("SSID ")) { cur = new WifiNet { Ssid = val, Known = known.Contains(val) }; if (val.Length > 0) w.Nets.Add(cur); }
                else if (cur != null && key == "Authentication") cur.Secure = val != "Open";
                else if (cur != null && key == "Signal") { int s; if (int.TryParse(val.TrimEnd('%'), out s)) cur.Signal = Math.Max(cur.Signal, s); }
            }
            if (w.Nets.Count == 0 && nets.Trim().Length > 0 && !nets.Contains("SSID")) w.Error = Lines(nets).First().Trim();   // e.g. location access is off
            w.Nets = w.Nets.GroupBy(n => n.Ssid).Select(g => g.OrderByDescending(n => n.Signal).First()).OrderByDescending(n => n.Signal).ToList();
            return w;
        }

        // Joins a network; a new one gets a profile first (WPA2-Personal with the password, or open).
        public static void WifiConnect(WifiNet n, string password)
        {
            if (!n.Known)
            {
                string name = SecurityElement.Escape(n.Ssid);
                string security = n.Secure
                    ? "<authEncryption><authentication>WPA2PSK</authentication><encryption>AES</encryption><useOneX>false</useOneX></authEncryption><sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>" + SecurityElement.Escape(password ?? "") + "</keyMaterial></sharedKey>"
                    : "<authEncryption><authentication>open</authentication><encryption>none</encryption><useOneX>false</useOneX></authEncryption>";
                string xml = "<?xml version=\"1.0\"?><WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\"><name>" + name + "</name><SSIDConfig><SSID><name>" + name +
                    "</name></SSID></SSIDConfig><connectionType>ESS</connectionType><connectionMode>auto</connectionMode><MSM><security>" + security + "</security></MSM></WLANProfile>";
                string f = Path.Combine(Path.GetTempPath(), "opendock-wifi.xml");
                File.WriteAllText(f, xml);
                Run("netsh", "wlan add profile filename=\"" + f + "\" user=current");
                try { File.Delete(f); } catch { }
            }
            Run("netsh", "wlan connect name=\"" + n.Ssid.Replace("\"", "") + "\"");
        }

        public static void WifiDisconnect() { Run("netsh", "wlan disconnect"); }

        // ---------- Bluetooth (WMI) ----------

        public class BtState { public bool HasRadio, RadioOk; public List<string> Devices = new List<string>(); }

        public static BtState Bluetooth()
        {
            var b = new BtState();
            foreach (var d in Wmi("SELECT Name, DeviceID, Status, Service FROM Win32_PnPEntity WHERE PNPClass='Bluetooth'"))
            {
                string id = d["DeviceID"];
                if (id.StartsWith(@"BTHENUM\DEV_") || id.StartsWith(@"BTHLE\DEV_")) b.Devices.Add(d["Name"]);
                else if (d["Service"].Equals("BTHUSB", StringComparison.OrdinalIgnoreCase)) { b.HasRadio = true; b.RadioOk |= d["Status"] == "OK"; }
            }
            b.Devices = b.Devices.Where(n => n.Length > 0).Distinct().OrderBy(n => n).ToList();
            return b;
        }

        // ---------- Notifications / Focus (registry, the same values Windows Settings writes) ----------

        const string PushKey = @"Software\Microsoft\Windows\CurrentVersion\PushNotifications", NotifyKey = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";

        public static bool Toasts { get { return RegInt(PushKey, "ToastEnabled", 1) != 0; } set { RegSet(PushKey, "ToastEnabled", value ? 1 : 0); } }

        public static List<string> NotifyApps()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(NotifyKey)) return k == null ? new List<string>() : k.GetSubKeyNames().ToList();
        }

        public static bool NotifyOn(string app) { return RegInt(NotifyKey + "\\" + app, "Enabled", 1) != 0; }
        public static void SetNotify(string app, bool on) { RegSet(NotifyKey + "\\" + app, "Enabled", on ? 1 : 0); }

        // ---------- Sound (Core Audio) ----------

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCo { }
        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IMMDeviceCollection c);
            [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice d);
        }
        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceCollection { [PreserveSig] int GetCount(out int n); [PreserveSig] int Item(int i, out IMMDevice d); }
        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
            [PreserveSig] int OpenPropertyStore(int access, out Native.IPropertyStore ps);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        }
        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioEndpointVolume
        {
            int RegisterControlChangeNotify(IntPtr p); int UnregisterControlChangeNotify(IntPtr p); int GetChannelCount(out int n);
            int SetMasterVolumeLevel(float db, ref Guid ctx);
            [PreserveSig] int SetMasterVolumeLevelScalar(float v, ref Guid ctx);
            int GetMasterVolumeLevel(out float db);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float v);
            int SetChannelVolumeLevel(int ch, float db, ref Guid ctx); int SetChannelVolumeLevelScalar(int ch, float v, ref Guid ctx);
            int GetChannelVolumeLevel(int ch, out float db); int GetChannelVolumeLevelScalar(int ch, out float v);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }
        // Undocumented but stable since Windows 7; it's what every "switch audio device" tool uses.
        [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] class PolicyConfigCo { }
        [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPolicyConfig
        {
            int GetMixFormat(); int GetDeviceFormat(); int ResetDeviceFormat(); int SetDeviceFormat(); int GetProcessingPeriod();
            int SetProcessingPeriod(); int GetShareMode(); int SetShareMode(); int GetPropertyValue(); int SetPropertyValue();
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        }

        public class AudioDev { public string Id, Name; public bool Default; }

        public static List<AudioDev> AudioDevices(bool output)
        {
            var list = new List<AudioDev>();
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
                IMMDevice def; string defId = null;
                if (en.GetDefaultAudioEndpoint(output ? 0 : 1, 1, out def) == 0) def.GetId(out defId);
                IMMDeviceCollection col;
                if (en.EnumAudioEndpoints(output ? 0 : 1, 1 /* active */, out col) != 0) return list;
                int n; col.GetCount(out n);
                for (int i = 0; i < n; i++)
                {
                    IMMDevice d; col.Item(i, out d);
                    string id; d.GetId(out id);
                    list.Add(new AudioDev { Id = id, Name = DeviceName(d), Default = id == defId });
                }
            }
            catch { }
            return list;
        }

        static string DeviceName(IMMDevice d)
        {
            Native.IPropertyStore ps;
            if (d.OpenPropertyStore(0, out ps) != 0) return "Unknown device";
            var key = new Native.PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };   // PKEY_Device_FriendlyName
            Native.PROPVARIANT v;
            if (ps.GetValue(ref key, out v) != 0) return "Unknown device";
            string s = v.vt == 31 ? Marshal.PtrToStringUni(v.p) : "Unknown device";
            Native.PropVariantClear(ref v);
            return s;
        }

        public static void SetDefaultAudio(string id)
        {
            try { var pc = (IPolicyConfig)new PolicyConfigCo(); for (int role = 0; role < 3; role++) pc.SetDefaultEndpoint(id, role); } catch { }
        }

        static IAudioEndpointVolume Volume(bool output)
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
            IMMDevice d;
            if (en.GetDefaultAudioEndpoint(output ? 0 : 1, 1, out d) != 0) return null;
            var iid = typeof(IAudioEndpointVolume).GUID;
            object o;
            return d.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out o) == 0 ? o as IAudioEndpointVolume : null;
        }

        public static float GetVolume(bool output) { try { var v = Volume(output); float f; return v != null && v.GetMasterVolumeLevelScalar(out f) == 0 ? f : -1; } catch { return -1; } }
        public static void SetVolume(bool output, float f) { try { var v = Volume(output); var g = Guid.Empty; if (v != null) v.SetMasterVolumeLevelScalar(Math.Max(0, Math.Min(1, f)), ref g); } catch { } }
        public static bool GetMute(bool output) { try { var v = Volume(output); bool m; return v != null && v.GetMute(out m) == 0 && m; } catch { return false; } }
        public static void SetMute(bool output, bool m) { try { var v = Volume(output); var g = Guid.Empty; if (v != null) v.SetMute(m, ref g); } catch { } }

        // ---------- SystemParametersInfo (accessibility, keyboard, mouse, wallpaper) ----------

        [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint a, uint u, ref uint v, uint f);
        [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint a, uint u, ref bool v, uint f);
        [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint a, uint u, IntPtr v, uint f);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SystemParametersInfo(uint a, uint u, string v, uint f);
        [StructLayout(LayoutKind.Sequential)] struct STICKYKEYS { public uint cbSize, dwFlags; }
        [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint a, uint u, ref STICKYKEYS v, uint f);
        [DllImport("user32.dll")] static extern uint GetDoubleClickTime();
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr h, int m, IntPtr w, string l, int flags, int timeout, out IntPtr result);
        const uint Save = 3;   // SPIF_UPDATEINIFILE | SPIF_SENDCHANGE: persist and tell running apps

        static uint GetU(uint a) { uint v = 0; SystemParametersInfo(a, 0, ref v, 0); return v; }
        static bool GetB(uint a) { bool v = false; SystemParametersInfo(a, 0, ref v, 0); return v; }

        public static bool ReduceMotion { get { return !GetB(0x1042); } set { SystemParametersInfo(0x1043, 0, (IntPtr)(value ? 0 : 1), Save); } }
        public static bool KeyboardCues { get { return GetB(0x100A); } set { SystemParametersInfo(0x100B, 0, (IntPtr)(value ? 1 : 0), Save); } }
        public static bool StickyKeys
        {
            get { var s = new STICKYKEYS { cbSize = 8 }; SystemParametersInfo(0x003A, 8, ref s, 0); return (s.dwFlags & 1) != 0; }
            set { var s = new STICKYKEYS { cbSize = 8 }; SystemParametersInfo(0x003A, 8, ref s, 0); s.dwFlags = value ? s.dwFlags | 1 : s.dwFlags & ~1u; SystemParametersInfo(0x003B, 8, ref s, Save); }
        }

        const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        public static bool ReduceTransparency
        {
            get { return RegInt(Personalize, "EnableTransparency", 1) == 0; }
            set { RegSet(Personalize, "EnableTransparency", value ? 0 : 1); IntPtr r; SendMessageTimeout((IntPtr)0xFFFF, 0x1A, IntPtr.Zero, "ImmersiveColorSet", 2, 1000, out r); }
        }

        public static int KeyRepeat { get { return (int)GetU(0x000A); } set { SystemParametersInfo(0x000B, (uint)value, IntPtr.Zero, Save); } }   // 0 (slow) .. 31 (fast)
        public static int KeyDelay { get { return (int)GetU(0x0016); } set { SystemParametersInfo(0x0017, (uint)value, IntPtr.Zero, Save); } }    // 0 (short) .. 3 (long)
        public static int MouseSpeed { get { return (int)GetU(0x0070); } set { SystemParametersInfo(0x0071, 0, (IntPtr)value, Save); } }         // 1 .. 20
        public static int ScrollLines { get { return (int)GetU(0x0068); } set { SystemParametersInfo(0x0069, (uint)value, IntPtr.Zero, Save); } }
        public static int DoubleClick { get { return (int)GetDoubleClickTime(); } set { SystemParametersInfo(0x0020, (uint)value, IntPtr.Zero, Save); } } // ms
        public static bool SwapButtons { get { return GetSystemMetrics(23) != 0; } set { SystemParametersInfo(0x0021, value ? 1u : 0u, IntPtr.Zero, Save); } }

        public static string WallpaperPath()
        {
            var sb = new StringBuilder(520);
            Native.SystemParametersInfo(0x73, 520, sb, 0);
            return sb.ToString();
        }

        public static void SetWallpaper(string path) { SystemParametersInfo(0x14, 0, path, Save); }

        // ----- the wallpaper library (kept in Settings, edited on the UI thread) -----

        static bool Same(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        public static bool InLibrary(string file) { return Settings.Wallpapers.Any(w => Same(w, file)); }

        // The first time, the library gets the Windows wallpapers plus the one on your desktop; after that it only
        // changes when you add or remove pictures.
        public static List<string> Library()
        {
            if (!Settings.WallpapersSeeded)
            {
                string[] exts = { ".jpg", ".jpeg", ".png", ".bmp" };
                try
                {
                    string windows = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Web\Wallpaper");
                    foreach (var f in Directory.EnumerateFiles(windows, "*.*", SearchOption.AllDirectories).Where(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant())))
                        if (!InLibrary(f)) Settings.Wallpapers.Add(f);
                }
                catch { }
                Settings.WallpapersSeeded = true;
                string cur = WallpaperPath();
                if (File.Exists(cur) && !InLibrary(cur)) AddToLibrary(cur);
                Settings.Save();
            }
            return Settings.Wallpapers.Where(File.Exists).ToList();
        }

        // Match actual contents, including copies that were renamed to avoid a filename collision.
        public static string CopyOf(string file)
        {
            try
            {
                var copy = Path.Combine(Settings.WallpaperDir, Path.GetFileName(file));
                if(!File.Exists(file)) return null;
                foreach(var candidate in new[]{copy}.Concat(Settings.Wallpapers.Where(p=>Within(p,Settings.WallpaperDir))).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if(!File.Exists(candidate)||new FileInfo(candidate).Length!=new FileInfo(file).Length) continue;
                    if(Same(Path.GetFullPath(candidate),Path.GetFullPath(file))) return candidate;
                    using(var hash=System.Security.Cryptography.SHA256.Create())
                    using(var original=File.OpenRead(file))
                    using(var owned=File.OpenRead(candidate))
                        if(hash.ComputeHash(original).SequenceEqual(hash.ComputeHash(owned))) return candidate;
                }
                return null;
            }
            catch { return null; }
        }
        internal static bool Within(string file,string directory)
        {
            return Path.GetFullPath(file).StartsWith(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
        }

        // Keeps a copy in OpenDock's own folder, so the picture stays even if you move or delete the original.
        public static string AddToLibrary(string file)
        {
            string dest = CopyOf(file) ?? file;
            if (!Within(dest,Environment.GetFolderPath(Environment.SpecialFolder.Windows)) && !Within(dest,Settings.WallpaperDir))
            {
                Directory.CreateDirectory(Settings.WallpaperDir);
                string name = Path.GetFileNameWithoutExtension(file), ext = Path.GetExtension(file);
                dest = Path.Combine(Settings.WallpaperDir, name + ext);
                for (int n = 2; File.Exists(dest); n++) dest = Path.Combine(Settings.WallpaperDir, name + " " + n + ext);
                File.Copy(file, dest);
            }
            Settings.Wallpapers.RemoveAll(w => Same(w, dest));
            Settings.Wallpapers.Insert(0, dest);
            Settings.Save();
            return dest;
        }

        public static void RemoveFromLibrary(string file)
        {
            Settings.Wallpapers.RemoveAll(w => Same(w, file));
            Settings.Save();
            // Our own copy goes too, unless it's still on the desktop.
            if (Same(Path.GetDirectoryName(Path.GetFullPath(file)),Path.GetFullPath(Settings.WallpaperDir)) && !Same(file, WallpaperPath()))
                try { File.Delete(file); } catch { }
        }

        // ---------- Displays (DDC/CI for brightness) ----------

        public class Display { public IntPtr Monitor; public string Device; public int Width, Height, Hz, Brightness = -1; public bool Primary; public Native.RECT Bounds, Work; }

        delegate bool MonitorProc(IntPtr m, IntPtr dc, IntPtr rect, IntPtr data);
        [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorProc cb, IntPtr data);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct MONITORINFOEX { public int cbSize; public Native.RECT rcMonitor, rcWork; public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFOEX mi);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency, dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct PHYSICAL_MONITOR { public IntPtr Handle; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description; }
        [DllImport("dxva2.dll")] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr m, out uint n);
        [DllImport("dxva2.dll")] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr m, uint n, [Out] PHYSICAL_MONITOR[] a);
        [DllImport("dxva2.dll")] static extern bool DestroyPhysicalMonitors(uint n, PHYSICAL_MONITOR[] a);
        [DllImport("dxva2.dll")] static extern bool GetMonitorBrightness(IntPtr h, out uint min, out uint cur, out uint max);
        [DllImport("dxva2.dll")] static extern bool SetMonitorBrightness(IntPtr h, uint v);

        public static List<Display> Screens()
        {
            var list = new List<Display>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (m, dc, r, data) =>
            {
                var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf(typeof(MONITORINFOEX)) };
                if (!GetMonitorInfo(m, ref mi)) return true;
                var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)) };
                EnumDisplaySettings(mi.szDevice, -1 /* current */, ref dm);
                var d = new Display { Monitor = m, Device = mi.szDevice, Width = dm.dmPelsWidth, Height = dm.dmPelsHeight, Hz = dm.dmDisplayFrequency, Primary = (mi.dwFlags & 1) != 0, Bounds = mi.rcMonitor, Work = mi.rcWork };
                list.Add(d);
                return true;
            }, IntPtr.Zero);
            return list.OrderByDescending(d => d.Primary).ToList();
        }

        public static List<Display> Displays()
        {
            var list = Screens();
            foreach (var d in list)
                Physical(d.Monitor, h => { uint min, cur, max; if (GetMonitorBrightness(h, out min, out cur, out max) && max > min) d.Brightness = (int)((cur - min) * 100 / (max - min)); });
            return list;
        }

        public static void SetBrightness(IntPtr monitor, int percent)
        {
            Physical(monitor, h => { uint min, cur, max; if (GetMonitorBrightness(h, out min, out cur, out max)) SetMonitorBrightness(h, (uint)(min + (max - min) * percent / 100)); });
        }

        static void Physical(IntPtr monitor, Action<IntPtr> use)
        {
            uint n;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out n) || n == 0) return;
            var a = new PHYSICAL_MONITOR[n];
            if (!GetPhysicalMonitorsFromHMONITOR(monitor, n, a)) return;
            try { use(a[0].Handle); } finally { DestroyPhysicalMonitors(n, a); }
        }

        // ---------- Power (powercfg) ----------

        public class Plan { public string Guid, Name; public bool Active; }

        public static List<Plan> Plans()
        {
            var list = new List<Plan>();
            foreach (var l in Lines(Run("powercfg", "/list")))
            {
                int g = l.IndexOf("GUID:");
                if (g < 0) continue;
                string rest = l.Substring(g + 5).Trim();
                int open = rest.IndexOf('('), close = rest.LastIndexOf(')');
                if (open < 0 || close < open) continue;
                list.Add(new Plan { Guid = rest.Substring(0, open).Trim(), Name = rest.Substring(open + 1, close - open - 1), Active = rest.EndsWith("*") });
            }
            return list;
        }

        public static void SetPlan(string guid) { Run("powercfg", "/setactive " + guid); }

        // Minutes (0 = never) for "VIDEOIDLE" (display off) or "STANDBYIDLE" (sleep) on the current plan.
        public static int Timeout(string sub, string setting)
        {
            string v = Field(Run("powercfg", "/query SCHEME_CURRENT " + sub + " " + setting), "Current AC Power Setting Index");
            try { return v.Length == 0 ? -1 : Convert.ToInt32(v, 16) / 60; } catch { return -1; }
        }

        public static void SetTimeout(string what, int minutes)   // what: "monitor" or "standby"
        {
            Run("powercfg", "/change " + what + "-timeout-ac " + minutes);
            Run("powercfg", "/change " + what + "-timeout-dc " + minutes);
        }

        // ---------- Privacy & Security ----------

        const string Consent = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";

        public static bool Allowed(string capability)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Consent + capability)) return k == null || !"Deny".Equals(k.GetValue("Value"));
        }

        public static void SetAllowed(string capability, bool on) { RegSet(Consent + capability, "Value", on ? "Allow" : "Deny", RegistryValueKind.String); }

        public static List<string[]> Antivirus()
        {
            return Wmi("SELECT displayName, productState FROM AntiVirusProduct", @"root\SecurityCenter2").Select(d =>
            {
                int st; int.TryParse(d["productState"], out st);
                return new[] { d["displayName"], ((st >> 12) & 0xF) == 1 ? "On" : "Off" };
            }).ToList();
        }

        // ---------- Users ----------

        public static List<string[]> Users()
        {
            var admins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var group = Wmi("SELECT Domain, Name FROM Win32_Group WHERE LocalAccount=TRUE AND SID='S-1-5-32-544'").FirstOrDefault();
            if (group != null)
                foreach (var u in Wmi("ASSOCIATORS OF {Win32_Group.Domain='" + group["Domain"] + "',Name='" + group["Name"] + "'} WHERE AssocClass=Win32_GroupUser Role=GroupComponent ResultClass=Win32_UserAccount"))
                    admins.Add(u["Name"]);
            return Wmi("SELECT Name, FullName, Disabled FROM Win32_UserAccount WHERE LocalAccount=TRUE")
                .Where(u => u["Disabled"] != "True")
                .Select(u => new[] { u["FullName"].Length > 0 ? u["FullName"] : u["Name"], admins.Contains(u["Name"]) ? "Admin" : "Standard", u["Name"] })
                .ToList();
        }

        // ---------- Printers ----------

        public static List<string[]> Printers()
        {
            return Wmi("SELECT Name, Default, WorkOffline FROM Win32_Printer").Select(p => new[] { p["Name"], p["Default"], p["WorkOffline"] }).OrderBy(p => p[0]).ToList();
        }

        public static void SetDefaultPrinter(string name)
        {
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT * FROM Win32_Printer WHERE Name='" + name.Replace("\\", "\\\\").Replace("'", "\\'") + "'"))
                    foreach (ManagementObject p in s.Get()) p.InvokeMethod("SetDefaultPrinter", null);
            }
            catch { }
        }
    }
}
