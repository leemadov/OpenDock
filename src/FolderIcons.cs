using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenDock
{
    // Custom folder icons that show up in Windows itself -- on the desktop, in Explorer, in the file dialogs -- not just
    // in Finder: a picture you choose, or the macOS-style folder Finder draws (its colour and symbol).
    //
    // It's Windows' own mechanism: a folder with a desktop.ini naming an icon. The icon files live in GlassDock's own
    // data folder (nothing is added to your folders but the small hidden desktop.ini), every folder done is listed
    // in foldericons.txt, and only a desktop.ini GlassDock wrote (it says so inside) is ever changed or removed.
    static class FolderIcons
    {
        const string Marker = "; GlassDock folder icon";
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern void SHChangeNotify(int e, uint flags, string a, IntPtr b);

        static string IconDir { get { return Path.Combine(Settings.DataDir, "FolderIcons"); } }
        static string List { get { return Path.Combine(Settings.DataDir, "foldericons.txt"); } }

        // Did GlassDock give this folder its icon?
        public static bool Has(string folder)
        {
            try
            {
                string ini = Path.Combine(folder, "desktop.ini");
                return File.Exists(ini) && File.ReadAllText(ini).Contains(Marker);
            }
            catch { return false; }
        }

        // A picture of your own (png, jpg, bmp, ico) as the folder's icon.
        public static void SetImage(string folder, string imageFile)
        {
            var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".png");
            try
            {
                CustomIcons.Normalize(imageFile, temp);
                var bmp = new BitmapImage(); bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.UriSource = new Uri(temp); bmp.EndInit(); bmp.Freeze();
                Write(folder, Square(bmp, 256));
            }
            finally { try { File.Delete(temp); } catch { } }
        }

        // The folder as Finder draws it (its colour tag and symbol), for Windows too.
        public static void SetFinderLook(string folder)
        {
            var img = FolderLook.Draw(FolderLook.ColorFor(folder), FolderLook.SymbolOf(folder), Theme.Dark, 256);
            Write(folder, img);
        }

        public static void Reset(string folder)
        {
            try
            {
                if (!Has(folder)) return;
                string ini = Path.Combine(folder, "desktop.ini");
                File.SetAttributes(ini, FileAttributes.Normal);
                File.Delete(ini);
                // the folder's own read-only flag was only set so Windows would read the desktop.ini
                var di = new DirectoryInfo(folder); di.Attributes &= ~FileAttributes.ReadOnly;
                Forget(folder);
                SHChangeNotify(0x00002000 /* SHCNE_UPDATEDIR */, 0x0005 /* SHCNF_PATH | FLUSH */, folder, IntPtr.Zero);
            }
            catch (Exception ex) { App.Log(ex); }
        }

        // Every folder GlassDock changed goes back to Windows' own icon.
        public static void ResetAll()
        {
            foreach (var f in Folders()) Reset(f);
        }

        // With "Folder icons in Windows too" on, a folder's colour or symbol set in Finder is applied to Windows as well.
        public static void Follow(string folder)
        {
            if (!Settings.FolderIconsInWindows || folder == null || !Directory.Exists(folder)) return;
            try { SetFinderLook(folder); } catch (Exception ex) { App.Log(ex); }
        }

        static void Write(string folder, BitmapSource image)
        {
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("That folder isn't there any more.");
            string ini = Path.Combine(folder, "desktop.ini");
            if (File.Exists(ini) && !Has(folder)) throw new InvalidOperationException("This folder already has its own desktop.ini (a custom icon or settings from another program), so it was left as it is.");
            Directory.CreateDirectory(IconDir);
            string icon = Path.Combine(IconDir, Math.Abs(Path.GetFullPath(folder).ToLowerInvariant().GetHashCode()) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".ico");
            File.WriteAllBytes(icon, Ico(image));
            if (File.Exists(ini)) File.SetAttributes(ini, FileAttributes.Normal);
            string was = File.Exists(ini) ? ReadIcon(ini) : null;
            File.WriteAllText(ini, "[.ShellClassInfo]\r\nIconResource=" + icon + ",0\r\n" + Marker + "\r\n", Encoding.Unicode);
            File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);
            var di = new DirectoryInfo(folder); di.Attributes |= FileAttributes.ReadOnly;   // Windows only reads a desktop.ini in a read-only folder
            if (was != null) try { File.Delete(was); } catch { }
            Remember(folder);
            SHChangeNotify(0x00002000 /* SHCNE_UPDATEDIR */, 0x0005, folder, IntPtr.Zero);
            SHChangeNotify(0x00001000 /* SHCNE_UPDATEITEM */, 0x0005, folder, IntPtr.Zero);
        }

        static string ReadIcon(string ini)
        {
            try
            {
                foreach (var line in File.ReadAllLines(ini))
                    if (line.StartsWith("IconResource=", StringComparison.OrdinalIgnoreCase))
                    {
                        string p = line.Substring(13); int comma = p.LastIndexOf(',');
                        if (comma > 0) p = p.Substring(0, comma);
                        return p.StartsWith(IconDir, StringComparison.OrdinalIgnoreCase) ? p : null;   // only ever our own file
                    }
            }
            catch { }
            return null;
        }

        // The picture centred on a transparent square, `size` px.
        static BitmapSource Square(BitmapSource src, int size)
        {
            double k = Math.Min((double)size / src.PixelWidth, (double)size / src.PixelHeight);
            double w = src.PixelWidth * k, h = src.PixelHeight * k;
            var dv = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
            using (var dc = dv.RenderOpen()) dc.DrawImage(src, new Rect((size - w) / 2, (size - h) / 2, w, h));
            var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32); rtb.Render(dv); rtb.Freeze();
            return rtb;
        }

        // An .ico holding one PNG (Windows Vista and later read it, at any size up to 256).
        static byte[] Ico(BitmapSource image)
        {
            var sq = image.PixelWidth == 256 && image.PixelHeight == 256 ? image : Square(image, 256);
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(sq));
            byte[] png; using (var ms = new MemoryStream()) { enc.Save(ms); png = ms.ToArray(); }
            using (var o = new MemoryStream())
            using (var w = new BinaryWriter(o))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)1);                       // ICONDIR: one image
                w.Write((byte)0); w.Write((byte)0); w.Write((byte)0); w.Write((byte)0);        // 256 x 256 (0 = 256), no palette
                w.Write((short)1); w.Write((short)32); w.Write(png.Length); w.Write(22);       // planes, bits, size, offset
                w.Write(png);
                return o.ToArray();
            }
        }

        static void Remember(string folder)
        {
            var all = Folders(); if (!all.Contains(folder, StringComparer.OrdinalIgnoreCase)) all.Add(folder);
            try { File.WriteAllLines(List, all.ToArray(), new UTF8Encoding(false)); } catch { }
        }

        static void Forget(string folder)
        {
            var all = Folders().Where(f => !string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)).ToList();
            try { if (all.Count == 0) File.Delete(List); else File.WriteAllLines(List, all.ToArray(), new UTF8Encoding(false)); } catch { }
        }

        static List<string> Folders()
        {
            try { return File.Exists(List) ? File.ReadAllLines(List).Where(l => l.Length > 0).ToList() : new List<string>(); }
            catch { return new List<string>(); }
        }
    }
}
