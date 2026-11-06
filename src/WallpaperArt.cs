using System;
using System.IO;
using System.Net;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenDock
{
    // OpenDock's own wallpapers: 4K scenes rendered in Blender (Cycles) for OpenDock -- original artwork, free to use
    // with it. The pictures live in the repository's wallpapers folder (rendered by wallpapers/render.py). A copy of
    // OpenDock running from the project finds them next to it; any other copy fetches the one you pick from GitHub
    // the first time. Picking one copies it into your wallpaper library.
    static class WallpaperArt
    {
        public static readonly string[] Names = { "Dune", "Drift", "Fold", "Strata", "Flux", "Liquid", "Glass" };

        // Most come in light and dark; Flux (liquid chrome) only works on black.
        public static bool Has(string name, bool dark) { return dark || name != "Flux"; }
        const string Online = "https://raw.githubusercontent.com/leemadov/OpenDock/main/wallpapers/";

        static string FileName(string name, bool dark) { return name.ToLowerInvariant() + "-" + (dark ? "dark" : "light") + ".jpg"; }

        // Where the picture sits in your library once picked.
        public static string FileFor(string name, bool dark) { return Path.Combine(Settings.WallpaperDir, "OpenDock " + name + (dark ? " Dark" : " Light") + ".jpg"); }

        // The wallpapers folder that ships with OpenDock (next to the exe, or the project's when run from bin\).
        static string Local(string relative)
        {
            string here = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var dir in new[] { Path.Combine(here, "wallpapers"), Path.Combine(here, "..", "wallpapers") })
            {
                string f = Path.GetFullPath(Path.Combine(dir, relative));
                if (File.Exists(f)) return f;
            }
            return null;
        }

        // A small preview for the library; null if it isn't here yet (it's fetched along with the picture).
        public static ImageSource Preview(string name, bool dark)
        {
            string f = File.Exists(FileFor(name, dark)) ? FileFor(name, dark) : Local(Path.Combine("thumbs", FileName(name, dark))) ?? Local(FileName(name, dark));
            if (f == null) return null;
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.DecodePixelWidth = 280; bi.UriSource = new Uri(f); bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch { return null; }
        }

        // The picture in your library (copied there, or downloaded once). Runs on a background thread.
        public static string Make(string name, bool dark)
        {
            string dest = FileFor(name, dark);
            if (File.Exists(dest)) return dest;
            Directory.CreateDirectory(Settings.WallpaperDir);
            string tmp = dest + ".part";
            string local = Local(FileName(name, dark));
            if (local != null) File.Copy(local, tmp, true);
            else
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                using (var wc = new WebClient()) { wc.Headers[HttpRequestHeader.UserAgent] = "OpenDock"; wc.DownloadFile(Online + FileName(name, dark), tmp); }
            }
            if (File.Exists(dest)) File.Delete(tmp); else File.Move(tmp, dest);
            return dest;
        }
    }
}
