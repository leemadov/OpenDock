using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace OpenDock
{
    // Icons come from the shell (IShellItemImageFactory) on a background STA thread so the UI never stutters.
    static class Icons
    {
        static readonly Dictionary<string, ImageSource> cache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, List<Action<ImageSource>>> pending = new Dictionary<string, List<Action<ImageSource>>>();
        static readonly BlockingCollection<Action> queue = new BlockingCollection<Action>();
        public static Dispatcher Ui;

        public static void Start()
        {
            Ui = Dispatcher.CurrentDispatcher;
            var t = new Thread(() => { foreach (var a in queue.GetConsumingEnumerable()) { try { a(); } catch (Exception ex) { Debug.WriteLine(ex); } } });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
        }

        // Runs work on the shell (STA) thread.
        public static void Run(Action a) { queue.Add(a); }

        // tile = app icon for the dock/launchpad (uniform rounded tile + soft shadow).
        // key lets callers share one cached icon between many files (e.g. every .txt).
        public static void Get(string path, bool tile, Action<ImageSource> done, int size = 256, bool keep = true, string key = null)
        {
            if (string.IsNullOrEmpty(path)) return;
            key = (key ?? path) + "|" + size + "|" + (tile ? (Settings.IconTiles ? (Theme.Dark ? "d" : "l") : "s") : "");
            ImageSource img;
            if (cache.TryGetValue(key, out img)) { done(img); return; }
            List<Action<ImageSource>> waiting;
            if (pending.TryGetValue(key, out waiting)) { waiting.Add(done); return; }
            pending[key] = new List<Action<ImageSource>> { done };
            bool tiles = Settings.IconTiles, dark = Theme.Dark;
            queue.Add(() =>
            {
                BitmapSource raw = Load(path, size);
                ImageSource result = raw != null && tile ? Compose(raw, tiles, dark, size) : raw;
                Ui.BeginInvoke(new Action(() =>
                {
                    if (keep && result != null) cache[key] = result;
                    var list = pending[key]; pending.Remove(key);
                    if (result != null) foreach (var cb in list) cb(result);
                }));
            });
        }

        public static void ClearCache() { cache.Clear(); }

        static BitmapSource Load(string path, int size)
        {
            // Store apps: the shell hands back a small logo that blurs when scaled up; their package has a crisp 256px one.
            const string apps = "shell:AppsFolder\\";
            if (path.StartsWith(apps, StringComparison.OrdinalIgnoreCase))
            {
                string png = Apps.PackageLogo(path.Substring(apps.Length));
                if (png != null)
                {
                    try
                    {
                        var bi = new BitmapImage();
                        bi.BeginInit();
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        bi.UriSource = new Uri(png);
                        bi.EndInit();
                        bi.Freeze();
                        return bi;
                    }
                    catch { }
                }
            }
            Native.IShellItemImageFactory f;
            try { Native.SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(Native.IShellItemImageFactory).GUID, out f); }
            catch { return null; }
            try
            {
                IntPtr hbm;
                if (f.GetImage(new Native.SIZE(size, size), 0, out hbm) != 0) return null;
                try { return FromHBitmap(hbm); } finally { Native.DeleteObject(hbm); }
            }
            finally { Marshal.ReleaseComObject(f); }
        }

        static BitmapSource FromHBitmap(IntPtr hbm)
        {
            Native.BITMAP b;
            Native.GetObject(hbm, Marshal.SizeOf(typeof(Native.BITMAP)), out b);
            int w = b.bmWidth, h = Math.Abs(b.bmHeight);
            var bi = new Native.BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var px = new byte[w * h * 4];
            IntPtr dc = Native.GetDC(IntPtr.Zero);
            Native.GetDIBits(dc, hbm, 0, (uint)h, px, ref bi, 0);
            Native.ReleaseDC(IntPtr.Zero, dc);
            // Shell bitmaps are usually premultiplied, thumbnails sometimes have no alpha at all: detect instead of guessing.
            bool anyAlpha = false, straight = false;
            for (int i = 0; i < px.Length; i += 4)
            {
                byte a = px[i + 3];
                if (a != 0) anyAlpha = true;
                if (px[i] > a || px[i + 1] > a || px[i + 2] > a) straight = true;
            }
            if (!anyAlpha) for (int i = 3; i < px.Length; i += 4) px[i] = 255;
            var bs = BitmapSource.Create(w, h, 96, 96, !anyAlpha || straight ? PixelFormats.Bgra32 : PixelFormats.Pbgra32, null, px, w * 4);
            bs.Freeze();
            return bs;
        }

        // tile: already a full-bleed squircle (opaque edges and corners). lum: average brightness of the artwork.
        static void Analyze(BitmapSource src, out bool tile, out double lum)
        {
            var bmp = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int w = bmp.PixelWidth, h = bmp.PixelHeight;
            var px = new byte[w * h * 4];
            bmp.CopyPixels(px, w * 4, 0);
            Func<double, double, byte> A = (x, y) => px[((int)(y * (h - 1)) * w + (int)(x * (w - 1))) * 4 + 3];
            tile = A(0.5, 0.07) > 200 && A(0.5, 0.93) > 200 && A(0.07, 0.5) > 200 && A(0.93, 0.5) > 200
                && A(0.13, 0.13) > 200 && A(0.87, 0.13) > 200 && A(0.13, 0.87) > 200 && A(0.87, 0.87) > 200;
            double sum = 0, weight = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                double a = px[i + 3] / 255.0;
                sum += a * (0.0722 * px[i] + 0.7152 * px[i + 1] + 0.2126 * px[i + 2]) / 255.0;
                weight += a;
            }
            lum = weight > 0 ? sum / weight : 0.5;
        }

        // Some apps only ship a 32px icon, which the shell centres in a big empty canvas with a faint 1px frame
        // around the edge. Crop to the artwork (ignoring the frame) so it can fill the tile.
        static BitmapSource Trim(BitmapSource src)
        {
            var bmp = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int w = bmp.PixelWidth, h = bmp.PixelHeight, x0 = w, y0 = h, x1 = -1, y1 = -1, m = w * 3 / 100;
            var px = new byte[w * h * 4];
            bmp.CopyPixels(px, w * 4, 0);
            for (int y = m; y < h - m; y++)
                for (int x = m; x < w - m; x++)
                    if (px[(y * w + x) * 4 + 3] > 16) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
            int size = Math.Max(x1 - x0, y1 - y0) + 1;
            if (x1 < 0 || size > w * 0.9) return src;   // already fills its square (or a full-bleed tile)
            size = Math.Min(w, (int)(size * 1.04) + 2);
            int cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
            int left = Math.Max(0, Math.Min(w - size, cx - size / 2)), top = Math.Max(0, Math.Min(h - size, cy - size / 2));
            var crop = new CroppedBitmap(src, new Int32Rect(left, top, size, size));
            crop.Freeze();
            return crop;
        }

        static BitmapSource Compose(BitmapSource icon, bool tiles, bool dark, int S)
        {
            icon = Trim(icon);
            double T = S * 0.875, k = S / 256.0;
            FrameworkElement content;
            bool tile; double lum;
            Analyze(icon, out tile, out lum);
            if (tiles && !tile)
            {
                // keep the glyph readable: white logos get a dark plate, black logos a light one
                if (dark ? lum < 0.22 : lum > 0.72) dark = !dark;
                var plate = new LinearGradientBrush(
                    dark ? Color.FromRgb(0x48, 0x48, 0x4C) : Colors.White,
                    dark ? Color.FromRgb(0x24, 0x24, 0x27) : Color.FromRgb(0xE6, 0xE6, 0xEA), 90);
                var img = new Image { Source = icon, Width = T * 0.68, Height = T * 0.68 };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                content = new Border
                {
                    Width = T, Height = T, CornerRadius = new CornerRadius(T * 0.225), Background = plate,
                    BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(30, 0, 0, 0)),
                    BorderThickness = new Thickness(1.5 * k), Child = img
                };
            }
            else
            {
                // full-bleed artwork gets the same squircle corners as everything else
                content = new Image { Source = icon, Width = T, Height = T, Clip = tile && tiles ? new RectangleGeometry(new Rect(0, 0, T, T), T * 0.225, T * 0.225) : null };
                RenderOptions.SetBitmapScalingMode(content, BitmapScalingMode.HighQuality);
            }
            content.Effect = new DropShadowEffect { BlurRadius = 12 * k, ShadowDepth = 4 * k, Direction = 270, Opacity = 0.3 };
            var g = new Grid { Width = S, Height = S };
            g.Children.Add(content);
            return Render(g, S, S);
        }

        public static BitmapSource Render(FrameworkElement e, int w, int h)
        {
            e.Measure(new Size(w, h));
            e.Arrange(new Rect(0, 0, w, h));
            e.UpdateLayout();
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(e);
            rtb.Freeze();
            return rtb;
        }

        // Gaussian blur rendered once into a bitmap (used for Launchpad, Spotlight, the dock backdrop).
        public static BitmapSource Blur(ImageSource src, int w, int h, double radius)
        {
            var img = new Image { Source = src, Stretch = Stretch.Fill, Width = w + radius * 4, Height = h + radius * 4, Effect = new BlurEffect { Radius = radius } };
            var c = new Canvas { Width = w, Height = h, ClipToBounds = true };
            Canvas.SetLeft(img, -radius * 2); Canvas.SetTop(img, -radius * 2);
            c.Children.Add(img);
            return Render(c, w, h);
        }

        // In-place blur of BGRX pixels: repeated horizontal + vertical running-sum passes, edges clamped.
        public static void BoxBlur(byte[] px, int w, int h, int r, int passes)
        {
            var tmp = new byte[px.Length];
            for (int p = 0; p < passes; p++) { BoxPass(px, tmp, w, h, r, true); BoxPass(tmp, px, w, h, r, false); }
        }

        static void BoxPass(byte[] src, byte[] dst, int w, int h, int r, bool horiz)
        {
            int n = horiz ? w : h, lines = horiz ? h : w, step = horiz ? 4 : w * 4, div = 2 * r + 1;
            for (int l = 0; l < lines; l++)
            {
                int start = horiz ? l * w * 4 : l * 4;
                for (int c = 0; c < 3; c++)
                {
                    int sum = 0;
                    for (int k = -r; k <= r; k++) sum += src[start + Math.Max(0, Math.Min(n - 1, k)) * step + c];
                    for (int i = 0; i < n; i++)
                    {
                        dst[start + i * step + c] = (byte)(sum / div);
                        sum += src[start + Math.Min(i + r + 1, n - 1) * step + c] - src[start + Math.Max(i - r, 0) * step + c];
                    }
                }
            }
        }

        // A screen area shrunk to dw x dh as BGRX pixels (GDI does the downscale, so it's cheap).
        public static byte[] CaptureScaled(int x, int y, int w, int h, int dw, int dh)
        {
            IntPtr screen = Native.GetDC(IntPtr.Zero), mem = Native.CreateCompatibleDC(screen), bmp = Native.CreateCompatibleBitmap(screen, dw, dh);
            try
            {
                IntPtr old = Native.SelectObject(mem, bmp);
                Native.SetStretchBltMode(mem, 4 /* HALFTONE */);
                bool ok = Native.StretchBlt(mem, 0, 0, dw, dh, screen, x, y, w, h, 0x00CC0020 /* SRCCOPY */);
                Native.SelectObject(mem, old);
                if (!ok) return null;
                var bi = new Native.BITMAPINFOHEADER { biSize = 40, biWidth = dw, biHeight = -dh, biPlanes = 1, biBitCount = 32 };
                var px = new byte[dw * dh * 4];
                return Native.GetDIBits(screen, bmp, 0, (uint)dh, px, ref bi, 0) == dh ? px : null;
            }
            finally { Native.DeleteObject(bmp); Native.DeleteDC(mem); Native.ReleaseDC(IntPtr.Zero, screen); }
        }

        public static BitmapSource CaptureScreen(int x, int y, int w, int h)
        {
            using (var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb))
            {
                using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h));
                IntPtr hb = bmp.GetHbitmap();
                try
                {
                    var s = Imaging.CreateBitmapSourceFromHBitmap(hb, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    s.Freeze();
                    return s;
                }
                finally { Native.DeleteObject(hb); }
            }
        }
    }

    // Every launchable app Windows knows about (Win32 + Store), straight from shell:AppsFolder.
    class AppEntry
    {
        public string Name, Id, Exe, Package;   // Package: install folder of Store / MSIX apps
        public string Icon { get { return "shell:AppsFolder\\" + Id; } }
        public string Target { get { return "app:" + Id; } }
    }

    static class Apps
    {
        public static List<AppEntry> All = new List<AppEntry>();
        public static event Action Changed;
        static DateTime loadedAt = DateTime.MinValue;
        static readonly string[] junk = { "uninstall", "readme", "release notes", "documentation", "license", "website", "help", "manual", "changelog",
            "module docs", "pydoc", "check for updates", "reset preferences", "faq", "support center", "install additional tools" };
        static readonly string[] junkExt = { ".url", ".chm", ".txt", ".htm", ".html", ".pdf", ".rtf", ".ini", ".log", ".xml", ".md", ".msi", ".msc" };
        // Admin utilities that the single "Windows Tools" entry already collects.
        // ponytail: English names only; the target-based rules below are language-independent
        static readonly HashSet<string> systemTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Character Map", "Component Services", "Computer Management", "Defragment and Optimize Drives", "Disk Cleanup", "Event Viewer",
            "iSCSI Initiator", "ODBC Data Sources (32-bit)", "ODBC Data Sources (64-bit)", "Performance Monitor", "Recovery Drive", "Registry Editor",
            "Resource Monitor", "Services", "Steps Recorder", "System Configuration", "System Information", "Task Scheduler", "Print Management",
            "Windows Defender Firewall with Advanced Security", "Windows Memory Diagnostic", "Windows PowerShell ISE", "Windows Media Player Legacy",
            "Local Security Policy", "Windows Fax and Scan", "Application Verifier (X64)", "Get Started"
        };

        public static void Refresh(bool force = false)
        {
            if (!force && (DateTime.Now - loadedAt).TotalSeconds < 60) return;
            loadedAt = DateTime.Now;
            Icons.Run(() =>
            {
                var list = Load();
                Icons.Ui.BeginInvoke(new Action(() => { All = list; if (Changed != null) Changed(); }));
            });
        }

        static List<AppEntry> Load()
        {
            var result = new List<AppEntry>();
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
            dynamic items = shell.NameSpace("shell:::{4234d49b-0245-4df3-b780-3893943456e1}").Items();
            int n = items.Count;
            for (int i = 0; i < n; i++)
            {
                try
                {
                    dynamic it = items.Item(i);
                    string name = it.Name, id = it.Path;
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(id)) continue;
                    string exe = null;
                    try { exe = it.ExtendedProperty("System.Link.TargetParsingPath") as string; } catch { }
                    if (string.IsNullOrEmpty(exe)) exe = ExpandKnownFolder(id);
                    if (Clutter(name, id, exe)) continue;
                    if (exe != null && !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe = null;
                    string pkg = null;
                    try { pkg = it.ExtendedProperty("System.AppUserModel.PackageInstallPath") as string; } catch { }
                    result.Add(new AppEntry { Name = name, Id = id, Exe = exe, Package = string.IsNullOrEmpty(pkg) ? null : pkg });
                }
                catch { }
            }
            Marshal.ReleaseComObject(shell);
            return result.GroupBy(a => a.Name).Select(g => g.First()).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        // Start-menu entries that aren't really apps: links, folders, installers, admin consoles, dev shells, WSL's Linux GUI apps.
        public static bool Clutter(string name, string id, string target)
        {
            target = (target ?? "").Trim('"');
            string n = name.ToLowerInvariant(), t = target.ToLowerInvariant();
            Func<string, string> ext = s => { try { return Path.GetExtension(s).ToLowerInvariant(); } catch { return ""; } };
            string file = t.Substring(t.LastIndexOf('\\') + 1);
            if (junk.Any(j => n.Contains(j)) || n.StartsWith("about ") || junkExt.Contains(ext(id)) || junkExt.Contains(ext(t))) return true;
            if (t.StartsWith("http:") || t.StartsWith("https:") || t.Contains(@"\windows kits\")) return true;
            if (file == "wslg.exe") return true;
            if ((file == "cmd.exe" || file == "powershell.exe" || file == "pwsh.exe") && name != "Command Prompt" && name != "Windows PowerShell") return true;
            if (file == "explorer.exe" && name != "File Explorer") return true;
            if (n.EndsWith("(x86)") || n.EndsWith("(wow)") || n.EndsWith("(administrator)") || n.EndsWith(" installer")) return true;
            if (systemTools.Contains(name) || id == "Microsoft.Windows.Shell.RunDialog") return true;
            // folder shortcuts, and shortcuts whose target has since been deleted
            try { return t.Length > 3 && t[1] == ':' && t[2] == '\\' &&(Directory.Exists(target) || !File.Exists(target)); } catch { return false; }
        }

        // "{6D809377-...}\Foo\foo.exe" -> "C:\Program Files\Foo\foo.exe"
        public static string ExpandKnownFolder(string id)
        {
            if (id.Length > 2 && id[1] == ':' ) return id;
            if (!id.StartsWith("{") || id.IndexOf('}') != 37) return null;
            Guid g;
            if (!Guid.TryParse(id.Substring(0, 38), out g)) return null;
            string root = Native.KnownFolder(g);
            return root == null ? null : root + id.Substring(38);
        }

        // The largest logo file a packaged app ships for its app-list icon (Square44x44Logo), e.g.
        // Assets\PhotosAppList.targetsize-256_altform-unplated.png. Null when the app isn't packaged or keeps
        // its logos only inside resources.pri (the shell's icon is used then).
        public static string PackageLogo(string id)
        {
            var e = ById(id);
            if (e == null || e.Package == null) return null;
            int bang = id.IndexOf('!');
            string appId = bang >= 0 ? id.Substring(bang + 1) : null;
            try
            {
                var doc = new System.Xml.XmlDocument();
                doc.Load(Path.Combine(e.Package, "AppxManifest.xml"));
                string logo = null;
                foreach (System.Xml.XmlElement app in doc.GetElementsByTagName("*"))
                {
                    if (app.LocalName != "Application" || (appId != null && app.GetAttribute("Id") != appId)) continue;
                    foreach (System.Xml.XmlElement ve in app.GetElementsByTagName("*"))
                        if (ve.LocalName == "VisualElements") { logo = ve.GetAttribute("Square44x44Logo"); break; }
                    break;
                }
                if (string.IsNullOrEmpty(logo)) return null;
                string full = Path.Combine(e.Package, logo), dir = Path.GetDirectoryName(full), stem = Path.GetFileNameWithoutExtension(full);
                string want = Theme.Dark ? "altform-unplated" : "altform-lightunplated"; // transparent, made for this background
                string best = null; int bestScore = -1;
                foreach (var f in Directory.EnumerateFiles(dir, stem + ".targetsize-*.png"))
                {
                    string q = Path.GetFileNameWithoutExtension(f).Substring(stem.Length + 1);   // "targetsize-256_altform-unplated"
                    if (q.Contains("contrast-")) continue;
                    var parts = q.Split('_');
                    int n;
                    if (!int.TryParse(parts[0].Substring("targetsize-".Length), out n)) continue;
                    int score = n * 3 + (parts.Length == 2 && parts[1] == want ? 2 : parts.Length == 1 ? 1 : 0);   // biggest first, then best variant
                    if (score > bestScore) { best = f; bestScore = score; }
                }
                return best != null && bestScore >= 96 * 3 ? best : null;   // only worth it if it's bigger than what the shell gives
            }
            catch { return null; }
        }

        public static AppEntry ById(string id) { return All.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)); }
        public static AppEntry ByExe(string exe) { return exe == null ? null : All.FirstOrDefault(a => string.Equals(a.Exe, exe, StringComparison.OrdinalIgnoreCase)); }

        public static string FriendlyName(string exe)
        {
            var a = ByExe(exe);
            if (a != null) return a.Name;
            try
            {
                var v = FileVersionInfo.GetVersionInfo(exe);
                if (!string.IsNullOrWhiteSpace(v.FileDescription) && v.FileDescription.Length < 40) return v.FileDescription.Trim();
                if (!string.IsNullOrWhiteSpace(v.ProductName)) return v.ProductName.Trim();
            }
            catch { }
            string n = Path.GetFileNameWithoutExtension(exe ?? "App");
            return n.Length > 0 ? char.ToUpper(n[0]) + n.Substring(1) : n;
        }
    }

    class WinInfo
    {
        public IntPtr Hwnd; public uint Pid; public string Title, Exe, Aumid;
        // Store apps all live in ApplicationFrameHost, so they're told apart by AppUserModelID.
        public string Key { get { return !string.IsNullOrEmpty(Aumid) && IsFrameHost ? Aumid : Exe; } }
        public bool IsFrameHost { get { return Exe != null && Exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase); } }
    }

    static class Windows
    {
        static readonly Dictionary<uint, string> exeByPid = new Dictionary<uint, string>();
        static readonly Dictionary<IntPtr, string> aumidByHwnd = new Dictionary<IntPtr, string>();
        static readonly HashSet<string> shellClasses = new HashSet<string> { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow" };
        static readonly uint myPid = (uint)Process.GetCurrentProcess().Id;

        // Same rules Alt+Tab uses: visible, unowned, not a tool window, not cloaked.
        public static List<WinInfo> Enumerate()
        {
            var list = new List<WinInfo>();
            var seen = new HashSet<IntPtr>();
            Native.EnumWindows((h, l) =>
            {
                if (!Native.IsWindowVisible(h)) return true;
                int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
                bool appWin = (ex & Native.WS_EX_APPWINDOW) != 0;
                if (!appWin && (Native.GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero || (ex & (Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE)) != 0)) return true;
                if (Native.GetWindowTextLength(h) == 0 || Native.IsCloaked(h)) return true;
                if (shellClasses.Contains(Native.ClassName(h))) return true;
                uint pid; Native.GetWindowThreadProcessId(h, out pid);
                if (pid == myPid) return true;
                string exe;
                if (!exeByPid.TryGetValue(pid, out exe)) exeByPid[pid] = exe = Native.ProcessPath(pid);
                if (exe == null) return true;
                string aumid;
                if (!aumidByHwnd.TryGetValue(h, out aumid)) aumidByHwnd[h] = aumid = Native.WindowAumid(h) ?? Native.ProcessAumid(pid);
                seen.Add(h);
                list.Add(new WinInfo { Hwnd = h, Pid = pid, Title = Native.WindowText(h), Exe = exe, Aumid = aumid });
                return true;
            }, IntPtr.Zero);
            foreach (var h in aumidByHwnd.Keys.Where(k => !seen.Contains(k)).ToList()) aumidByHwnd.Remove(h);
            if (exeByPid.Count > 500) exeByPid.Clear();
            return list;
        }

        public static string ExeOf(IntPtr h)
        {
            uint pid; Native.GetWindowThreadProcessId(h, out pid);
            string exe;
            if (!exeByPid.TryGetValue(pid, out exe)) exeByPid[pid] = exe = Native.ProcessPath(pid);
            return exe;
        }
    }

    static class Launcher
    {
        // target is "app:<AppsFolder id>" or a file/folder path.
        public static void Open(string target, string args = null)
        {
            try
            {
                if (target.StartsWith("app:"))
                {
                    var app = Apps.ById(target.Substring(4));
                    if (args != null && app != null && app.Exe != null) Process.Start(app.Exe, args);
                    else Process.Start("explorer.exe", "shell:AppsFolder\\" + target.Substring(4));
                }
                else if (args != null) Process.Start(new ProcessStartInfo(target, args) { UseShellExecute = true });
                else Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "OpenDock"); }
        }

        public static void Shell(string uri) { try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { } }

        public static string Quote(IEnumerable<string> files) { return string.Join(" ", files.Select(f => "\"" + f + "\"")); }

        public static void Recycle(IEnumerable<string> paths)
        {
            foreach (var p in paths)
            {
                try
                {
                    if (Directory.Exists(p)) Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(p, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    else Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(p, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { MessageBox.Show(ex.Message, "OpenDock"); }
            }
        }

        public static readonly string Downloads = Native.KnownFolder(new Guid("374DE290-123F-4565-9164-39C4925E467B")) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public const string RecycleBin = "::{645FF040-5081-101B-9F08-00AA002F954E}";

        public static bool TrashFull()
        {
            var info = new Native.SHQUERYRBINFO { cbSize = Marshal.SizeOf(typeof(Native.SHQUERYRBINFO)) };
            return Native.SHQueryRecycleBin(null, ref info) == 0 && info.i64NumItems > 0;
        }
    }

    // The desktop wallpaper, pre-blurred: the dock draws it behind itself for a frosted-glass look
    // without depending on undocumented DWM blur APIs.
    static class Wallpaper
    {
        public static BitmapSource Blurred;
        public static bool TopIsLight;
        public static event Action Changed;

        public static void Load()
        {
            double sw = SystemParameters.PrimaryScreenWidth, sh = SystemParameters.PrimaryScreenHeight;
            Icons.Run(() =>
            {
                int w = (int)(sw / 6), h = (int)(sh / 6);
                var sb = new StringBuilder(520);
                Native.SystemParametersInfo(0x73 /* SPI_GETDESKWALLPAPER */, 520, sb, 0);
                string path = sb.ToString();
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(new SolidColorBrush(DesktopColor()), null, new Rect(0, 0, w, h));
                    if (File.Exists(path))
                    {
                        try
                        {
                            var bi = new BitmapImage();
                            bi.BeginInit();
                            bi.CacheOption = BitmapCacheOption.OnLoad;
                            bi.UriSource = new Uri(path);
                            bi.DecodePixelWidth = Math.Max(w * 2, 64);
                            bi.EndInit();
                            dc.DrawImage(bi, Fit(bi.PixelWidth, bi.PixelHeight, w, h));
                        }
                        catch { }
                    }
                }
                var flat = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                flat.Render(dv);
                // Menu bar text colour follows the brightness of the wallpaper behind it, like macOS.
                var px = new byte[w * 4 * Math.Max(3, h / 30)];
                flat.CopyPixels(new Int32Rect(0, 0, w, px.Length / (w * 4)), px, w * 4, 0);
                double lum = 0;
                for (int i = 0; i < px.Length; i += 4) lum += 0.0722 * px[i] + 0.7152 * px[i + 1] + 0.2126 * px[i + 2];
                bool light = lum / (px.Length / 4) > 165;
                var blurred = Icons.Blur(flat, w, h, 10);   // heavy frost: shapes and colour show through, no detail
                Icons.Ui.BeginInvoke(new Action(() => { Blurred = blurred; TopIsLight = light; if (Changed != null) Changed(); }));
            });
        }

        static Rect Fit(double iw, double ih, double w, double h)
        {
            string style = (Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WallpaperStyle", "10") ?? "10").ToString();
            if (style == "2") return new Rect(0, 0, w, h);                                        // stretch
            double s = style == "6" ? Math.Min(w / iw, h / ih) : Math.Max(w / iw, h / ih);       // fit : fill (default)
            return new Rect((w - iw * s) / 2, (h - ih * s) / 2, iw * s, ih * s);
        }

        static Color DesktopColor()
        {
            try
            {
                var parts = ((string)Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Colors", "Background", "0 0 0")).Split(' ');
                return Color.FromRgb(byte.Parse(parts[0]), byte.Parse(parts[1]), byte.Parse(parts[2]));
            }
            catch { return Colors.Black; }
        }
    }
}
